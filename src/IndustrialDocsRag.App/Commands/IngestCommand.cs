using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Ingestion;
using IndustrialDocsRag.Retrieval.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

public static class IngestCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string chunksPath,
        string indexPath,
        CancellationToken ct)
    {
        var embedder = services.GetRequiredService<IEmbeddingService>();
        var index = services.GetRequiredService<InMemoryChunkIndex>();

        Console.WriteLine($"块文件 : {RepoPaths.ToDisplay(chunksPath)}");
        Console.WriteLine($"嵌入   : {embedder.Name}  ({embedder.DenseDimension} 维, sparse={embedder.ProducesSparse})");
        Console.WriteLine();

        var pipeline = new IngestionPipeline(
            new JsonlChunkSource(chunksPath),
            services.GetRequiredService<ITermNormalizer>(),
            embedder,
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

        await index.SaveAsync(indexPath, embedder.Name, ct);

        Console.WriteLine($"{report.ChunkCount} chunks embedded, index saved to {RepoPaths.ToDisplay(indexPath)}");
        Console.WriteLine($"耗时 {report.ElapsedMs} ms");
        return 0;
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
