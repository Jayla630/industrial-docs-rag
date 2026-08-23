namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>
/// 混合检索的降级判据。
/// <para>
/// 叫 Signal 不叫 Warning 是刻意的：三条里只有 <see cref="SingleSideEmpty"/> 无歧义，
/// 另外两条的触发率要拿 25 题评测集实测之后才定级。提前命名成 Warning 就等于提前定了级，
/// 而一个天天触发的警告等于没有警告 —— 那本身就是一种失效。
/// </para>
/// </summary>
public enum RetrievalSignalKind
{
    /// <summary>某一路 0 结果，融合退化成单路。无歧义的失效。</summary>
    SingleSideEmpty,

    /// <summary>两路都非空，但融合后的 Top-K 里某一路一条都没有。</summary>
    FusedFromOneSideOnly,

    /// <summary>两路候选零交集，各找各的。</summary>
    NoCandidateOverlap,
}

/// <summary>
/// 一条判据。成员全是 primitive，结构相等成立，测试可直接断言整个 record。
/// <para>
/// <see cref="Message"/> 不得包含换行：下游按行 <c>grep '^SIGNAL:'</c> 统计触发率，
/// 多一个换行就会把一条判据算成两条。目前的实现只拼固定文案和计数，不含 chunk 文本，
/// 天然满足；往判据里塞 chunk 摘要之类的自由文本时要留意这条约束。
/// </para>
/// </summary>
public sealed record RetrievalSignal(RetrievalSignalKind Kind, string Message);
