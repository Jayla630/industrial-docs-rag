namespace IndustrialDocsRag.Core.Embeddings;

/// <summary>
/// 当 <see cref="IEmbeddingService.ProducesSparse"/> 为 false 时的稀疏向量兜底编码器。
/// Week1 只有 NullSparseEncoder，Week2 实现 BM25 版本。
/// </summary>
public interface ISparseEncoder
{
    /// <summary>写进评测报告的标识，例 <c>bm25-jieba</c>、<c>none</c>。</summary>
    string Name { get; }

    /// <summary>文档侧编码。BM25 下这一侧要做文档长度归一化。</summary>
    SparseVector EncodeDocument(string text);

    /// <summary>查询侧编码。BM25 下这一侧只用 IDF，公式与文档侧不同。</summary>
    SparseVector EncodeQuery(string text);
}
