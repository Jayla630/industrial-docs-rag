using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.Fusion;

namespace IndustrialDocsRag.Tests;

public class HybridRetrieverTests
{
    /// <summary>记录收到的 TopK，用来验证候选扩大确实发生了。</summary>
    private sealed class SpyRetriever(string name, string tag, params string[] ids) : IRetriever
    {
        public string Name => name;

        public int? LastTopK { get; private set; }

        public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
            RetrievalQuery query, CancellationToken ct = default)
        {
            LastTopK = query.TopK;

            IReadOnlyList<RetrievedChunk> hits = ids
                .Select((id, i) => new RetrievedChunk(
                    new DocumentChunk
                    {
                        Id = id,
                        SourceFile = "sv660n-manual.pdf",
                        SectionPath = "第5章 故障处理",
                        Text = id,
                    },
                    ids.Length - i,
                    tag))
                .Take(query.TopK)
                .ToList();

            return Task.FromResult(hits);
        }
    }

    /// <summary>
    /// 融合要在更大的候选池上做才有意义：某个块在稠密侧排第 8，
    /// 只取 Top-5 的话它根本进不了融合，而它可能在稀疏侧排第 1。
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_两路各取TopK的三倍候选()
    {
        var dense = new SpyRetriever("dense", "dense", "a", "b");
        var sparse = new SpyRetriever("sparse", "sparse", "b", "c");
        var hybrid = new HybridRetriever(dense, sparse, new RrfFusion());

        await hybrid.RetrieveAsync(new RetrievalQuery { Text = "q", TopK = 5 });

        Assert.Equal(15, dense.LastTopK);
        Assert.Equal(15, sparse.LastTopK);
    }

    [Fact]
    public async Task RetrieveAsync_结果按TopK截断()
    {
        var hybrid = new HybridRetriever(
            new SpyRetriever("dense", "dense", "a", "b", "c"),
            new SpyRetriever("sparse", "sparse", "d", "e"),
            new RrfFusion());

        var hits = await hybrid.RetrieveAsync(new RetrievalQuery { Text = "q", TopK = 2 });

        Assert.Equal(2, hits.Count);
    }

    /// <summary>
    /// Name 要能唯一标识一次实验配置 —— Week3 的评测报告按它分组。
    /// </summary>
    [Fact]
    public void Name_含两路检索器与融合方式()
    {
        var hybrid = new HybridRetriever(
            new SpyRetriever("dense:bge-m3", "dense"),
            new SpyRetriever("sparse:bm25", "sparse"),
            new RrfFusion());

        Assert.Equal("hybrid(dense:bge-m3 + sparse:bm25, rrf)", hybrid.Name);
    }

    [Fact]
    public async Task RetrieveAsync_一路无结果时另一路照常返回()
    {
        var hybrid = new HybridRetriever(
            new SpyRetriever("dense", "dense", "a", "b"),
            new SpyRetriever("sparse", "sparse"),
            new RrfFusion());

        var hits = await hybrid.RetrieveAsync(new RetrievalQuery { Text = "q", TopK = 5 });

        Assert.Equal(["a", "b"], hits.Select(h => h.Chunk.Id));
    }
}
