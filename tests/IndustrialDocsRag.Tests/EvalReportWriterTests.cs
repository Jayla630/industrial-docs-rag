using IndustrialDocsRag.Eval;

namespace IndustrialDocsRag.Tests;

public class EvalReportWriterTests
{
    private static ConfigSummary Summary(string name, double overall, params (Stratum Stratum, double Recall)[] byStratum)
        => new(name, overall, byStratum.ToDictionary(x => x.Stratum, x => x.Recall), 0.5, 25);

    /// <summary>
    /// 分层列直接由 <c>Enum.GetValues&lt;Stratum&gt;()</c> 推导，
    /// 所以五个分层必然全部出现，不会因为某次忘了同步一份字符串数组而漏列。
    /// </summary>
    [Fact]
    public void 主表每个配置一行每个分层一列()
    {
        var markdown = EvalReportWriter.StratumTable(
            [Summary("dense", 0.52, (Stratum.Prose, 1.0), (Stratum.FaultCode, 0.2))]);

        Assert.Contains("| dense |", markdown);

        foreach (var stratum in Enum.GetValues<Stratum>())
        {
            Assert.Contains(stratum.ToString(), markdown);
        }

        Assert.Contains("52%", markdown);
    }

    /// <summary>缺失的分层要显示为 —，不能显示 0% —— 0% 和「没这一层的题」是两回事。</summary>
    [Fact]
    public void 主表缺失分层显示为破折号()
    {
        var markdown = EvalReportWriter.StratumTable([Summary("dense", 0.5, (Stratum.Prose, 1.0))]);

        Assert.Contains("—", markdown);
    }

    /// <summary>
    /// 空集合只出表头 —— Task 9 的 EvalCommand 会直接调它，`--configs` 过滤后集合为空是可达路径。
    /// 与 EvalMetrics.Aggregate 的空集合防护对齐。
    /// </summary>
    [Fact]
    public void 主表空集合只输出表头()
    {
        var markdown = EvalReportWriter.StratumTable([]);

        Assert.Contains("| 配置 |", markdown);
        Assert.Contains("总计", markdown);
    }

    [Fact]
    public void 配对表列出双方独有的题号()
    {
        var markdown = EvalReportWriter.PairwiseTable(
            [new PairwiseDiff("hybrid", "dense", ["q03", "q07"], [], ["q01"], ["q09"])]);

        Assert.Contains("q03", markdown);
        Assert.Contains("q07", markdown);
        Assert.Contains("+2", markdown);
        Assert.Contains("-0", markdown);
    }

    [Fact]
    public void 配对表无差异时明确写出无差异()
    {
        var markdown = EvalReportWriter.PairwiseTable(
            [new PairwiseDiff("a", "b", [], [], ["q01"], [])]);

        Assert.Contains("无差异", markdown);
    }

    [Fact]
    public void 配对表空集合只输出表头()
    {
        var markdown = EvalReportWriter.PairwiseTable([]);

        Assert.Contains("| 对比 |", markdown);
    }
}
