namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>一次检索请求。</summary>
public sealed record RetrievalQuery
{
    /// <summary>查询文本。调用方应当先经过 ITermNormalizer 规范化再传进来。</summary>
    public required string Text { get; init; }

    public int TopK { get; init; } = 5;

    /// <summary>
    /// 分数下限。
    /// <para>
    /// Week1 不要用：BGE-M3 的稠密余弦分数分布很窄，毫不相关的文本对之间也常有 0.5~0.7，
    /// 设绝对阈值几乎必然误伤真正相关的块。等 Week3 拿到实测分布再决定是否启用。
    /// </para>
    /// </summary>
    public double? MinScore { get; init; }

    /// <summary>元数据过滤。Week2 接上 Qdrant 的 payload 索引后才有实现，Week1 忽略。</summary>
    public IReadOnlyDictionary<string, string>? Filters { get; init; }
}
