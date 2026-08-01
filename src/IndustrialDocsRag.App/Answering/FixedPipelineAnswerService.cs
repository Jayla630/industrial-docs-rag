using System.Runtime.CompilerServices;
using System.Text;
using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.App.Orchestration;
using IndustrialDocsRag.Core.Answering;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace IndustrialDocsRag.App.Answering;

/// <summary>
/// Week1 的问答实现：固定流水线，检索 → 填 prompt → 流式生成。
/// <para>
/// 检索时机和查询词都是写死的。Week2 会加一个 function calling 版本，让模型自己决定
/// 检不检索、用什么词检索 —— 但那要等语料上千块之后才可观测：现在只有 15 块，
/// Top-5 一次就捞走三分之一，分不清"模型改写得聪明"和"语料太小随便捞都中"。
/// </para>
/// </summary>
public sealed class FixedPipelineAnswerService(
    Kernel kernel,
    IRetriever retriever,
    ITermNormalizer normalizer,
    RagOptions options) : IAnswerService
{
    public string Name => "fixed-pipeline";

    public async IAsyncEnumerable<AnswerEvent> AskAsync(
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 与入库侧调用同一个 normalizer 实例，保证两侧对称
        var normalized = normalizer.Normalize(question);
        yield return new AnswerEvent.RetrievalStarted(normalized);

        // C# 的迭代器方法不允许在带 catch 的 try 块里 yield，
        // 所以把 await 和 yield 分开写：先在 try 里拿结果，出了 try 再 yield。
        IReadOnlyList<RetrievedChunk> hits = [];
        Exception? failure = null;
        try
        {
            hits = await retriever.RetrieveAsync(
                new RetrievalQuery { Text = normalized, TopK = options.TopK }, ct);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            yield return new AnswerEvent.Failed($"检索失败：{failure.Message}", failure);
            yield break;
        }

        yield return new AnswerEvent.RetrievalCompleted(hits);

        if (hits.Count == 0)
        {
            yield return new AnswerEvent.TextDelta("手册中未找到相关内容。");
            yield return new AnswerEvent.Completed([], []);
            yield break;
        }

        string systemPrompt;
        try
        {
            systemPrompt = await KernelFactory.RenderSystemPromptAsync(kernel, BuildContext(hits), ct);
        }
        catch (Exception ex)
        {
            failure = ex;
            systemPrompt = string.Empty;
        }

        if (failure is not null)
        {
            yield return new AnswerEvent.Failed($"提示词渲染失败：{failure.Message}", failure);
            yield break;
        }

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(question);

        await using var stream = chat
            .GetStreamingChatMessageContentsAsync(history, kernel: kernel, cancellationToken: ct)
            .GetAsyncEnumerator(ct);

        // 累积完整答案，供结束时提取引用标记。流式吐出去的片段本身留不住。
        var answer = new StringBuilder();

        while (true)
        {
            string? delta = null;
            var finished = false;

            try
            {
                if (await stream.MoveNextAsync())
                {
                    delta = stream.Current.Content;
                }
                else
                {
                    finished = true;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null)
            {
                // 流式场景下已经吐出去的 TextDelta 收不回来，只能补一个 Failed 让消费方决定怎么呈现
                yield return new AnswerEvent.Failed($"生成失败：{failure.Message}", failure);
                yield break;
            }

            if (finished)
            {
                break;
            }

            if (!string.IsNullOrEmpty(delta))
            {
                answer.Append(delta);
                yield return new AnswerEvent.TextDelta(delta);
            }
        }

        // 与本轮召回集求交集，丢掉模型编造的编号；被丢掉的一并报出来。
        var extraction = CitationExtractor.Extract(answer.ToString(), hits);
        yield return new AnswerEvent.Completed(extraction.Citations, extraction.RejectedIds);
    }

    /// <summary>
    /// 把召回结果拼成送进 prompt 的片段文本。
    /// 每块以 <c>[chunk-id]</c> 开头 —— 模型要靠它写引用标记，格式必须一眼可抄。
    /// </summary>
    private static string BuildContext(IReadOnlyList<RetrievedChunk> hits)
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
