using IndustrialDocsRag.App.Answering;
using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.App.Orchestration;
using IndustrialDocsRag.Core.Answering;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;
using Microsoft.SemanticKernel;
using IndustrialDocsRag.Retrieval.Diagnostics;
using IndustrialDocsRag.Retrieval.Embeddings;
using IndustrialDocsRag.Retrieval.Fusion;
using IndustrialDocsRag.Retrieval.InMemory;
using IndustrialDocsRag.Retrieval.Normalization;
using IndustrialDocsRag.Retrieval.Qdrant;
using IndustrialDocsRag.Retrieval.Sparse;
using Qdrant.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.App;

/// <summary>
/// 组合根。整个项目"检索层可替换"的主张，在代码上就体现为这里的几行注册 ——
/// 换检索器、换嵌入服务，其余代码一行不动。
/// </summary>
public static class CompositionRoot
{
    public static RagOptions LoadOptions(IReadOnlyDictionary<string, string?> overrides)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(typeof(CompositionRoot).Assembly, optional: true)
            .AddEnvironmentVariables("IDR_")
            .AddInMemoryCollection(overrides)
            .Build();

        var options = new RagOptions();
        config.GetSection(RagOptions.SectionName).Bind(options);
        return options;
    }

    public static ServiceProvider Build(RagOptions options, string retrieverKind, int shuffleSeed = 20260801)
    {
        var services = new ServiceCollection();

        services.AddSingleton(options);

        // ★ DI 里的 ITermNormalizer 恒为 passthrough，不受 --normalizer 影响。
        //
        //   RetrieveCommand / FixedPipelineAnswerService / SkAnswerService / IngestionPipeline
        //   各有一处 normalizer.Normalize(text)，而改写后的文本会同时喂给稠密侧和稀疏侧。
        //   把 hex 规范化器注册到这里，稠密侧的查询也会被改写 —— 规范化 A/B 就不是单变量实验了，
        //   而且失效方式是「规范化组看起来有收益」。
        //
        //   CompositionRootNormalizerTests 锁死了这个不变量。
        services.AddSingleton<ITermNormalizer>(PassthroughTermNormalizer.Instance);

        // 稀疏侧的规范化器单独选，只经由 Bm25SparseEncoder 的构造参数进入系统。
        // 注入到编码器里，入库侧与查询侧的对称性就由构造保证，不靠调用点各自记得调。
        services.AddSingleton(_ => new Bm25SparseEncoder(options.NormalizerKind switch
        {
            "passthrough" => PassthroughTermNormalizer.Instance,
            "hex" => new HexTermNormalizer(),
            var other => throw new InvalidOperationException(
                $"未知的规范化器：{other}（可选 passthrough / hex）"),
        }));
        services.AddSingleton<ISparseEncoder>(sp => sp.GetRequiredService<Bm25SparseEncoder>());

        services.AddSingleton<IScoreFusion>(_ => options.FusionKind switch
        {
            "rrf" => new RrfFusion(),
            "weighted" => new WeightedFusion(options.FusionAlpha),
            var other => throw new InvalidOperationException(
                $"未知的融合方式：{other}（可选 rrf / weighted）"),
        });

        services.AddSingleton<IEmbeddingService>(_ => options.Embedding.Provider switch
        {
            "fake" => new FakeEmbeddingService(options.Embedding.DenseDimension),

            "remote" => new RemoteDenseEmbeddingService(
                options.Embedding.Endpoint,
                options.Embedding.Model,
                options.Embedding.ApiKey ?? string.Empty,
                options.Embedding.DenseDimension,
                options.Embedding.BatchSize),

            var other => throw new InvalidOperationException(
                $"未知的嵌入服务：{other}（可选 fake / remote）"),
        });

        services.AddSingleton<InMemoryChunkIndex>();

        // Qdrant 相关的都是惰性构造：backend=memory 时根本不会去连它，
        // 没装 Docker 的人照样能用 15 条 fixture 跑通全链路。
        services.AddSingleton(_ => new QdrantOptions
        {
            Host = options.Qdrant.Host,
            Port = options.Qdrant.Port,
            Collection = options.Qdrant.Collection,
        });

        services.AddSingleton(sp =>
        {
            var q = sp.GetRequiredService<QdrantOptions>();
            return new QdrantClient(q.Host, q.Port);
        });

        services.AddSingleton(sp => new QdrantChunkIndex(
            sp.GetRequiredService<QdrantClient>(),
            sp.GetRequiredService<QdrantOptions>(),
            options.Embedding.DenseDimension));

        services.AddSingleton<IChunkIndex>(sp => options.Backend switch
        {
            "memory" => sp.GetRequiredService<InMemoryChunkIndex>(),
            "qdrant" => sp.GetRequiredService<QdrantChunkIndex>(),
            var other => throw new InvalidOperationException(
                $"未知的后端：{other}（可选 memory / qdrant）"),
        });

        // ↓ 这两处 switch 就是「检索层可替换」的全部改动量：
        //   换检索器一行，换存储后端一行，其余代码零改动。
        services.AddSingleton<IRetriever>(sp => retrieverKind switch
        {
            "dense" => Dense(sp),
            "sparse" => Sparse(sp),

            // 融合在客户端做，所以这一行对内存索引和 Qdrant 是同一份代码
            "hybrid" => new HybridRetriever(Dense(sp), Sparse(sp), sp.GetRequiredService<IScoreFusion>()),

            "shuffle" => new ShuffleRetriever(sp.GetRequiredService<InMemoryChunkIndex>(), shuffleSeed),

            var other => throw new InvalidOperationException(
                $"未知的检索器：{other}（可选 dense / sparse / hybrid / shuffle）"),
        });

        IRetriever Dense(IServiceProvider sp) => options.Backend == "qdrant"
            ? new QdrantDenseRetriever(
                sp.GetRequiredService<QdrantClient>(),
                sp.GetRequiredService<QdrantOptions>(),
                sp.GetRequiredService<IEmbeddingService>())
            : new InMemoryVectorRetriever(
                sp.GetRequiredService<InMemoryChunkIndex>(),
                sp.GetRequiredService<IEmbeddingService>());

        IRetriever Sparse(IServiceProvider sp) => options.Backend == "qdrant"
            ? new QdrantSparseRetriever(
                sp.GetRequiredService<QdrantClient>(),
                sp.GetRequiredService<QdrantOptions>(),
                sp.GetRequiredService<ISparseEncoder>())
            : new InMemorySparseRetriever(
                sp.GetRequiredService<InMemoryChunkIndex>(),
                sp.GetRequiredService<ISparseEncoder>());

        // Kernel 和问答服务都是惰性构造的：只有 ask 命令会去解析它们，
        // 所以 ingest / retrieve 在没有配置生成模型 Key 的情况下照样能跑。
        services.AddSingleton(_ => KernelFactory.Create(options));

        services.AddSingleton<IAnswerService>(sp => options.AnswererKind switch
        {
            "fixed" => new FixedPipelineAnswerService(
                sp.GetRequiredService<Kernel>(),
                sp.GetRequiredService<IRetriever>(),
                sp.GetRequiredService<ITermNormalizer>(),
                options),

            // 注意传的是工厂而不是 Kernel 实例：每次 AskAsync 要新建一个，
            // 否则插件里暂存的召回集会跨轮污染（见 ManualSearchPlugin 的注释）。
            "agent" => new SkAnswerService(
                options,
                sp.GetRequiredService<IRetriever>(),
                sp.GetRequiredService<ITermNormalizer>(),
                () => KernelFactory.Create(options)),

            var other => throw new InvalidOperationException(
                $"未知的问答实现：{other}（可选 fixed / agent）"),
        });

        return services.BuildServiceProvider();
    }
}
