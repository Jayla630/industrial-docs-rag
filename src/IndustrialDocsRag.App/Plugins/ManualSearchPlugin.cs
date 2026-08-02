using System.ComponentModel;
using System.Text;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using Microsoft.SemanticKernel;

namespace IndustrialDocsRag.App.Plugins;

/// <summary>
/// 暴露给 LLM 的手册检索工具。模型自己决定检不检索、用什么词检索、检索几次。
/// <para>
/// ★ <b>本轮召回集必须累加，不能覆盖。</b> auto-invoke 下模型经常拆成多次调用
/// （先查故障码、再查处理方法），覆盖式暂存会让先前召回的块在引用校验时被判成幻觉。
/// 这条警告是 `plan-week1.md` §3.3 末尾在 Week1 就写下的，现在兑现。
/// </para>
/// <para>
/// ★ 另一半是<b>作用域</b>：这个实例挂在 Kernel 上，而 Kernel 若被复用，
/// 上一轮问答的召回集会污染下一轮。所以 <see cref="Answering.SkAnswerService"/>
/// 每次 AskAsync 都新建 Kernel 与本插件 —— SK 的 Kernel 很轻，别图省事复用。
/// </para>
/// </summary>
public sealed class ManualSearchPlugin(
    IRetriever retriever,
    ITermNormalizer normalizer,
    int defaultTopK)
{
    private readonly Dictionary<string, RetrievedChunk> _recalled = new(StringComparer.Ordinal);
    private readonly List<string> _queries = [];

    /// <summary>本轮问答累计召回的全部块，按首次召回顺序。引用校验用它求交集。</summary>
    public IReadOnlyList<RetrievedChunk> Recalled => [.. _recalled.Values];

    /// <summary>模型实际发起过的查询词。一轮里可能有多条 —— 这正是 function calling 的看点。</summary>
    public IReadOnlyList<string> Queries => _queries;

    /// <summary>每次工具调用触发一次，供上层发 RetrievalStarted 事件。</summary>
    public event Action<string>? QueryIssued;

    [KernelFunction("search_manual")]
    [Description("在伺服驱动器手册中检索相关内容。适合查故障码、参数号、对象字典索引、接线与调试说明。")]
    public async Task<string> SearchAsync(
        [Description("检索词。用手册里会出现的说法，例如「E201.2 辅助码」「H06-08 出厂值」「制动电阻 接线」。")]
        string query,
        CancellationToken ct = default)
    {
        var normalized = normalizer.Normalize(query);
        _queries.Add(normalized);
        QueryIssued?.Invoke(normalized);

        var hits = await retriever.RetrieveAsync(
            new RetrievalQuery { Text = normalized, TopK = defaultTopK }, ct);

        foreach (var hit in hits)
        {
            // 累加：同一块被多次召回时保留首次的名次信息，不同查询召回的块全部留下
            _recalled.TryAdd(hit.Chunk.Id, hit);
        }

        return hits.Count == 0
            ? "没有检索到相关内容。"
            : Render(hits);
    }

    /// <summary>
    /// 返回给模型的文本。每块以 <c>[chunk-id]</c> 开头 —— 模型要照抄它写引用标记，
    /// 格式必须一眼可抄，这与固定流水线那条路完全一致。
    /// </summary>
    private static string Render(IReadOnlyList<RetrievedChunk> hits)
    {
        var sb = new StringBuilder();

        foreach (var hit in hits)
        {
            var page = hit.Chunk.PageNumber is { } p ? $"p.{p}" : "无页码";
            sb.Append('[').Append(hit.Chunk.Id).Append("] (")
              .Append(page).Append(", ").Append(hit.Chunk.SectionPath).AppendLine(")");
            sb.AppendLine(hit.Chunk.Text);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
