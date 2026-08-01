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
using IndustrialDocsRag.Retrieval.InMemory;
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

        // 规范化：Week1 恒等实现，入库侧和查询侧共用同一个实例，保证两侧对称。
        services.AddSingleton<ITermNormalizer>(PassthroughTermNormalizer.Instance);

        services.AddSingleton<ISparseEncoder>(NullSparseEncoder.Instance);

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
        services.AddSingleton<IChunkIndex>(sp => sp.GetRequiredService<InMemoryChunkIndex>());

        // ↓ 这一处 switch 就是 ShuffleRetriever 对照实验的全部改动量
        services.AddSingleton<IRetriever>(sp => retrieverKind switch
        {
            "dense" => new InMemoryVectorRetriever(
                sp.GetRequiredService<InMemoryChunkIndex>(),
                sp.GetRequiredService<IEmbeddingService>()),

            "shuffle" => new ShuffleRetriever(sp.GetRequiredService<InMemoryChunkIndex>(), shuffleSeed),

            var other => throw new InvalidOperationException(
                $"未知的检索器：{other}（可选 dense / shuffle）"),
        });

        // Kernel 和问答服务都是惰性构造的：只有 ask 命令会去解析它们，
        // 所以 ingest / retrieve 在没有配置生成模型 Key 的情况下照样能跑。
        services.AddSingleton(_ => KernelFactory.Create(options));

        services.AddSingleton<IAnswerService>(sp => new FixedPipelineAnswerService(
            sp.GetRequiredService<Kernel>(),
            sp.GetRequiredService<IRetriever>(),
            sp.GetRequiredService<ITermNormalizer>(),
            options));

        return services.BuildServiceProvider();
    }
}
