using System.Text;
using IndustrialDocsRag.App.Commands;
using IndustrialDocsRag.App.Configuration;

namespace IndustrialDocsRag.App;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await DispatchAsync(args, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args, CancellationToken ct)
    {
        var (command, positional, flags) = ParseArgs(args);

        if (command is null || flags.ContainsKey("help") || flags.ContainsKey("h"))
        {
            PrintUsage();
            return command is null ? 1 : 0;
        }

        var options = CompositionRoot.LoadOptions(new Dictionary<string, string?>());

        // 允许命令行覆盖嵌入服务，方便在真实端点和离线假向量之间来回切换做对比。
        // 注意切换后必须重新 ingest —— InMemoryChunkIndex.EnsureCompatibleWith 会拦下不匹配的组合。
        if (Flag(flags, "embedder") is { } embedder)
        {
            options.Embedding.Provider = embedder;
        }

        if (Flag(flags, "answerer") is { } answerer)
        {
            options.AnswererKind = answerer;
        }

        if (Flag(flags, "backend") is { } backend)
        {
            options.Backend = backend;
        }

        // 融合方式是 Week3 的一条实验维度，命令行可直接切换
        if (Flag(flags, "fusion") is { } fusion)
        {
            options.FusionKind = fusion;
        }

        if (Flag(flags, "alpha") is { } alpha && double.TryParse(alpha, out var parsedAlpha))
        {
            options.FusionAlpha = parsedAlpha;
        }

        switch (command)
        {
            case "ingest":
            {
                var chunksPath = RepoPaths.Resolve(Flag(flags, "input") ?? options.ChunksPath);
                var indexPath = RepoPaths.Resolve(Flag(flags, "index") ?? options.IndexPath);

                var sparseModelPath = SparseModelPath(flags, options);
                var emitChunks = Flag(flags, "emit-chunks") is { } e ? RepoPaths.Resolve(e) : null;

                // ingest 不需要检索器，但组合根要求给一个 —— 用默认值即可
                await using var services = CompositionRoot.Build(options, options.RetrieverKind);
                return await IngestCommand.RunAsync(
                    services, chunksPath, indexPath, sparseModelPath, Flag(flags, "doc-id"), emitChunks, ct);
            }

            case "retrieve":
            {
                if (positional.Count == 0)
                {
                    Console.Error.WriteLine("retrieve 需要一个查询串。例：idr retrieve \"0x6041 是什么\"");
                    return 1;
                }

                var indexPath = RepoPaths.Resolve(Flag(flags, "index") ?? options.IndexPath);
                var retriever = Flag(flags, "retriever") ?? options.RetrieverKind;
                var topK = Flag(flags, "topk") is { } t && int.TryParse(t, out var parsed) ? parsed : options.TopK;

                await using var services = CompositionRoot.Build(options, retriever, ShuffleSeed(flags));
                return await RetrieveCommand.RunAsync(
                    services, indexPath, SparseModelPath(flags, options), positional[0], topK, DumpPath(flags), ct);
            }

            case "ask":
            {
                if (positional.Count == 0)
                {
                    Console.Error.WriteLine("ask 需要一个问题。例：idr ask \"E201.2 的辅助码是多少\"");
                    return 1;
                }

                var indexPath = RepoPaths.Resolve(Flag(flags, "index") ?? options.IndexPath);
                var retriever = Flag(flags, "retriever") ?? options.RetrieverKind;

                if (Flag(flags, "topk") is { } tk && int.TryParse(tk, out var k))
                {
                    options.TopK = k;
                }

                await using var services = CompositionRoot.Build(options, retriever, ShuffleSeed(flags));
                return await AskCommand.RunAsync(
                    services, indexPath, SparseModelPath(flags, options), positional[0], DumpPath(flags), ct);
            }

            default:
                Console.Error.WriteLine($"未知命令：{command}");
                PrintUsage();
                return 1;
        }
    }

    private static string? Flag(IReadOnlyDictionary<string, string> flags, string name)
        => flags.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// ShuffleRetriever 的随机种子。
    /// 语料只有十几块时，随机抽 K 块有 K/N 的概率误打误撞抽中正确答案，
    /// 单跑一次的对照结论不可信 —— 换几个 seed 多跑几轮才说明问题。
    /// </summary>
    private static int ShuffleSeed(IReadOnlyDictionary<string, string> flags)
        => Flag(flags, "seed") is { } s && int.TryParse(s, out var seed) ? seed : 20260801;

    private static string SparseModelPath(IReadOnlyDictionary<string, string> flags, RagOptions options)
        => RepoPaths.Resolve(Flag(flags, "sparse-model") ?? options.SparseModelPath);

    private static string? DumpPath(IReadOnlyDictionary<string, string> flags)
        => Flag(flags, "dump-topk") is { } p ? RepoPaths.Resolve(p) : null;

    /// <summary>
    /// 极简参数解析：<c>--name value</c> 与 <c>--name=value</c> 两种写法，其余算位置参数。
    /// 两个子命令还不值得引入一个命令行框架，等命令多起来再换。
    /// </summary>
    private static (string? Command, List<string> Positional, Dictionary<string, string> Flags) ParseArgs(string[] args)
    {
        string? command = null;
        var positional = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var body = arg[2..];
                var eq = body.IndexOf('=', StringComparison.Ordinal);

                if (eq >= 0)
                {
                    flags[body[..eq]] = body[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    flags[body] = args[++i];
                }
                else
                {
                    flags[body] = string.Empty;   // 开关型参数
                }
            }
            else if (command is null)
            {
                command = arg;
            }
            else
            {
                positional.Add(arg);
            }
        }

        return (command, positional, flags);
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        industrial-docs-rag — 工业设备手册检索增强问答

        用法：
          idr ingest   [--input <chunks.jsonl|manual.md>] [--index <index.json>] [--embedder remote|fake]
                       [--doc-id <id>] [--emit-chunks <文件>]
              读入块，嵌入后写索引并落盘。
              --input 给 .md 走自动切分（表格行上下文化 + 页码锚点），给 .jsonl 读手工切好的块。
              --emit-chunks 把切分结果落成 jsonl 供人眼抽样审阅。

          idr retrieve <查询串> [--topk N] [--retriever dense|sparse|hybrid|shuffle] [--seed N]
              只跑检索、不接生成模型，直接打印召回结果与分数。

          idr ask      <问题> [--topk N] [--retriever dense|sparse|hybrid|shuffle] [--seed N]
              完整问答：检索 → 填 prompt → 流式生成 → 引用表。
              答案里的 [chunk-id] 会与召回集求交集，模型编造的编号会被丢弃并单独报出。

        公共开关：
        --embedder remote  调用 BGE-M3 远程端点（默认，需要 API Key）
        --embedder fake    离线哈希词袋，不调外部服务；换了之后必须重新 ingest。
        --dump-topk <文件> 把本次召回追加写成 jsonl，供 Week3 的 pooling 标注。

        --answerer fixed|agent   fixed 是固定流水线（检索一次再作答）；
                                 agent 把检索包成 search_manual 交给 LLM 自主调用，
                                 一轮问答里可能出现多次检索。

        --backend memory|qdrant  存储后端。qdrant 需先 `docker compose up -d`。
                                 两者的检索器种类完全一样，换后端只是一行开关。

        检索器：
        dense    BGE-M3 稠密向量余弦。强在语义（0x6041 ↔ 手册里的 6041），弱在精确术语。
        sparse   自建 BM25。强在精确术语（E201.2 / 0x22010201 这类稀有词 IDF 极高），弱在语义。
        hybrid   两路都跑再融合。--fusion rrf（默认）或 weighted，后者用 --alpha 调稠密侧权重。
        shuffle  对照组，随机返回块，用来证明检索真的在起作用。

        BM25 的词表与 IDF 落在 --sparse-model（默认 data/bm25.week2.json）。
        这个文件同时是索引清单：它记录产出稠密向量的嵌入服务名与维度，不匹配直接硬失败。

        --retriever shuffle 是对照组：随机返回块，用来证明检索真的在起作用。
        注意它有统计功效问题：随机抽 K 块时，正确那块被误抽中的概率是 K/N，
        当前 15 块 / TopK=5 时高达 1/3。先用 retrieve 换几个 --seed 确认抽出的块
        不含正确答案，再拿这个 seed 去跑 ask，对照结论才可信。

        Key 通过 user-secrets 配置，不写进任何仓库文件：
          dotnet user-secrets set "Rag:Embedding:ApiKey" "<key>" --project src/IndustrialDocsRag.App

        路径中的相对路径按仓库根目录解析，不受当前工作目录影响。
        """);
}
