using System.Text.Json;
using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Serialization;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Ingestion;
using IndustrialDocsRag.Retrieval.InMemory;
using IndustrialDocsRag.Retrieval.Sparse;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

public static class IngestCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string chunksPath,
        string indexPath,
        string sparseModelPath,
        string? docId,
        string? emitChunksPath,
        CancellationToken ct)
    {
        var embedder = services.GetRequiredService<IEmbeddingService>();
        var index = services.GetRequiredService<InMemoryChunkIndex>();
        var sparseEncoder = services.GetRequiredService<Bm25SparseEncoder>();
        var source = CreateSource(chunksPath, docId);

        Console.WriteLine($"块来源 : {RepoPaths.ToDisplay(chunksPath)}  ({source.Name})");
        Console.WriteLine($"嵌入   : {embedder.Name}  ({embedder.DenseDimension} 维, sparse={embedder.ProducesSparse})");
        Console.WriteLine();

        if (emitChunksPath is not null)
        {
            var written = await DumpChunksAsync(source, emitChunksPath, ct);
            Console.WriteLine($"切分结果已写出 {written} 块 → {RepoPaths.ToDisplay(emitChunksPath)}");
            Console.WriteLine();
        }

        var pipeline = new IngestionPipeline(
            source,
            services.GetRequiredService<ITermNormalizer>(),
            embedder,
            sparseEncoder,
            services.GetRequiredService<IChunkIndex>());

        var progress = new Progress<IngestionProgress>(p =>
            Console.WriteLine(p.Total > 0 ? $"  [{p.Stage}] {p.Done}/{p.Total}" : $"  [{p.Stage}]"));

        var report = await pipeline.RunAsync(progress, ct);

        Console.WriteLine();
        PrintIssues(report);

        if (report.HasErrors)
        {
            Console.Error.WriteLine("存在错误，未写入索引。修正后重试。");
            return 1;
        }

        if (services.GetRequiredService<RagOptions>().Backend == "memory")
        {
            await index.SaveAsync(indexPath, embedder.Name, ct);
        }

        // BM25 的词表与 IDF 必须落盘：不落盘，重启后查询侧与入库侧的权重就对不上，
        // 检索结果静默劣化 —— 不报错，只是变差。这个文件同时充当索引清单。
        await SparseModelStore.SaveAsync(
            sparseModelPath, sparseEncoder, embedder.Name, embedder.DenseDimension, ct);

        var backend = services.GetRequiredService<RagOptions>().Backend;
        var where = backend == "memory" ? RepoPaths.ToDisplay(indexPath) : $"qdrant/{services.GetRequiredService<RagOptions>().Qdrant.Collection}";
        Console.WriteLine($"{report.ChunkCount} chunks embedded, index saved to {where}");
        Console.WriteLine($"稀疏   : {report.SparseEncoderName}  (词表 {sparseEncoder.Model?.Vocabulary.Count ?? 0} 词, 平均 {report.SparseNonZeroAverage} 非零/块)");
        Console.WriteLine($"        → {RepoPaths.ToDisplay(sparseModelPath)}");
        Console.WriteLine($"耗时 {report.ElapsedMs} ms");
        return 0;
    }

    /// <summary>
    /// 按扩展名选来源：<c>.md</c> 走自动切分，其余按 Week1 的手工 jsonl 读。
    /// 两条路产出同一 schema，所以 15 块的 fixture 与上千块的全量语料随时可以互换着跑。
    /// </summary>
    private static IChunkSource CreateSource(string path, string? docId)
        => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
            ? new MarkdownChunkSource(path, docId ?? Path.GetFileNameWithoutExtension(path))
            : new JsonlChunkSource(path);

    /// <summary>
    /// 把切分结果落成 jsonl 供人眼审阅。上千块的切分器不可能靠脑补验证，必须能抽样看。
    /// </summary>
    private static async Task<int> DumpChunksAsync(IChunkSource source, string path, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var writer = new StreamWriter(path, append: false);
        var count = 0;

        await foreach (var chunk in source.ReadAsync(ct))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(chunk, JsonDefaults.Options));
            count++;
        }

        return count;
    }

    private static void PrintIssues(IngestionReport report)
    {
        if (report.Issues.Count == 0)
        {
            Console.WriteLine("校验通过，无问题。");
            return;
        }

        foreach (var issue in report.Issues)
        {
            var tag = issue.Severity == ValidationSeverity.Error ? "错误" : "警告";
            var color = issue.Severity == ValidationSeverity.Error ? ConsoleColor.Red : ConsoleColor.Yellow;

            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"  [{tag}] {issue.ChunkId}: {issue.Message}");
            Console.ForegroundColor = previous;
        }

        Console.WriteLine();
    }
}
