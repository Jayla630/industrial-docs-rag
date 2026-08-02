using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;
using Qdrant.Client;

namespace IndustrialDocsRag.Retrieval.Qdrant;

/// <summary>
/// Qdrant 上的 BM25 稀疏检索，查命名向量 <c>bm25</c>。
/// <para>
/// Qdrant 算的是稀疏向量点积，而我们把 BM25 的权重按惯例拆到了两侧
/// （文档侧 tf 与长度归一化、查询侧 IDF），所以这个点积就是 BM25 分数 ——
/// 与 <see cref="InMemory.InMemorySparseRetriever"/> 应当给出一致的排序。
/// 不一致就说明 Qdrant 接入有 bug，而不是 BM25 写错了。
/// </para>
/// </summary>
public sealed class QdrantSparseRetriever(
    QdrantClient client,
    QdrantOptions options,
    ISparseEncoder encoder) : IRetriever
{
    public string Name => $"qdrant-sparse:{encoder.Name}";

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default)
    {
        var sparse = encoder.EncodeQuery(query.Text);

        if (sparse.NnZ == 0)
        {
            // 查询里一个词都不在词表内。这不是错误 —— `0x6041` 就是这种情况
            // （手册写的是 `6041`），正是稀疏侧该输给稠密侧的地方。
            return [];
        }

        var hits = await client.SearchAsync(
            options.Collection,
            sparse.Values,
            limit: (ulong)query.TopK,
            payloadSelector: true,
            vectorName: QdrantOptions.SparseVectorName,
            sparseIndices: sparse.Indices,
            cancellationToken: ct);

        return [.. hits.Select(h => new RetrievedChunk(QdrantPayload.ToChunk(h.Payload), h.Score, "sparse"))];
    }
}
