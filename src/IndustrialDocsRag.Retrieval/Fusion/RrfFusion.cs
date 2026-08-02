using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.Fusion;

/// <summary>
/// Reciprocal Rank Fusion：<c>score = Σ 1/(k + rank)</c>，rank 从 1 起。
/// <para>
/// 它<b>只用排名、完全丢弃原始分数</b>。好处是两路分数不可比时也不需要归一化；
/// 代价是在本项目里可能吃亏 —— 错误码精确匹配恰恰是 sparse 分数畸高的场景，
/// 而 RRF 会把这个最强的信号稀释成「第 1 名」这一个事实。
/// Week3 要量化的正是它与 <see cref="WeightedFusion"/> 的差异（§7.4）。
/// </para>
/// </summary>
/// <param name="k">平滑常数，IR 文献惯用 60。越大越淡化头部排名的优势。</param>
public sealed class RrfFusion(int k = 60) : IScoreFusion
{
    public string Name => "rrf";

    public IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<RetrievedChunk> dense,
        IReadOnlyList<RetrievedChunk> sparse,
        int topK)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var list in new[] { dense, sparse })
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank].Chunk.Id;
                scores[id] = scores.GetValueOrDefault(id) + (1.0 / (k + rank + 1));
            }
        }

        return FusionMerge.Combine(dense, sparse, Name, id => scores[id], topK);
    }
}
