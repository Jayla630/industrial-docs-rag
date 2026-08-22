using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Retrieval.Embeddings;
using IndustrialDocsRag.Retrieval.Normalization;
using IndustrialDocsRag.Retrieval.Sparse;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 这个文件是索引清单。它守的都是「不报错、只是变差」的静默失效：
/// 用假向量灌库后换真实端点、用规范化训练的词表配不带规范化的查询侧。
/// </summary>
public class SparseModelStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bm25-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        GC.SuppressFinalize(this);
    }

    private static Bm25SparseEncoder Trained(ITermNormalizer normalizer)
    {
        var encoder = new Bm25SparseEncoder(normalizer);
        encoder.Fit(["对象 0x6041 状态字", "参数 H06-08 速度正向限制"]);
        return encoder;
    }

    [Fact]
    public async Task 落盘并加载后统计量一致()
    {
        var encoder = Trained(PassthroughTermNormalizer.Instance);
        await SparseModelStore.SaveAsync(_path, encoder, "fake:1024", 1024, "passthrough");

        var loaded = await SparseModelStore.LoadAsync(_path);

        Assert.Equal(encoder.Model!.DocumentCount, loaded.Model.DocumentCount);
        Assert.Equal(encoder.Model.Vocabulary.Count, loaded.Model.Vocabulary.Count);
        Assert.Equal("passthrough", loaded.NormalizerName);
    }

    /// <summary>
    /// ★ 本任务的核心守卫：用规范化训练出来的词表，配一个不带规范化的查询侧，
    /// 结果不是报错而是词面对不上、分数静默变差。必须硬失败。
    /// </summary>
    [Fact]
    public async Task 规范化器不匹配时硬失败()
    {
        var encoder = Trained(new HexTermNormalizer());
        await SparseModelStore.SaveAsync(_path, encoder, new FakeEmbeddingService(1024).Name, 1024, "hex-unify");

        var loaded = await SparseModelStore.LoadAsync(_path);

        var ex = Assert.Throws<InvalidOperationException>(() => loaded.EnsureCompatibleWith(
            new FakeEmbeddingService(1024), PassthroughTermNormalizer.Instance));

        Assert.Contains("hex-unify", ex.Message);
        Assert.Contains("passthrough", ex.Message);
        Assert.Contains("ingest", ex.Message);
    }

    [Fact]
    public async Task 规范化器匹配时通过()
    {
        var encoder = Trained(new HexTermNormalizer());
        await SparseModelStore.SaveAsync(_path, encoder, new FakeEmbeddingService(1024).Name, 1024, "hex-unify");

        var loaded = await SparseModelStore.LoadAsync(_path);

        loaded.EnsureCompatibleWith(new FakeEmbeddingService(1024), new HexTermNormalizer());
    }

    /// <summary>Week2 就有的守卫不能因为改格式而弄丢。</summary>
    [Fact]
    public async Task 嵌入服务不匹配时仍然硬失败()
    {
        var encoder = Trained(PassthroughTermNormalizer.Instance);
        await SparseModelStore.SaveAsync(_path, encoder, "remote:BAAI/bge-m3", 1024, "passthrough");

        var loaded = await SparseModelStore.LoadAsync(_path);

        Assert.Throws<InvalidOperationException>(() => loaded.EnsureCompatibleWith(
            new FakeEmbeddingService(1024), PassthroughTermNormalizer.Instance));
    }

    /// <summary>
    /// 旧格式（v1，没有 normalizerName）必须硬失败并指向 `idr ingest`，
    /// 不能默默当成 passthrough —— 那样守卫等于没加。
    /// </summary>
    [Fact]
    public async Task 旧格式文件硬失败()
    {
        await File.WriteAllTextAsync(_path,
            """
            {"version":1,"tokenizerName":"industrial-v1","embeddingServiceName":"fake:1024",
             "denseDimension":1024,"createdAt":"2026-08-01T00:00:00+08:00",
             "model":{"vocabulary":{},"documentFrequency":{},"documentCount":0,
                      "averageLength":0,"k1":1.2,"b":0.75}}
            """);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => SparseModelStore.LoadAsync(_path));
        Assert.Contains("ingest", ex.Message);
    }
}
