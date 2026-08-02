using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.Fusion;

/// <summary>两个融合器共用的合并逻辑：按 chunk id 对齐、定 tag、排序截断。</summary>
internal static class FusionMerge
{
    /// <summary>
    /// 单侧独有的命中保留原 tag，两路都有的打上融合器名。
    /// <para>
    /// 这不是装饰。Week3 要回答「哪几道题只有 sparse 能召回、dense 完全捞不到」，
    /// 而这个 tag 是唯一的数据来源 —— 融合之后原始的来源信息就没别处可查了。
    /// </para>
    /// </summary>
    public static IReadOnlyList<RetrievedChunk> Combine(
        IReadOnlyList<RetrievedChunk> dense,
        IReadOnlyList<RetrievedChunk> sparse,
        string fusionName,
        Func<string, double> scoreOf,
        int topK)
    {
        var denseIds = dense.Select(h => h.Chunk.Id).ToHashSet(StringComparer.Ordinal);
        var sparseIds = sparse.Select(h => h.Chunk.Id).ToHashSet(StringComparer.Ordinal);

        return dense.Concat(sparse)
            .GroupBy(h => h.Chunk.Id, StringComparer.Ordinal)
            .Select(g => new RetrievedChunk(
                g.First().Chunk,
                scoreOf(g.Key),
                denseIds.Contains(g.Key) && sparseIds.Contains(g.Key)
                    ? fusionName
                    : g.First().RetrieverTag))
            .OrderByDescending(h => h.Score)
            // 分数相同时按 id 排，保证可复现
            .ThenBy(h => h.Chunk.Id, StringComparer.Ordinal)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// 组内 min-max 归一化。
    /// <para>
    /// 全部相同（含只有一个命中）时返回 1.0 而不是 0 —— 返回 0 会把一路仅有的候选直接抹掉。
    /// </para>
    /// </summary>
    public static Dictionary<string, double> Normalize(IReadOnlyList<RetrievedChunk> hits)
    {
        if (hits.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        var max = hits.Max(h => h.Score);
        var min = hits.Min(h => h.Score);
        var range = max - min;

        return hits.ToDictionary(
            h => h.Chunk.Id,
            h => range <= 0 ? 1.0 : (h.Score - min) / range,
            StringComparer.Ordinal);
    }
}
