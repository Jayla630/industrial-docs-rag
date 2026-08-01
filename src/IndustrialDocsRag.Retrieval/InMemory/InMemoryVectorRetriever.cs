using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.InMemory;

/// <summary>
/// Week1 的检索实现：把查询嵌入后，与索引里每个向量算余弦，取 Top-K。
/// <para>
/// 全量扫描，没有任何近似加速。在十几到几千个块的规模下这完全不是瓶颈 ——
/// 真正的瓶颈是嵌入服务那一次网络往返。Week2 换 Qdrant 是为了拿到稀疏向量和混合检索，
/// 不是为了性能。
/// </para>
/// </summary>
public sealed class InMemoryVectorRetriever(InMemoryChunkIndex index, IEmbeddingService embedder) : IRetriever
{
    public string Name => $"dense:{embedder.Name}";

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default)
    {
        index.EnsureCompatibleWith(embedder);

        var queryVector = await embedder.EmbedQueryAsync(query.Text, ct);

        var scored = new List<RetrievedChunk>(index.Items.Count);
        foreach (var item in index.Items)
        {
            ct.ThrowIfCancellationRequested();

            var score = VectorMath.Cosine(queryVector.Dense, item.Embedding.Dense);
            if (query.MinScore is { } min && score < min)
            {
                continue;
            }

            scored.Add(new RetrievedChunk(item.Chunk, score, "dense"));
        }

        return scored
            .OrderByDescending(r => r.Score)
            // 分数相同时按 id 排，保证同一份索引每次跑出来的顺序完全一致（评测需要可复现）
            .ThenBy(r => r.Chunk.Id, StringComparer.Ordinal)
            .Take(query.TopK)
            .ToList();
    }
}
