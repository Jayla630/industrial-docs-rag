using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

/// <summary>
/// 只跑检索、不接生成模型，把召回结果和分数直接打出来。
/// <para>
/// 这个命令的存在是刻意的：检索和生成同时接通再调试，出问题时分不清是召回差还是 prompt 差，
/// 而那是最烧时间的一类排查。先在这里确认召回本身是对的，再去接 LLM。
/// </para>
/// </summary>
public static class RetrieveCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string indexPath,
        string question,
        int topK,
        string? dumpTopKPath,
        CancellationToken ct)
    {
        var index = services.GetRequiredService<InMemoryChunkIndex>();
        await index.LoadAsync(indexPath, ct);

        var retriever = services.GetRequiredService<IRetriever>();
        var normalizer = services.GetRequiredService<ITermNormalizer>();

        // 与入库侧调用同一个 normalizer 实例，保证两侧对称
        var normalized = normalizer.Normalize(question);

        Console.WriteLine($"索引   : {RepoPaths.ToDisplay(indexPath)}  ({await index.CountAsync(ct)} 块, 由 {index.EmbeddingServiceName} 产出)");
        Console.WriteLine($"检索器 : {retriever.Name}");
        Console.WriteLine($"查询   : {normalized}");
        Console.WriteLine();

        var hits = await retriever.RetrieveAsync(new RetrievalQuery { Text = normalized, TopK = topK }, ct);

        if (dumpTopKPath is not null)
        {
            await Diagnostics.TopKDumper.AppendAsync(dumpTopKPath, question, retriever.Name, hits, ct);
            Console.WriteLine($"Top-K 已追加写入 {RepoPaths.ToDisplay(dumpTopKPath)}");
            Console.WriteLine();
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("没有召回任何块。");
            return 0;
        }

        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var page = h.Chunk.PageNumber is { } p ? $"p.{p}" : "无页码";

            Console.WriteLine($"[{i + 1}] {h.Score:F4}  {h.Chunk.Id}");
            Console.WriteLine($"     {page} | {h.Chunk.SectionPath} | {h.Chunk.Kind} | tag={h.RetrieverTag}");
            Console.WriteLine($"     {Preview(h.Chunk.Text, 90)}");
            Console.WriteLine();
        }

        return 0;
    }

    private static string Preview(string text, int maxLength)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= maxLength ? flat : string.Concat(flat.AsSpan(0, maxLength), "…");
    }
}
