using IndustrialDocsRag.App;
using IndustrialDocsRag.App.Configuration;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Retrieval.Sparse;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// ★ 锁死「规范化不泄漏到稠密侧」这个不变量。
/// <para>
/// RetrieveCommand / FixedPipelineAnswerService / SkAnswerService / IngestionPipeline
/// 各有一处 <c>normalizer.Normalize(text)</c>，拿的是 DI 里的 ITermNormalizer，
/// 而改写后的文本会同时喂给稠密侧和稀疏侧。若把 HexTermNormalizer 注册成 DI 里的
/// ITermNormalizer，稠密侧的查询也会被改写，规范化 A/B 就不是单变量实验了 ——
/// 而且失效方式是「规范化组看起来有收益」，正是本项目最该防的那种。
/// </para>
/// <para>
/// 有了这条测试，泄漏在单测阶段就被拦下，不用等跑完整套评测才从数字里看出来。
/// </para>
/// </summary>
public class CompositionRootNormalizerTests
{
    private static RagOptions Options(string normalizerKind) => new()
    {
        NormalizerKind = normalizerKind,
        Backend = "memory",
        Embedding = { Provider = "fake" },
    };

    [Fact]
    public void 开启hex规范化后DI里的ITermNormalizer仍是passthrough()
    {
        using var services = CompositionRoot.Build(Options("hex"), "sparse");

        Assert.Same(PassthroughTermNormalizer.Instance, services.GetRequiredService<ITermNormalizer>());
    }

    [Fact]
    public void 开启hex规范化后稀疏编码器用的是hex()
    {
        using var services = CompositionRoot.Build(Options("hex"), "sparse");

        Assert.Equal("hex-unify", services.GetRequiredService<Bm25SparseEncoder>().NormalizerName);
    }

    [Fact]
    public void 默认不开规范化时两侧都是passthrough()
    {
        using var services = CompositionRoot.Build(Options("passthrough"), "sparse");

        Assert.Same(PassthroughTermNormalizer.Instance, services.GetRequiredService<ITermNormalizer>());
        Assert.Equal("passthrough", services.GetRequiredService<Bm25SparseEncoder>().NormalizerName);
    }

    [Fact]
    public void 未知的规范化器名硬失败()
    {
        using var services = CompositionRoot.Build(Options("nope"), "sparse");

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<Bm25SparseEncoder>());

        Assert.Contains("nope", ex.Message);
    }
}
