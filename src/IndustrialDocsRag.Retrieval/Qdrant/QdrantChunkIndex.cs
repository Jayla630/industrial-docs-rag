using IndustrialDocsRag.Core.Retrieval;
using Qdrant.Client;
using Qdrant.Client.Grpc;

// 本命名空间自己叫 ...Retrieval.Qdrant，写 `Qdrant.Client.Grpc.SparseVector` 会被解析成
// 本命名空间下的 Qdrant.Client，编译不过。与 Week1 §9.14 那条「目录不能叫 Kernel」同类。
using GrpcSparseVector = global::Qdrant.Client.Grpc.SparseVector;

namespace IndustrialDocsRag.Retrieval.Qdrant;

/// <summary>
/// Qdrant 后端的索引实现。一个 collection、两个命名向量：
/// <c>dense</c>（BGE-M3，余弦）与稀疏的 <c>bm25</c>。
/// <para>
/// <see cref="IChunkIndex"/> 的签名一行没改 —— collection 名、维度这些都通过
/// <see cref="QdrantOptions"/> 构造函数注入。这正是 Week1 那套抽象在 Week2 的验收。
/// </para>
/// </summary>
public sealed class QdrantChunkIndex(QdrantClient client, QdrantOptions options, int denseDimension) : IChunkIndex
{
    public async Task<bool> ExistsAsync(CancellationToken ct = default)
        => await client.CollectionExistsAsync(options.Collection, ct);

    /// <summary>
    /// 删掉重建。增量 upsert 是 YAGNI —— 全量重建 1667 块只要几十秒，
    /// 而增量要处理「切分规则变了，旧块该不该删」这类问题，成本远高于收益。
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        if (await client.CollectionExistsAsync(options.Collection, ct))
        {
            await client.DeleteCollectionAsync(options.Collection, cancellationToken: ct);
        }

        await client.CreateCollectionAsync(
            options.Collection,
            vectorsConfig: new VectorParamsMap
            {
                Map =
                {
                    [QdrantOptions.DenseVectorName] = new VectorParams
                    {
                        Size = (ulong)denseDimension,
                        Distance = Distance.Cosine,
                    },
                },
            },
            // 稀疏向量必须在建 collection 时声明，建完再加不行
            sparseVectorsConfig: new SparseVectorConfig
            {
                Map = { [QdrantOptions.SparseVectorName] = new SparseVectorParams() },
            },
            cancellationToken: ct);
    }

    public async Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        var points = new List<PointStruct>(chunks.Count);

        foreach (var item in chunks)
        {
            var vectors = new NamedVectors();

            vectors.Vectors[QdrantOptions.DenseVectorName] = new Vector
            {
                Dense = new DenseVector { Data = { item.Embedding.Dense } },
            };

            if (item.Embedding.Sparse is { NnZ: > 0 } sparse)
            {
                vectors.Vectors[QdrantOptions.SparseVectorName] = new Vector
                {
                    Sparse = new GrpcSparseVector
                    {
                        Indices = { sparse.Indices },
                        Values = { sparse.Values },
                    },
                };
            }

            var point = new PointStruct
            {
                Id = QdrantPayload.PointId(item.Chunk.Id),
                Vectors = new Vectors { Vectors_ = vectors },
            };

            QdrantPayload.Fill(point.Payload, item.Chunk);
            points.Add(point);
        }

        await client.UpsertAsync(options.Collection, points, cancellationToken: ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
        => (int)await client.CountAsync(options.Collection, cancellationToken: ct);
}
