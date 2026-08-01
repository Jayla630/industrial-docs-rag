using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Core.Answering;

/// <summary>
/// 一次问答过程中产生的事件。
/// <para>
/// 做成事件流而不是一次性返回结果，是为了 Week4 的 WPF：界面要能实时显示「正在检索…」、
/// 逐字吐出答案、最后再渲染引用列表。Week1 的控制台用的是同一套事件，只是渲染方式简单。
/// </para>
/// </summary>
public abstract record AnswerEvent
{
    // 私有构造函数把继承封闭在本类型内部，穷举 switch 时不会漏分支。
    private AnswerEvent() { }

    /// <summary>检索开始。</summary>
    /// <param name="QueryText">
    /// Week1 是规范化后的原始问题。
    /// Week2 接入 function calling 之后，这里会变成 LLM 自己改写的检索词，且一轮问答可能触发多次
    /// （例如把「6041 报警怎么处理」拆成两次检索）。
    /// 事件结构不变、消费方不用改 —— 这正是它在 Week1 就存在的价值，尽管现在看起来很无聊。
    /// </param>
    public sealed record RetrievalStarted(string QueryText) : AnswerEvent;

    /// <summary>检索完成，携带本轮召回集。引用校验就是拿答案里的 id 和这批求交集。</summary>
    public sealed record RetrievalCompleted(IReadOnlyList<RetrievedChunk> Hits) : AnswerEvent;

    /// <summary>模型吐出的一段文本增量。</summary>
    public sealed record TextDelta(string Text) : AnswerEvent;

    /// <summary>正常结束。</summary>
    /// <param name="Citations">已与本轮召回集求过交集、确认真实存在的引用。</param>
    /// <param name="RejectedIds">
    /// 答案里出现过、但不在召回集里的编号 —— 即模型编造的引用。
    /// 不静默丢弃而是报出来：引用幻觉率是衡量 RAG 可信度的一个直接指标，
    /// Week3 的评测要统计它。数量为 0 才说明模型老实。
    /// </param>
    public sealed record Completed(
        IReadOnlyList<Citation> Citations,
        IReadOnlyList<string> RejectedIds) : AnswerEvent;

    /// <summary>
    /// 失败。
    /// <para>
    /// 流式场景下异常可能发生在迭代中途，那时已经吐出去的 <see cref="TextDelta"/> 收不回来，
    /// 只能补发一个 Failed，由消费方决定怎么呈现（控制台打红字，WPF 在气泡下方挂错误条）。
    /// </para>
    /// </summary>
    public sealed record Failed(string Message, Exception? Error = null) : AnswerEvent;
}
