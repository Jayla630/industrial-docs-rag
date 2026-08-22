using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Eval;

/// <summary>
/// 一道题在某个配置下的结果。<see cref="HitRank"/> 为 null 表示召回集里没有任何块覆盖锚点。
/// </summary>
public sealed record QuestionOutcome(string Qid, Stratum Stratum, int? HitRank)
{
    /// <summary>
    /// Top-K 以内命中。所有指标都建立在这一个判据上 —— 不要在别处再写一遍
    /// <c>HitRank is { } r &amp;&amp; r &lt;= k</c>，两份实现迟早会漂移。
    /// </summary>
    public bool HitWithin(int k) => HitRank is { } rank && rank <= k;

    /// <summary>命中名次的倒数，未命中或超出 Top-K 记 0。</summary>
    public double ReciprocalRank(int k) => HitWithin(k) ? 1d / HitRank!.Value : 0d;
}

/// <summary>一个配置在整份评测集上的汇总。</summary>
public sealed record ConfigSummary(
    string ConfigName,
    double OverallRecall,
    IReadOnlyDictionary<Stratum, double> RecallByStratum,
    double MeanReciprocalRank,
    int QuestionCount);

/// <summary>两个配置的逐题配对结果。</summary>
public sealed record PairwiseDiff(
    string LeftName,
    string RightName,
    IReadOnlyList<string> OnlyLeft,
    IReadOnlyList<string> OnlyRight,
    IReadOnlyList<string> Both,
    IReadOnlyList<string> Neither);

/// <summary>
/// 指标计算。只认「命中 / 未命中」和名次，不认检索器、不认存储后端 —— 换掉任何一层都不影响它。
/// </summary>
public static class EvalMetrics
{
    /// <summary>
    /// 统一的名次口径：分数降序，<b>并列时按 chunk id 升序破平局</b>。
    /// <para>
    /// Week2 §8.9 实测：两块分数精确并列（14.0180）时，内存实现按 id 破平局、Qdrant 不破，
    /// 同一批数据第 3/4 名互换。名次由评测器统一决定，不交给后端 ——
    /// 否则「换存储后端跑出同一张表」这条一致性结论就不成立了。
    /// </para>
    /// </summary>
    public static IReadOnlyList<RetrievedChunk> Rank(IReadOnlyList<RetrievedChunk> hits)
        => hits.OrderByDescending(h => h.Score)
               .ThenBy(h => h.Chunk.Id, StringComparer.Ordinal)
               .ToList();

    /// <summary>
    /// 第一个覆盖<b>任一</b>锚点的块的名次（从 1 起），没有则 null。
    /// 一题多锚点是常态：pooling 播种会把「同样正确的其它出处」补进来。
    /// </summary>
    public static int? FirstHitRank(
        IReadOnlyList<RetrievedChunk> hits, IReadOnlyList<ResolvedAnchor> anchors)
    {
        var ranked = Rank(hits);

        for (var i = 0; i < ranked.Count; i++)
        {
            if (anchors.Any(anchor => RelevanceJudge.Covers(ranked[i].Chunk, anchor)))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// 分层聚合。分层不是装饰：n=25 时总分差几个百分点说明不了任何事
    /// （差 1 题就是 4pp，95% CI 约 ±19pp），而「稠密在 Prose 层 5/5、在 FaultCode 层 1/5」是硬的。
    /// </summary>
    public static ConfigSummary Aggregate(
        string configName, IReadOnlyList<QuestionOutcome> outcomes, int topK)
        => new(
            configName,
            outcomes.Count == 0 ? 0 : outcomes.Count(o => o.HitWithin(topK)) / (double)outcomes.Count,
            outcomes.GroupBy(o => o.Stratum)
                    .ToDictionary(g => g.Key, g => g.Count(o => o.HitWithin(topK)) / (double)g.Count()),
            outcomes.Count == 0 ? 0 : outcomes.Average(o => o.ReciprocalRank(topK)),
            outcomes.Count);

    /// <summary>
    /// 逐题配对。<see cref="PairwiseDiff.OnlyLeft"/> 是「左边命中而右边没命中」的题号。
    /// <para>
    /// 这是本项目在 n=25 下唯一能拿出去讲的对比口径：同一批题上的逐题胜负消掉了题目难度的方差。
    /// <c>hybrid 相对 dense +6/-0</c> 比 <c>76% vs 52%</c> 硬得多。
    /// </para>
    /// </summary>
    public static PairwiseDiff Pairwise(
        string leftName, IReadOnlyList<QuestionOutcome> left,
        string rightName, IReadOnlyList<QuestionOutcome> right,
        int topK)
    {
        var rightByQid = right.ToDictionary(o => o.Qid, StringComparer.Ordinal);

        List<string> onlyLeft = [], onlyRight = [], both = [], neither = [];

        foreach (var l in left)
        {
            // 右侧缺这道题时按未命中处理：两组题目理论上一致，真不一致时应当能从数字里看出来，
            // 而不是抛异常中断整份报告。
            var rightHit = rightByQid.TryGetValue(l.Qid, out var r) && r.HitWithin(topK);

            var bucket = (l.HitWithin(topK), rightHit) switch
            {
                (true, false) => onlyLeft,
                (false, true) => onlyRight,
                (true, true) => both,
                _ => neither,
            };

            bucket.Add(l.Qid);
        }

        return new PairwiseDiff(leftName, rightName, onlyLeft, onlyRight, both, neither);
    }
}
