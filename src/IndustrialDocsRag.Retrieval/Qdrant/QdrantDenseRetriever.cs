using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IndustrialDocsRag.Retrieval.Qdrant;

/// <summary>Qdrant 上的稠密向量检索，查命名向量 <c>dense</c>。</summary>
public sealed class QdrantDenseRetriever(
    QdrantClient client,
    QdrantOptions options,
    IEmbeddingService embedder) : IRetriever
{
    public string Name => $"qdrant-dense:{embedder.Name}";

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default)
    {
        var embedded = await embedder.EmbedQueryAsync(query.Text, ct);

        var hits = await client.SearchAsync(
            options.Collection,
            embedded.Dense,
            limit: (ulong)query.TopK,
            payloadSelector: true,
            vectorName: QdrantOptions.DenseVectorName,
            cancellationToken: ct);

        return [.. hits.Select(h => new RetrievedChunk(QdrantPayload.ToChunk(h.Payload), h.Score, "dense"))];
    }
}
