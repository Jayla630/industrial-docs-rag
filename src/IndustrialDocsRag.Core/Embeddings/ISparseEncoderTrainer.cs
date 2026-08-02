namespace IndustrialDocsRag.Core.Embeddings;

/// <summary>
/// 需要先看过整个语料才能编码的稀疏编码器。
/// <para>
/// BM25 与 <see cref="IEmbeddingService"/> 的本质区别就在这里：稠密嵌入是逐条独立的，
/// 而 BM25 的权重依赖全语料统计量（df / N / avgdl），必须先 fit 再 encode。
/// </para>
/// <para>
/// 单独立一个接口而不是往 <see cref="ISparseEncoder"/> 上加方法：
/// <c>NullSparseEncoder</c> 和将来的学习式稀疏编码器都不需要训练，
/// 让它们去实现一个空的 Fit 是污染。
/// </para>
/// </summary>
public interface ISparseEncoderTrainer
{
    /// <summary>用全语料统计词表、df、平均长度。必须在任何编码之前调用一次。</summary>
    void Fit(IReadOnlyList<string> documents);
}
