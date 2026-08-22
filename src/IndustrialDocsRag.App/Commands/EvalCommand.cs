using System.Text;
using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.App.Evaluation;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Eval;
using IndustrialDocsRag.Retrieval.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

/// <summary>
/// 跑评测矩阵，产出三张表。
/// <para>
/// <b>一次调用跑完整个矩阵</b>是刻意的：报告里的每个数字都必须由同一次运行产出。
/// 中途任何一次代码改动（哪怕只是破平局规则）都会让早期数字与后期数字不可比 ——
/// 这是 plan-week2.md §8.10 那条「换了措辞的探针不能与旧数据放进同一张表」的推广。
/// </para>
/// </summary>
public static class EvalCommand
{
    public static async Task<int> RunAsync(
        RagOptions baseOptions,
        string questionsPath,
        string corpusPath,
        string? configNames,
        bool validateOnly,
        string outPath,
        int topK,
        CancellationToken ct)
    {
        var questions = await EvalQuestionStore.LoadAsync(questionsPath, ct);

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine(
                $"找不到语料：{RepoPaths.ToDisplay(corpusPath)}。" +
                "评测需要完整语料，仓库自带的 15 块 fixture 撑不起 25 题。" +
                "先按 README「如何复现」跑 tools/pdf_to_md.py。");
            return 1;
        }

        var markdown = await File.ReadAllTextAsync(corpusPath, ct);

        // 解析失败一律硬失败：解析不出的锚点会让那道题对所有配置永远判 0，静默拉低整张表
        var anchors = AnchorResolver.Resolve(markdown, questions);
        var anchorsByQid = anchors.GroupBy(a => a.Qid)
                                  .ToDictionary(g => g.Key, g => (IReadOnlyList<ResolvedAnchor>)[.. g]);

        Console.WriteLine($"评测集 : {RepoPaths.ToDisplay(questionsPath)}  ({questions.Count} 题)");
        Console.WriteLine($"锚点   : {anchors.Count} 个，全部解析成功");

        if (validateOnly)
        {
            Console.WriteLine("--validate：只校验锚点，不跑检索。");
            return 0;
        }

        var configs = EvalConfig.Select(configNames);

        // ★ Qdrant 侧只建了 baseline 一个 collection。规范化组的 IndexPath 在 qdrant 后端下
        //   会被忽略，检索会静默落到 baseline 的稀疏向量上 —— 而模型文件的守卫拦不住这一类：
        //   它比的是「模型文件 vs 编码器」，不是「模型文件 vs collection 里实际写着的向量」。
        //   所以在这里显式拒绝。
        if (baseOptions.Backend != "memory")
        {
            var normOnly = configs.Where(c => c.Normalizer != "passthrough").Select(c => c.Name).ToList();
            if (normOnly.Count > 0)
            {
                Console.Error.WriteLine(
                    $"配置 {string.Join(" / ", normOnly)} 需要规范化组的索引，而 --backend {baseOptions.Backend} " +
                    "下只有 baseline 一套。Qdrant 只用于跑复核表：" +
                    "--backend qdrant --configs dense,sparse,hybrid-rrf");
                return 1;
            }
        }

        var outcomes = new Dictionary<string, IReadOnlyList<QuestionOutcome>>(StringComparer.Ordinal);

        foreach (var config in configs)
        {
            Console.WriteLine();
            Console.WriteLine($"→ {config.Name}");
            outcomes[config.Name] = await RunConfigAsync(
                baseOptions, config, questions, anchorsByQid, topK, ct);
        }

        await WriteReportAsync(outPath, configs, outcomes, topK, ct);
        Console.WriteLine();
        Console.WriteLine($"报告已写入 {RepoPaths.ToDisplay(outPath)}");
        return 0;
    }

    private static async Task<IReadOnlyList<QuestionOutcome>> RunConfigAsync(
        RagOptions baseOptions,
        EvalConfig config,
        IReadOnlyList<EvalQuestion> questions,
        IReadOnlyDictionary<string, IReadOnlyList<ResolvedAnchor>> anchorsByQid,
        int topK,
        CancellationToken ct)
    {
        // 每个配置一套独立的 options 与容器 —— 配置之间不能互相污染
        var options = CompositionRoot.LoadOptions(new Dictionary<string, string?>());
        options.Backend = baseOptions.Backend;
        options.Embedding.Provider = baseOptions.Embedding.Provider;
        options.Embedding.ApiKey = baseOptions.Embedding.ApiKey;
        options.Qdrant = baseOptions.Qdrant;
        options.FusionKind = config.Fusion;
        options.FusionAlpha = config.Alpha;
        options.NormalizerKind = config.Normalizer;

        await using var services = CompositionRoot.Build(options, config.Retriever);

        var indexPath = RepoPaths.Resolve(config.IndexPath);

        // shuffle 只有内存路径（ShuffleRetriever 绑 InMemoryChunkIndex.Items），所以主表全跑内存。
        if (options.Backend == "memory")
        {
            await services.GetRequiredService<InMemoryChunkIndex>().LoadAsync(indexPath, ct);
        }

        await SparseModelLoader.LoadIfNeededAsync(services, RepoPaths.Resolve(config.SparseModelPath), ct);

        var retriever = services.GetRequiredService<IRetriever>();
        var results = new List<QuestionOutcome>(questions.Count);

        foreach (var question in questions)
        {
            // 注意：不在这里调 ITermNormalizer。规范化只发生在 Bm25SparseEncoder 内部，
            // 在这里调会把稠密侧的查询也改掉，规范化 A/B 就不是单变量实验了。
            var hits = await retriever.RetrieveAsync(
                new RetrievalQuery { Text = question.Query, TopK = topK }, ct);

            results.Add(new QuestionOutcome(
                question.Qid,
                question.Stratum,
                EvalMetrics.FirstHitRank(hits, anchorsByQid.GetValueOrDefault(question.Qid, []))));
        }

        var summary = EvalMetrics.Aggregate(config.Name, results, topK);
        Console.WriteLine($"   Recall@{topK} = {summary.OverallRecall:P0}   MRR = {summary.MeanReciprocalRank:F3}");
        return results;
    }

    private static async Task WriteReportAsync(
        string outPath,
        IReadOnlyList<EvalConfig> configs,
        IReadOnlyDictionary<string, IReadOnlyList<QuestionOutcome>> outcomes,
        int topK,
        CancellationToken ct)
    {
        var summaries = configs
            .Select(c => EvalMetrics.Aggregate(c.Name, outcomes[c.Name], topK))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Week3 评测报告（Recall@{topK}）");
        sb.AppendLine();
        sb.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## 方法论");
        sb.AppendLine();
        sb.AppendLine("- ground truth 绑「页码 + 关键串」锚点，判命中看召回块的 `[CharOffset, CharOffset+SourceLength)` 是否覆盖锚点");
        sb.AppendLine("- 名次口径：分数降序，**并列时按 chunk id 升序破平局**（Week2 §8.9：内存破平局、Qdrant 不破，同批数据第 3/4 名互换）");
        sb.AppendLine($"- n={outcomes.Values.First().Count}，聚合百分比的 95% CI 约 ±19pp，**总计一列不作结论**，请看分层与逐题配对");
        sb.AppendLine("- 候选池外的相关块会漏标，召回率被系统性高估（pooling 的已知局限）");
        sb.AppendLine();
        sb.AppendLine("## 表 1 — 分层召回率");
        sb.AppendLine();
        sb.Append(EvalReportWriter.StratumTable(summaries));
        sb.AppendLine();

        var pairs = new (string Left, string Right)[]
        {
            ("hybrid-rrf", "dense"),
            ("hybrid-rrf", "sparse"),
            ("norm-hybrid-rrf", "hybrid-rrf"),
            ("norm-sparse", "sparse"),
            ("norm-dense", "dense"),
        };

        var diffs = pairs
            .Where(p => outcomes.ContainsKey(p.Left) && outcomes.ContainsKey(p.Right))
            .Select(p => EvalMetrics.Pairwise(p.Left, outcomes[p.Left], p.Right, outcomes[p.Right], topK))
            .ToList();

        if (diffs.Count > 0)
        {
            sb.AppendLine("## 表 2 — 逐题配对胜负");
            sb.AppendLine();
            sb.Append(EvalReportWriter.PairwiseTable(diffs));
            sb.AppendLine();
            sb.AppendLine("> `norm-dense vs dense` 必须是「无差异」。有差异说明规范化泄漏到了稠密侧，规范化组的全部数字作废。");
            sb.AppendLine();
        }

        var fusionRows = summaries.Where(s => s.ConfigName.StartsWith("hybrid", StringComparison.Ordinal)).ToList();
        if (fusionRows.Count > 0)
        {
            sb.AppendLine("## 表 3 — 融合方式对比");
            sb.AppendLine();
            sb.Append(EvalReportWriter.StratumTable(fusionRows));
            sb.AppendLine();
            sb.AppendLine("> plan-week1.md §7.4 的预言：RRF 只用排名、丢弃原始分数，而错误码精确匹配恰恰是 sparse 分数畸高的场景。");
            sb.AppendLine("> 看 `FaultCode` 与 `Notation` 两层 —— 证实证伪都要如实写。");
            sb.AppendLine();
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(outPath, sb.ToString(), ct);
    }
}
