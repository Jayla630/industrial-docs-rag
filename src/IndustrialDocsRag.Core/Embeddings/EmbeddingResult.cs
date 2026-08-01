namespace IndustrialDocsRag.Core.Embeddings;

/// <summary>
/// 一次嵌入调用的结果。
/// <para>
/// <paramref name="Sparse"/> 为 null 表示当前嵌入服务不产出稀疏向量。
/// 远程的 OpenAI 兼容端点大多如此 —— <c>/v1/embeddings</c> 的响应格式里没有装稀疏权重的位置，
/// 哪怕底层模型（如 BGE-M3）本身能产出。此时混合检索的稀疏侧由 <see cref="ISparseEncoder"/> 兜底。
/// </para>
/// </summary>
public sealed record EmbeddingResult(float[] Dense, SparseVector? Sparse);
