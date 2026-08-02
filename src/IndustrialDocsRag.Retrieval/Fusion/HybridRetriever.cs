using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.Fusion;

/// <summary>
/// 混合检索：并行跑两路召回，再交给 <see cref="IScoreFusion"/> 融合。
/// <para>
/// 它是个<b>装饰器</b>，组合任意两个 <see cref="IRetriever"/>，与存储无关 ——
/// 内存路径（<c>InMemoryVectorRetriever</c> + <c>InMemorySparseRetriever</c>）和
/// Qdrant 路径共用同一份融合逻辑。这也是「检索层可替换」这个主张在 Week2 的兑现。
/// </para>
/// </summary>
/// <param name="candidateMultiplier">
/// 候选扩大倍数。融合要在更大的候选池上做才有意义：某个块在稠密侧排第 8、
/// 只取 Top-5 的话它压根进不了融合，而它可能正是稀疏侧的第 1 名。
/// </param>
public sealed class HybridRetriever(
    IRetriever dense,
    IRetriever sparse,
    IScoreFusion fusion,
    int candidateMultiplier = 3) : IRetriever
{
    public string Name => $"hybrid({dense.Name} + {sparse.Name}, {fusion.Name})";

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default)
    {
        var wide = query with { TopK = query.TopK * candidateMultiplier };

        var denseTask = dense.RetrieveAsync(wide, ct);
        var sparseTask = sparse.RetrieveAsync(wide, ct);
        await Task.WhenAll(denseTask, sparseTask);

        return fusion.Fuse(await denseTask, await sparseTask, query.TopK);
    }
}
