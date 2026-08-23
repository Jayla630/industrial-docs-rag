namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>
/// 从两路候选与融合结果推导降级判据。纯函数，不查索引，开销是几次集合运算。
/// </summary>
public static class RetrievalSignals
{
    /// <summary>
    /// 判「这条来自哪一路」一律用 chunk id 求交，<b>不看 RetrieverTag</b>。
    /// tag 规则是 FusionMerge 的实现细节，判据依赖它的话，
    /// 以后改 tag 规则会静默改掉判据语义 —— 又一个「不报错、只是判错」。
    /// </summary>
    /// <remarks>
    /// 调用时请用具名参数（<c>dense:</c> / <c>sparse:</c> / <c>fused:</c>）。三个参数类型相同，
    /// 位置传参传反了不会报错，只会算出反向的判据（比如把「dense 侧 0 结果」错报成「sparse 侧 0 结果」）。
    /// </remarks>
    public static IReadOnlyList<RetrievalSignal> Detect(
        IReadOnlyList<RetrievedChunk> dense,
        IReadOnlyList<RetrievedChunk> sparse,
        IReadOnlyList<RetrievedChunk> fused)
    {
        // 某一路为空时，另外两条必然成立（空集跟谁交都是空）。只报根因，不把同一件事说三遍。
        // 删掉这个提前 return 会让两路都空的情况同时触发 FusedFromOneSideOnly 与 NoCandidateOverlap，
        // 一条失效报三遍。
        if (dense.Count == 0 || sparse.Count == 0)
        {
            var empty = dense.Count == 0 ? "dense" : "sparse";
            var other = dense.Count == 0 ? "sparse" : "dense";
            var message = dense.Count == 0 && sparse.Count == 0
                ? "两路均 0 结果"
                : $"{empty} 侧 0 结果，融合退化成纯 {other}";

            return [new RetrievalSignal(RetrievalSignalKind.SingleSideEmpty, message)];
        }

        var denseIds = dense.Select(h => h.Chunk.Id).ToHashSet(StringComparer.Ordinal);
        var sparseIds = sparse.Select(h => h.Chunk.Id).ToHashSet(StringComparer.Ordinal);
        var fusedIds = fused.Select(h => h.Chunk.Id).ToHashSet(StringComparer.Ordinal);

        var signals = new List<RetrievalSignal>();

        var fromDense = fusedIds.Count(denseIds.Contains);
        var fromSparse = fusedIds.Count(sparseIds.Contains);

        // fused 是两路并集截断出来的（见 FusionMerge.Combine），所以 fusedIds ⊆ denseIds ∪ sparseIds。
        // 走到这里时 dense/sparse 都非空，只要 fused 非空就必有条目落在某一路里，
        // 即 fromDense 与 fromSparse 不可能同时为 0 —— 因此不需要、也不该有「两侧都没进 Top-K」的分支。
        if (fromDense == 0 || fromSparse == 0)
        {
            var absent = fromDense == 0 ? "dense" : "sparse";
            signals.Add(new RetrievalSignal(
                RetrievalSignalKind.FusedFromOneSideOnly,
                $"{absent} 侧有候选但一条都没进 Top-{fused.Count}"));
        }

        if (!denseIds.Overlaps(sparseIds))
        {
            signals.Add(new RetrievalSignal(
                RetrievalSignalKind.NoCandidateOverlap,
                $"两路候选零交集（dense {denseIds.Count} 条 / sparse {sparseIds.Count} 条）"));
        }

        return signals;
    }
}
