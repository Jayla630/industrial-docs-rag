namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>
/// 一次混合检索的完整过程：两路原始候选 + 融合结果 + 降级判据。
/// <para>
/// 轨迹<b>随结果返回</b>而不是旁路收集，是因为旁路是可变状态：并发下会把 A 查询的轨迹
/// 配到 B 查询上，而失效的表现是「显示了一份看起来完全合理、却不属于这次查询的轨迹」——
/// 为一个可观测性功能引入一类新的静默失效，不划算。
/// </para>
/// <para>
/// ★ 带集合属性，测试一律断言成员、不断言整个 record（见 CLAUDE.md「测试约定」）。
/// </para>
/// </summary>
public sealed record RetrievalTrace
{
    /// <summary>规范化之后、真正送进检索的那个串，不是用户的原始输入。</summary>
    public required string Query { get; init; }

    public required string RetrieverName { get; init; }

    /// <summary>
    /// 稠密侧原始候选。长度是 <c>TopK × candidateMultiplier</c>，<b>不是 TopK</b> ——
    /// 融合要在更大的候选池上做才有意义。展示时必须标出这个倍数，否则看的人第一反应是数字对不上。
    /// </summary>
    public required IReadOnlyList<RetrievedChunk> Dense { get; init; }

    /// <summary>稀疏侧原始候选，长度口径同 <see cref="Dense"/>。</summary>
    public required IReadOnlyList<RetrievedChunk> Sparse { get; init; }

    /// <summary>融合后的最终结果，长度 ≤ TopK。</summary>
    public required IReadOnlyList<RetrievedChunk> Fused { get; init; }

    /// <summary>从上面三项推导，不额外查一次索引。</summary>
    public required IReadOnlyList<RetrievalSignal> Signals { get; init; }
}
