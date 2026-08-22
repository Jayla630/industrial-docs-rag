using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Eval;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 指标计算。这里产出的是报告里的每一个数字，所以口径必须唯一且可复现 ——
/// 名次怎么排、并列怎么破、Top-K 之外算不算命中，全部只有一处定义。
/// </summary>
public class EvalMetricsTests
{
    private static RetrievedChunk Hit(string id, double score, int charOffset = 0) =>
        new(new DocumentChunk
        {
            Id = id,
            SourceFile = "m.pdf",
            SectionPath = "第1章",
            CharOffset = charOffset,
            SourceLength = 10,
            Text = id,
        }, score, "test");

    private static ResolvedAnchor At(int offset) => new("q01", 1, "needle", offset);

    /// <summary>
    /// Week2 §8.9 实测：两块分数精确并列（14.0180）时，内存实现按 chunk id 破平局、Qdrant 不破，
    /// 同一批数据第 3/4 名互换。名次必须由评测器统一决定，不能交给存储后端。
    /// </summary>
    [Fact]
    public void Rank_分数并列时按chunkId升序破平局()
    {
        var hits = new[] { Hit("b", 1.0), Hit("a", 1.0), Hit("c", 2.0) };

        var ranked = EvalMetrics.Rank(hits);

        Assert.Equal(["c", "a", "b"], ranked.Select(h => h.Chunk.Id));
    }

    [Fact]
    public void Rank_分数降序()
    {
        var hits = new[] { Hit("a", 0.1), Hit("b", 0.9), Hit("c", 0.5) };

        Assert.Equal(["b", "c", "a"], EvalMetrics.Rank(hits).Select(h => h.Chunk.Id));
    }

    [Fact]
    public void FirstHitRank_返回第一个命中的名次从1开始()
    {
        var hits = new[] { Hit("a", 3.0, 0), Hit("b", 2.0, 100), Hit("c", 1.0, 200) };

        Assert.Equal(2, EvalMetrics.FirstHitRank(hits, [At(105)]));
    }

    [Fact]
    public void FirstHitRank_全不命中返回null()
    {
        Assert.Null(EvalMetrics.FirstHitRank([Hit("a", 1.0, 0)], [At(999)]));
    }

    /// <summary>一题多锚点时，命中任一锚点即算命中，取最靠前的那个名次。</summary>
    [Fact]
    public void FirstHitRank_多锚点取最靠前的命中()
    {
        var hits = new[] { Hit("a", 3.0, 0), Hit("b", 2.0, 100), Hit("c", 1.0, 200) };

        Assert.Equal(2, EvalMetrics.FirstHitRank(hits, [At(205), At(105)]));
    }

    [Fact]
    public void HitWithin_名次在K以内算命中()
    {
        var outcome = new QuestionOutcome("q01", Stratum.Prose, HitRank: 3);

        Assert.True(outcome.HitWithin(3));
        Assert.False(outcome.HitWithin(2));
    }

    [Fact]
    public void HitWithin_未命中恒为false()
    {
        Assert.False(new QuestionOutcome("q01", Stratum.Prose, HitRank: null).HitWithin(100));
    }

    [Fact]
    public void ReciprocalRank_是名次的倒数()
    {
        Assert.Equal(0.5, new QuestionOutcome("q01", Stratum.Prose, HitRank: 2).ReciprocalRank(10));
    }

    [Fact]
    public void ReciprocalRank_超出K或未命中为零()
    {
        Assert.Equal(0d, new QuestionOutcome("q01", Stratum.Prose, HitRank: 2).ReciprocalRank(1));
        Assert.Equal(0d, new QuestionOutcome("q01", Stratum.Prose, HitRank: null).ReciprocalRank(10));
    }

    /// <summary>
    /// 分层不是装饰：n=25 时总分差几个百分点说明不了任何事（差 1 题就是 4pp，95% CI 约 ±19pp），
    /// 而「稠密在 Prose 层 5/5、在 FaultCode 层 1/5」是硬的。
    /// </summary>
    [Fact]
    public void Aggregate_按分层与总计各出一份()
    {
        var outcomes = new List<QuestionOutcome>
        {
            new("q01", Stratum.Prose, HitRank: 1),
            new("q02", Stratum.Prose, HitRank: null),
            new("q03", Stratum.FaultCode, HitRank: 3),
        };

        var summary = EvalMetrics.Aggregate("dense", outcomes, topK: 5);

        Assert.Equal(2d / 3, summary.OverallRecall, 6);
        Assert.Equal(0.5, summary.RecallByStratum[Stratum.Prose], 6);
        Assert.Equal(1.0, summary.RecallByStratum[Stratum.FaultCode], 6);
    }

    /// <summary>名次超出 Top-K 的题按未命中计 —— 主指标是 Recall@5，第 7 名不算召回到。</summary>
    [Fact]
    public void Aggregate_名次超出K的按未命中计()
    {
        var outcomes = new List<QuestionOutcome> { new("q01", Stratum.Prose, HitRank: 7) };

        Assert.Equal(0d, EvalMetrics.Aggregate("dense", outcomes, topK: 5).OverallRecall);
    }

    /// <summary>空评测集不能除零。</summary>
    [Fact]
    public void Aggregate_空集合不崩溃()
    {
        var summary = EvalMetrics.Aggregate("dense", [], topK: 5);

        Assert.Equal(0d, summary.OverallRecall);
        Assert.Empty(summary.RecallByStratum);
        Assert.Equal(0, summary.QuestionCount);
    }

    /// <summary>
    /// 逐题配对：n=25 的聚合差异 95% CI 约 ±19pp，不可信；
    /// 同一批题上的逐题胜负消掉了题目难度的方差，才是能拿出去讲的结论。
    /// </summary>
    [Fact]
    public void Pairwise_列出一方命中另一方未命中的题()
    {
        var a = new List<QuestionOutcome>
        {
            new("q01", Stratum.Prose, 1),
            new("q02", Stratum.Prose, null),
            new("q03", Stratum.Prose, 2),
        };

        var b = new List<QuestionOutcome>
        {
            new("q01", Stratum.Prose, null),
            new("q02", Stratum.Prose, 1),
            new("q03", Stratum.Prose, 4),
        };

        var diff = EvalMetrics.Pairwise("a", a, "b", b, topK: 5);

        Assert.Equal(["q01"], diff.OnlyLeft);
        Assert.Equal(["q02"], diff.OnlyRight);
        Assert.Equal(["q03"], diff.Both);
        Assert.Empty(diff.Neither);
    }

    /// <summary>
    /// 右侧缺这道题时按未命中处理，不能崩 —— 配置矩阵里两组题目理论上一致，
    /// 但真出现不一致时应当能看出来而不是抛异常。
    /// </summary>
    [Fact]
    public void Pairwise_右侧缺题按未命中处理()
    {
        var diff = EvalMetrics.Pairwise(
            "a", [new QuestionOutcome("q01", Stratum.Prose, 1)],
            "b", [],
            topK: 5);

        Assert.Equal(["q01"], diff.OnlyLeft);
    }
}
