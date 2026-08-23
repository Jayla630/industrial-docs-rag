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

    /// <summary>
    /// ★ 这个设计唯一的结构性风险：两个方法各走各的代码路径，
    /// 轨迹显示的 Top-K 跟实际检索的对不上 —— 不抛异常，只是判错。
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_与轨迹的Fused逐条相同()
    {
        var hybrid = new HybridRetriever(
            new SpyRetriever("dense", "dense", "a", "b", "c"),
            new SpyRetriever("sparse", "sparse", "b", "c", "d"),
            new RrfFusion());

        var query = new RetrievalQuery { Text = "q", TopK = 3 };
        var plain = await hybrid.RetrieveAsync(query);
        var trace = await hybrid.RetrieveWithTraceAsync(query);

        Assert.Equal(
            plain.Select(h => (h.Chunk.Id, h.Score, h.RetrieverTag)),
            trace.Fused.Select(h => (h.Chunk.Id, h.Score, h.RetrieverTag)));
    }

    [Fact]
    public async Task RetrieveWithTraceAsync_两路候选是原始返回未被截断()
    {
        var hybrid = new HybridRetriever(
            new SpyRetriever("dense", "dense", "a", "b", "c"),
            new SpyRetriever("sparse", "sparse", "x", "y"),
            new RrfFusion());

        var trace = await hybrid.RetrieveWithTraceAsync(
            new RetrievalQuery { Text = "q", TopK = 2 });

        // 断言成员而不是整个 record：RetrievalTrace 带集合属性，
        // 自动生成的 Equals 对 IReadOnlyList 走引用比较（见 CLAUDE.md 测试约定）
        Assert.Equal(["a", "b", "c"], trace.Dense.Select(h => h.Chunk.Id));
        Assert.Equal(["x", "y"], trace.Sparse.Select(h => h.Chunk.Id));
        Assert.Equal(2, trace.Fused.Count);
        Assert.Equal("q", trace.Query);
        Assert.Equal(hybrid.Name, trace.RetrieverName);
    }

    [Fact]
    public async Task RetrieveWithTraceAsync_两路收到扩大后的TopK()
    {
        var dense = new SpyRetriever("dense", "dense", "a");
        var sparse = new SpyRetriever("sparse", "sparse", "b");
        var hybrid = new HybridRetriever(dense, sparse, new RrfFusion());

        await hybrid.RetrieveWithTraceAsync(new RetrievalQuery { Text = "q", TopK = 4 });

        // 断言检索器收到的 TopK，不是返回条数 ——
        // 索引块数不足时返回自然会少，拿返回条数断言会在小语料上假失败
        Assert.Equal(12, dense.LastTopK);
        Assert.Equal(12, sparse.LastTopK);
    }

    [Fact]
    public async Task RetrieveWithTraceAsync_稀疏侧为空时带出SingleSideEmpty()
    {
        var hybrid = new HybridRetriever(
            new SpyRetriever("dense", "dense", "a", "b"),
            new SpyRetriever("sparse", "sparse"),
            new RrfFusion());

        var trace = await hybrid.RetrieveWithTraceAsync(
            new RetrievalQuery { Text = "0x6041 是什么", TopK = 5 });

        var signal = Assert.Single(trace.Signals);
        Assert.Equal(RetrievalSignalKind.SingleSideEmpty, signal.Kind);

        // Kind 恒为 SingleSideEmpty，不携带「哪一侧」的信息 —— 只断言 Kind 的话，
        // 万一 HybridRetriever 里那句 Detect 调用被改成位置传参且顺序传反，
        // dense/sparse 互换后 Kind 照样是 SingleSideEmpty，这条测试会假通过。
        // 断言 Message 才能真正守住 Task 1 定的具名参数约定。
        Assert.Contains("sparse", signal.Message);
    }
}
