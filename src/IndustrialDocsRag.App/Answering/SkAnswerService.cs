using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.App.Plugins;
using IndustrialDocsRag.Core.Answering;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace IndustrialDocsRag.App.Answering;

/// <summary>
/// Week2 的问答实现：把检索包成 <c>search_manual</c> 交给模型，由它决定检不检索、
/// 用什么词检索、检索几次。
/// <para>
/// 与 <see cref="FixedPipelineAnswerService"/> 并存，<c>--answerer fixed|agent</c> 切换。
/// 这个开关同时是 Week3 的一条实验维度：固定检索 vs LLM 自主检索的召回率差异。
/// </para>
/// <para>
/// Week1 §7.1 把它推迟到现在是对的 —— 15 块语料下 Top-5 一次就捞走三分之一，
/// 分不清「模型改写得聪明」和「语料太小随便捞都中」。1667 块才可观测。
/// </para>
/// </summary>
public sealed class SkAnswerService(
    RagOptions options,
    IRetriever retriever,
    ITermNormalizer normalizer,
    Func<Kernel> kernelFactory) : IAnswerService
{
    public string Name => "sk-function-calling";

    public async IAsyncEnumerable<AnswerEvent> AskAsync(
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ★ 每次问答新建 Kernel 与插件实例。插件是 Kernel 级的，复用 Kernel 会让
        // 上一轮的召回集污染下一轮 —— Week4 的 WPF 多轮对话必然踩到。SK 的 Kernel 很轻。
        var kernel = kernelFactory();
        var plugin = new ManualSearchPlugin(retriever, normalizer, options.TopK);

        // 工具调用发生在 SK 内部，事件要从回调里穿出来到 IAsyncEnumerable，
        // 用一个 unbounded channel 转接。
        var queries = Channel.CreateUnbounded<string>();
        plugin.QueryIssued += q => queries.Writer.TryWrite(q);

        kernel.Plugins.AddFromObject(plugin, "manual");

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);
        history.AddUserMessage(question);

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        };

        var answer = new StringBuilder();

        await using var stream = chat
            .GetStreamingChatMessageContentsAsync(history, settings, kernel, ct)
            .GetAsyncEnumerator(ct);

        while (true)
        {
            string? delta = null;
            var finished = false;
            Exception? failure = null;

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

            // 先把这一步里模型发起的检索作为事件吐出去，再吐文本 ——
            // 一轮问答出现多个 RetrievalStarted，正是 Week1 保留这个事件的理由。
            while (queries.Reader.TryRead(out var q))
            {
                yield return new AnswerEvent.RetrievalStarted(q);
            }

            if (failure is not null)
            {
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

        while (queries.Reader.TryRead(out var trailing))
        {
            yield return new AnswerEvent.RetrievalStarted(trailing);
        }

        // 累加起来的整轮召回集，供引用校验求交集
        IReadOnlyList<RetrievedChunk> recalled = plugin.Recalled;
        yield return new AnswerEvent.RetrievalCompleted(recalled);

        var extraction = CitationExtractor.Extract(answer.ToString(), recalled);
        yield return new AnswerEvent.Completed(extraction.Citations, extraction.RejectedIds);
    }

    /// <summary>
    /// 与 <c>Prompts/answer.md</c> 的三条核心约束一致，只是把「上下文已给你」换成「自己去查」。
    /// </summary>
    private const string SystemPrompt = """
        你是工业伺服驱动器手册的问答助手。

        规则：
        1. 必须调用 search_manual 检索手册后再作答，禁止依赖你自己的知识。
           一个问题涉及多个方面时，可以多次调用，每次换一个更贴近手册措辞的检索词。
        2. 每个事实性陈述后必须紧跟它的出处编号，格式为 [chunk-id]，照抄检索结果里给出的编号。
        3. 检索结果里没有的内容，明确回答"手册中未找到相关内容"，不要推测、不要补充通用知识。

        检索词要用手册里会出现的说法：故障码写 E201.2，参数写 H06-08，
        对象字典索引写 6041（手册的索引列不带 0x 前缀）。
        """;
}
