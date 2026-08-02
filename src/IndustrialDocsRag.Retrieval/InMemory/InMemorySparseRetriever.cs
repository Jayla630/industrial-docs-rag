using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.InMemory;

/// <summary>
/// 内存里的 BM25 检索：查询侧编码出 IDF 向量，与每个块的文档侧向量做点积，取 Top-K。
/// <para>
/// 点积就是 BM25 分数 —— 权重按惯例拆到了两侧（文档侧存 tf 与长度归一化，查询侧存 IDF），
/// 这也正是 Qdrant 稀疏向量检索的算法。所以这个实现与 Qdrant 版本应当给出一致的排序，
/// 施工第 7 步拿它当对照：不一致就是 Qdrant 接入有 bug，而不是 BM25 写错了。
/// </para>
/// <para>
/// 它不是一次性脚手架：没有 Docker 的人 clone 仓库后，靠它加上 15 条 fixture
/// 依然能跑通混合检索。
/// </para>
/// </summary>
public sealed class InMemorySparseRetriever(InMemoryChunkIndex index, ISparseEncoder encoder) : IRetriever
{
    public string Name => $"sparse:{encoder.Name}";

    public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default)
    {
        var queryVector = encoder.EncodeQuery(query.Text);

        var scored = new List<RetrievedChunk>(index.Items.Count);
        foreach (var item in index.Items)
        {
            ct.ThrowIfCancellationRequested();

            var score = Dot(item.Embedding.Sparse ?? SparseVector.Empty, queryVector);

            // 稀疏侧一个词都不命中就是 0 分，与稠密侧不同 ——
            // 0 分的块留在结果里毫无意义，还会在混合检索里挤掉另一路的候选
            if (score <= 0)
            {
                continue;
            }

            scored.Add(new RetrievedChunk(item.Chunk, score, "sparse"));
        }

        IReadOnlyList<RetrievedChunk> top = scored
            .OrderByDescending(r => r.Score)
            // 分数相同时按 id 排，保证同一份索引每次跑出来的顺序一致（评测需要可复现）
            .ThenBy(r => r.Chunk.Id, StringComparer.Ordinal)
            .Take(query.TopK)
            .ToList();

        return Task.FromResult(top);
    }

    /// <summary>
    /// 两个稀疏向量的点积。两侧 Indices 都是升序，走归并即可，
    /// 不必为查询向量另建字典 —— 1667 个块每次查询都要走一遍这里。
    /// </summary>
    private static double Dot(SparseVector document, SparseVector query)
    {
        var sum = 0.0;
        int i = 0, j = 0;

        while (i < document.Indices.Length && j < query.Indices.Length)
        {
            var a = document.Indices[i];
            var b = query.Indices[j];

            if (a == b)
            {
                sum += (double)document.Values[i] * query.Values[j];
                i++;
                j++;
            }
            else if (a < b)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return sum;
    }
}
