using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.Fusion;

/// <summary>
/// 加权分数融合：<c>α·dense_norm + (1-α)·sparse_norm</c>，两侧各自组内 min-max 归一化。
/// <para>
/// 归一化不能省：余弦分布在 0~1，BM25 分数无上界，不归一就是 BM25 单独说了算。
/// </para>
/// <para>
/// 与 <see cref="RrfFusion"/> 的区别是它<b>保留了分数的相对强弱</b>。
/// 对「某个稀有术语精确命中、分数远高于其余候选」这种情形，它给得出 RRF 给不出的区分。
/// 这正是 §7.4 那个实验要测的东西，Week2 只负责把两条路都铺好。
/// </para>
/// </summary>
/// <param name="alpha">稠密侧权重，0~1。1 表示只用稠密侧，0 表示只用稀疏侧。</param>
public sealed class WeightedFusion(double alpha = 0.5) : IScoreFusion
{
    public string Name => $"weighted(a={alpha:0.##})";

    public IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<RetrievedChunk> dense,
        IReadOnlyList<RetrievedChunk> sparse,
        int topK)
    {
        var denseNorm = FusionMerge.Normalize(dense);
        var sparseNorm = FusionMerge.Normalize(sparse);

        return FusionMerge.Combine(
            dense,
            sparse,
            Name,
            id => (alpha * denseNorm.GetValueOrDefault(id))
                  + ((1 - alpha) * sparseNorm.GetValueOrDefault(id)),
            topK);
    }
}
