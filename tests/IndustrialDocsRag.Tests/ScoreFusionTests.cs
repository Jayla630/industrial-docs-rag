using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.Fusion;

namespace IndustrialDocsRag.Tests;

public class ScoreFusionTests
{
    private static RetrievedChunk Hit(string id, double score, string tag) => new(
        new DocumentChunk
        {
            Id = id,
            SourceFile = "sv660n-manual.pdf",
            SectionPath = "第5章 故障处理",
            Text = id,
        },
        score,
        tag);

    /// <summary>
    /// 两路都召回的块，证据更强，应当排在只有一路召回的块前面。
    /// </summary>
    [Fact]
    public void Rrf_两路都命中的块排在单路命中之前()
    {
        var dense = new[] { Hit("a", 0.61, "dense"), Hit("b", 0.60, "dense") };
        var sparse = new[] { Hit("c", 30.0, "sparse"), Hit("b", 12.0, "sparse") };

        var fused = new RrfFusion().Fuse(dense, sparse, topK: 3);

        Assert.Equal("b", fused[0].Chunk.Id);
    }

    /// <summary>
    /// ★ RRF 只看排名，不看分数量级。这正是它在本项目里可能<b>吃亏</b>的地方：
    /// 错误码精确匹配恰恰是 sparse 分数畸高的场景，RRF 会把最强的信号稀释掉。
    /// Week3 要量化的就是这个差异（§7.4）。
    /// </summary>
    [Fact]
    public void Rrf_分数量级不影响结果()
    {
        var dense = new[] { Hit("a", 0.9, "dense"), Hit("b", 0.1, "dense") };
        var sparse = new[] { Hit("b", 999.0, "sparse"), Hit("a", 1.0, "sparse") };

        var scaled = new[] { Hit("b", 0.002, "sparse"), Hit("a", 0.000002, "sparse") };

        Assert.Equal(
            new RrfFusion().Fuse(dense, sparse, 2).Select(h => h.Chunk.Id),
            new RrfFusion().Fuse(dense, scaled, 2).Select(h => h.Chunk.Id));
    }

    /// <summary>
    /// 加权融合前必须组内归一：余弦在 0~1，BM25 无上界，不归一就是 BM25 单独说了算。
    /// </summary>
    [Fact]
    public void Weighted_组内归一化后稠密侧不被BM25的量级压垮()
    {
        var dense = new[] { Hit("a", 0.80, "dense"), Hit("b", 0.50, "dense") };
        var sparse = new[] { Hit("b", 900.0, "sparse"), Hit("a", 300.0, "sparse") };

        // α=0.9 偏重稠密侧，a 在稠密侧是第一，应当胜出
        var fused = new WeightedFusion(0.9).Fuse(dense, sparse, topK: 2);

        Assert.Equal("a", fused[0].Chunk.Id);
    }

    [Fact]
    public void Weighted_alpha为1时等价于只用稠密侧()
    {
        var dense = new[] { Hit("a", 0.80, "dense"), Hit("b", 0.50, "dense") };
        var sparse = new[] { Hit("b", 900.0, "sparse") };

        var fused = new WeightedFusion(1.0).Fuse(dense, sparse, topK: 2);

        Assert.Equal(["a", "b"], fused.Select(h => h.Chunk.Id));
    }

    /// <summary>
    /// ★ 单侧独有的命中要保留原 tag —— Week3 分析「哪几道题只有 sparse 能召回、
    /// dense 完全捞不到」时，这是唯一的数据来源。
    /// </summary>
    [Fact]
    public void Fuse_单侧独有的命中保留原tag_两路都有的打融合器名()
    {
        var dense = new[] { Hit("only-dense", 0.7, "dense"), Hit("both", 0.6, "dense") };
        var sparse = new[] { Hit("only-sparse", 20.0, "sparse"), Hit("both", 10.0, "sparse") };

        var fused = new RrfFusion().Fuse(dense, sparse, topK: 3).ToDictionary(h => h.Chunk.Id, h => h.RetrieverTag);

        Assert.Equal("rrf", fused["both"]);
        Assert.Equal("dense", fused["only-dense"]);
        Assert.Equal("sparse", fused["only-sparse"]);
    }

    [Fact]
    public void Fuse_一侧为空时退化为另一侧的排序()
    {
        var dense = new[] { Hit("a", 0.7, "dense"), Hit("b", 0.6, "dense") };

        var fused = new RrfFusion().Fuse(dense, [], topK: 5);

        Assert.Equal(["a", "b"], fused.Select(h => h.Chunk.Id));
    }

    [Fact]
    public void Fuse_TopK截断生效()
    {
        var dense = new[] { Hit("a", 0.7, "dense"), Hit("b", 0.6, "dense"), Hit("c", 0.5, "dense") };

        Assert.Equal(2, new RrfFusion().Fuse(dense, [], topK: 2).Count);
    }
}
