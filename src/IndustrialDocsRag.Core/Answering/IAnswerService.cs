namespace IndustrialDocsRag.Core.Answering;

/// <summary>
/// 端到端问答。
/// <para>
/// Week1 的实现是固定流水线（检索 → 填 prompt → 生成）；Week2 增加 function calling 版本，
/// 由 LLM 自主决定检索时机与查询词。两者共用这个接口，同时也构成 Week3 的一条实验维度。
/// </para>
/// </summary>
public interface IAnswerService
{
    /// <summary>写进评测报告的标识，例 <c>fixed-pipeline</c>、<c>function-calling</c>。</summary>
    string Name { get; }

    /// <summary>
    /// 提问并流式返回过程事件。
    /// 一次正常的调用会依次产生：RetrievalStarted → RetrievalCompleted → 若干 TextDelta → Completed。
    /// </summary>
    IAsyncEnumerable<AnswerEvent> AskAsync(string question, CancellationToken ct = default);
}
