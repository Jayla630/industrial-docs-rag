namespace IndustrialDocsRag.Core.Embeddings;

/// <summary>把文本变成向量。实现可以是远程 API、本地推理服务，或开发期用的确定性伪向量。</summary>
public interface IEmbeddingService
{
    /// <summary>写进评测报告的标识，例 <c>bge-m3-remote</c>、<c>fake-hash</c>。</summary>
    string Name { get; }

    /// <summary>稠密向量维度。BGE-M3 是 1024。</summary>
    int DenseDimension { get; }

    /// <summary>本实现是否同时产出稀疏向量。false 时调用方需要用 <see cref="ISparseEncoder"/> 兜底。</summary>
    bool ProducesSparse { get; }

    /// <summary>
    /// 查询侧嵌入。
    /// <para>
    /// 与文档侧分开有两个理由：一是批量策略不同（查询永远是单条，文档要分批）；
    /// 二是不少嵌入模型的查询侧需要 instruction 前缀（BGE-M3 不需要，但换模型时不想改签名）。
    /// </para>
    /// </summary>
    Task<EmbeddingResult> EmbedQueryAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 文档侧批量嵌入。返回顺序必须与 <paramref name="texts"/> 一一对应。
    /// 实现负责内部分批，调用方只管一次性把整批丢进来。
    /// </summary>
    Task<IReadOnlyList<EmbeddingResult>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default);
}
