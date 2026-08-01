using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.App.Diagnostics;
using IndustrialDocsRag.Core.Answering;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App.Commands;

public static class AskCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        string indexPath,
        string question,
        string? dumpTopKPath,
        CancellationToken ct)
    {
        var index = services.GetRequiredService<InMemoryChunkIndex>();
        await index.LoadAsync(indexPath, ct);

        var answerer = services.GetRequiredService<IAnswerService>();
        var retriever = services.GetRequiredService<IRetriever>();
        var options = services.GetRequiredService<RagOptions>();

        Console.WriteLine($"索引   : {RepoPaths.ToDisplay(indexPath)}  ({await index.CountAsync(ct)} 块, 由 {index.EmbeddingServiceName} 产出)");
        Console.WriteLine($"检索器 : {retriever.Name}");
        Console.WriteLine($"生成   : {options.Chat.Model} @ {options.Chat.Endpoint}");
        Console.WriteLine();
        Console.WriteLine($"> {question}");
        Console.WriteLine();

        var failed = false;

        await foreach (var evt in answerer.AskAsync(question, ct))
        {
            switch (evt)
            {
                case AnswerEvent.RetrievalStarted e:
                    WriteDim($"  → 检索: \"{e.QueryText}\"");
                    break;

                case AnswerEvent.RetrievalCompleted e:
                    var scores = string.Join(" / ", e.Hits.Select(h => h.Score.ToString("F4")));
                    WriteDim($"  ← 命中 {e.Hits.Count} 块  ({scores})");
                    foreach (var h in e.Hits)
                    {
                        WriteDim($"      {h.Chunk.Id}  p.{h.Chunk.PageNumber}");
                    }

                    if (dumpTopKPath is not null)
                    {
                        await TopKDumper.AppendAsync(dumpTopKPath, question, retriever.Name, e.Hits, ct);
                        WriteDim($"  ⤓ Top-K 已追加写入 {RepoPaths.ToDisplay(dumpTopKPath)}");
                    }

                    Console.WriteLine();
                    break;

                case AnswerEvent.TextDelta e:
                    Console.Write(e.Text);
                    break;

                case AnswerEvent.Completed e:
                    Console.WriteLine();
                    PrintCitations(e);
                    break;

                case AnswerEvent.Failed e:
                    failed = true;
                    Console.WriteLine();
                    WriteColored($"  ✗ {e.Message}", ConsoleColor.Red, toStdErr: true);
                    break;
            }
        }

        return failed ? 1 : 0;
    }

    private static void PrintCitations(AnswerEvent.Completed e)
    {
        Console.WriteLine();

        if (e.Citations.Count == 0)
        {
            WriteDim("  （答案中没有有效引用）");
        }
        else
        {
            Console.WriteLine("引用：");
            for (var i = 0; i < e.Citations.Count; i++)
            {
                var c = e.Citations[i];
                var page = c.PageNumber is { } p ? $"p.{p}" : "无页码";
                Console.WriteLine($"  [{i + 1}] {c.ChunkId}");
                Console.WriteLine($"      {c.SourceFile} | {page} | {c.SectionPath}");
                WriteDim($"      {c.Quote}");
            }
        }

        // 编造的引用要显式报出来，不能静默丢弃 ——
        // 读者以为每句话都有出处，实际出处指向不存在的块，这比没有引用更危险。
        if (e.RejectedIds.Count > 0)
        {
            Console.WriteLine();
            WriteColored(
                $"  ⚠ 丢弃了 {e.RejectedIds.Count} 个模型编造的引用编号：{string.Join(", ", e.RejectedIds)}",
                ConsoleColor.Yellow);
        }
    }

    private static void WriteDim(string text) => WriteColored(text, ConsoleColor.DarkGray);

    private static void WriteColored(string text, ConsoleColor color, bool toStdErr = false)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        if (toStdErr)
        {
            Console.Error.WriteLine(text);
        }
        else
        {
            Console.WriteLine(text);
        }

        Console.ForegroundColor = prev;
    }
}
