using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.InMemory;
using IndustrialDocsRag.Retrieval.Sparse;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

/// <summary>
/// 只跑检索、不接生成模型，把召回结果和分数直接打出来。
/// <para>
/// 这个命令的存在是刻意的：检索和生成同时接通再调试，出问题时分不清是召回差还是 prompt 差，
/// 而那是最烧时间的一类排查。先在这里确认召回本身是对的，再去接 LLM。
/// </para>
/// </summary>
public static class RetrieveCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string indexPath,
        string sparseModelPath,
        string question,
        int topK,
        string? dumpTopKPath,
        bool explain,
        CancellationToken ct)
    {
        var options = services.GetRequiredService<RagOptions>();
        var chunkIndex = services.GetRequiredService<IChunkIndex>();

        if (options.Backend == "memory")
        {
            await services.GetRequiredService<InMemoryChunkIndex>().LoadAsync(indexPath, ct);
        }

        await SparseModelLoader.LoadIfNeededAsync(services, sparseModelPath, ct);

        var retriever = services.GetRequiredService<IRetriever>();
        var normalizer = services.GetRequiredService<ITermNormalizer>();

        // 与入库侧调用同一个 normalizer 实例，保证两侧对称
        var normalized = normalizer.Normalize(question);

        var where = options.Backend == "memory" ? RepoPaths.ToDisplay(indexPath) : $"qdrant/{options.Qdrant.Collection}";
        Console.WriteLine($"索引   : {where}  ({await chunkIndex.CountAsync(ct)} 块)");
        Console.WriteLine($"检索器 : {retriever.Name}");
        Console.WriteLine($"查询   : {normalized}");
        Console.WriteLine();

        var query = new RetrievalQuery { Text = normalized, TopK = topK };

        RetrievalTrace? trace = null;
        IReadOnlyList<RetrievedChunk> hits;

        if (explain)
        {
            if (ExplainNotSupported(retriever) is { } refusal)
            {
                Console.Error.WriteLine(refusal);
                return 1;
            }

            trace = await ((IExplainableRetriever)retriever).RetrieveWithTraceAsync(query, ct);
            hits = trace.Fused;
        }
        else
        {
            hits = await retriever.RetrieveAsync(query, ct);
        }

        if (dumpTopKPath is not null)
        {
            await Diagnostics.TopKDumper.AppendAsync(dumpTopKPath, question, retriever.Name, hits, ct);
            Console.WriteLine($"Top-K 已追加写入 {RepoPaths.ToDisplay(dumpTopKPath)}");
            Console.WriteLine();
        }

        if (trace is not null)
        {
            PrintTrace(trace, topK);
            Console.WriteLine();
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("没有召回任何块。");
            return 0;
        }

        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var page = h.Chunk.PageNumber is { } p ? $"p.{p}" : "无页码";

            Console.WriteLine($"[{i + 1}] {h.Score:F4}  {h.Chunk.Id}");
            Console.WriteLine($"     {page} | {h.Chunk.SectionPath} | {h.Chunk.Kind} | tag={h.RetrieverTag}");
            Console.WriteLine($"     {Preview(h.Chunk.Text, 90)}");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// --explain 只能用在实现了轨迹的检索器上。返回 null 表示可以继续，否则返回要打印的错误信息。
    /// <para>
    /// 不支持时必须报错退出，不能静默打一张空表 —— 空表看起来像「这次检索确实没有轨迹」，
    /// 又是一个「看起来正常」的失效。
    /// </para>
    /// </summary>
    internal static string? ExplainNotSupported(IRetriever retriever)
        => retriever is IExplainableRetriever
            ? null
            : $"--explain 只支持实现了检索轨迹的检索器（当前：{retriever.Name}）。请改用 --retriever hybrid。";

    private static void PrintTrace(RetrievalTrace trace, int topK)
    {
        PrintSide("稠密侧候选", trace.Dense, topK);
        PrintSide("稀疏侧候选", trace.Sparse, topK);

        Console.WriteLine($"── 融合结果（Top-{topK}）");
        for (var i = 0; i < trace.Fused.Count; i++)
        {
            var h = trace.Fused[i];
            Console.WriteLine($"  [{i + 1}] {h.Score:F4}  {h.Chunk.Id}  tag={h.RetrieverTag}");
        }

        Console.WriteLine();

        if (trace.Signals.Count == 0)
        {
            Console.WriteLine("SIGNAL: none");
            return;
        }

        // 前缀固定，便于批量跑 25 题时 grep 统计触发率
        foreach (var s in trace.Signals)
        {
            Console.WriteLine($"SIGNAL: {s.Kind} {s.Message}");
        }
    }

    private static void PrintSide(string title, IReadOnlyList<RetrievedChunk> hits, int topK)
    {
        Console.WriteLine(PoolLabel(title, hits.Count, topK));

        if (hits.Count == 0)
        {
            Console.WriteLine("  （空）");
            Console.WriteLine();
            return;
        }

        for (var i = 0; i < hits.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {hits[i].Score:F4}  {hits[i].Chunk.Id}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 候选池的标题行。抽成纯函数是因为它是这个功能里唯一没被单测覆盖过、
    /// 又被实测证明有问题的部分：稀疏侧候选数常年小于 TopK（BM25 只在词有交集时才有分），
    /// 「候选池 = Top-K × 倍数」这种因果表述在那种情况下会自相矛盾（3 条截不成 5 条）。
    /// <para>
    /// 真实倍数是 <c>HybridRetriever</c> 构造时的 candidateMultiplier，显示层拿不到，
    /// 写死一个数字的话，谁改了倍数这里就会安静地对不上 —— 所以干脆不提具体倍数，
    /// 只陈述两个独立事实：候选池有多少条、融合结果截到多少条。
    /// </para>
    /// </summary>
    internal static string PoolLabel(string title, int count, int topK)
        => $"── {title}（{count} 条 · 融合前的候选池；下方融合结果截断到 Top-{topK}）";

    private static string Preview(string text, int maxLength)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= maxLength ? flat : string.Concat(flat.AsSpan(0, maxLength), "…");
    }
}
