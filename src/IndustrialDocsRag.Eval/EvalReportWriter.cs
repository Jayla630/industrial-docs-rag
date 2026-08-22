using System.Globalization;
using System.Text;

namespace IndustrialDocsRag.Eval;

/// <summary>
/// 把汇总结果排成 Markdown 表。README 里的表是从这里的输出摘出去的，所以格式要能直接粘。
/// </summary>
public static class EvalReportWriter
{
    /// <summary>五个分层，顺序即列序。由枚举推导，不维护第二份清单。</summary>
    private static readonly Stratum[] Strata = Enum.GetValues<Stratum>();

    /// <summary>
    /// 主表：行 = 配置，列 = 分层 + 总计。
    /// <para>
    /// 分层在前、总计在后是刻意的：总计在 n=25 下不可作结论（95% CI 约 ±19pp），
    /// 排版上就不该让它抢主位。
    /// </para>
    /// </summary>
    public static string StratumTable(IReadOnlyList<ConfigSummary> summaries)
    {
        var sb = new StringBuilder();

        sb.Append("| 配置 |");
        foreach (var stratum in Strata)
        {
            sb.Append(' ').Append(stratum).Append(" |");
        }

        sb.AppendLine(" 总计 |");

        // 分隔行要与表头列数一致：前缀已出「配置」那一段，这里再出 5 个分层 + 总计，故用 <=
        sb.Append("|---|");
        for (var i = 0; i <= Strata.Length; i++)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        foreach (var summary in summaries)
        {
            sb.Append("| ").Append(summary.ConfigName).Append(" |");

            foreach (var stratum in Strata)
            {
                // 缺失的分层显示 — 而不是 0%：「这一层全错」和「这一层没题」是两回事
                sb.Append(' ')
                  .Append(summary.RecallByStratum.TryGetValue(stratum, out var recall)
                      ? Percent(recall)
                      : "—")
                  .Append(" |");
            }

            sb.Append(' ').Append(Percent(summary.OverallRecall)).AppendLine(" |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 表 2：逐题配对胜负。只列出一方命中而另一方未命中的题号。
    /// <para>
    /// 这是 n=25 下唯一站得住的对比口径 —— 同一批题上的逐题胜负消掉了题目难度的方差。
    /// </para>
    /// </summary>
    public static string PairwiseTable(IReadOnlyList<PairwiseDiff> diffs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("| 对比 | 净胜 | 仅左侧命中 | 仅右侧命中 |");
        sb.AppendLine("|---|---|---|---|");

        foreach (var diff in diffs)
        {
            var net = $"+{diff.OnlyLeft.Count}/-{diff.OnlyRight.Count}";
            var noDifference = diff.OnlyLeft.Count == 0 && diff.OnlyRight.Count == 0;

            sb.Append("| ").Append(diff.LeftName).Append(" vs ").Append(diff.RightName)
              .Append(" | ").Append(noDifference ? "无差异" : net)
              .Append(" | ").Append(Join(diff.OnlyLeft))
              .Append(" | ").Append(Join(diff.OnlyRight))
              .AppendLine(" |");
        }

        return sb.ToString();
    }

    private static string Join(IReadOnlyList<string> qids)
        => qids.Count == 0 ? "—" : string.Join(", ", qids);

    private static string Percent(double value)
        => (value * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
}
