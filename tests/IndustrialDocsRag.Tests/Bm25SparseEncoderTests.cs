using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Retrieval.Normalization;
using IndustrialDocsRag.Retrieval.Sparse;

namespace IndustrialDocsRag.Tests;

public class Bm25SparseEncoderTests
{
    /// <summary>
    /// 「E201.2」只出现在一条里，「故障」条条都有 —— 稀有词的 IDF 必须明显更高，
    /// 否则稀疏侧接不住精确术语，混合检索就没有意义。
    /// </summary>
    private static readonly string[] Corpus =
    [
        "故障 E201.2 V相过流 辅助码 0x22010201",
        "故障 E208.2 编码器通讯超时 辅助码 0x22080208",
        "故障 E420.0 缺相故障 辅助码 0x04200420",
        "故障处理 一类故障发生时驱动器立即封锁输出",
    ];

    private static Bm25SparseEncoder Fitted()
    {
        var encoder = new Bm25SparseEncoder(PassthroughTermNormalizer.Instance);
        encoder.Fit(Corpus);
        return encoder;
    }

    /// <summary>
    /// ★ 未训练就编码必须硬失败。
    /// <para>
    /// 这是 §5.1 第 4 项那个静默失效的最后一道闸：没有 df/N/avgdl 时，
    /// 若默默返回一个「看起来正常」的向量，检索照样有分数、照样有 Top-K，
    /// 但权重全是错的，而且不报错。
    /// </para>
    /// </summary>
    [Fact]
    public void EncodeDocument_未训练时抛异常而不是返回垃圾权重()
    {
        var encoder = new Bm25SparseEncoder(PassthroughTermNormalizer.Instance);

        var ex = Assert.Throws<InvalidOperationException>(() => encoder.EncodeDocument("故障"));
        Assert.Contains("Fit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeQuery_未训练时抛异常()
    {
        var encoder = new Bm25SparseEncoder(PassthroughTermNormalizer.Instance);

        Assert.Throws<InvalidOperationException>(() => encoder.EncodeQuery("故障"));
    }

    [Fact]
    public void EncodeQuery_稀有词的权重高于常见词()
    {
        var encoder = Fitted();

        var rare = QueryWeight(encoder, "0x22010201");
        var common = QueryWeight(encoder, "故障");

        Assert.True(rare > common, $"稀有词权重 {rare} 应当高于常见词 {common}");
    }

    [Fact]
    public void EncodeQuery_词表外的词被忽略()
    {
        var encoder = Fitted();

        Assert.Equal(0, encoder.EncodeQuery("完全不存在的词汇").NnZ);
    }

    /// <summary>
    /// 文档侧要做长度归一化：同一个词在短文档里的权重应当高于长文档。
    /// </summary>
    [Fact]
    public void EncodeDocument_同一个词在长文档中的权重更低()
    {
        var encoder = Fitted();

        var shortDoc = DocWeight(encoder, "故障 E201.2", "e201.2");
        var longDoc = DocWeight(
            encoder,
            "故障 E201.2 " + string.Join(' ', Enumerable.Repeat("驱动器封锁输出停机", 40)),
            "e201.2");

        Assert.True(shortDoc > longDoc, $"短文档权重 {shortDoc} 应当高于长文档 {longDoc}");
    }

    /// <summary>
    /// 点积就是 BM25 分数：含精确术语的文档必须压过只含通用词的文档。
    /// </summary>
    [Fact]
    public void 文档与查询点积_精确术语命中的文档得分最高()
    {
        var encoder = Fitted();
        var query = encoder.EncodeQuery("E201.2 的辅助码是多少");

        var scores = Corpus.Select(d => Dot(encoder.EncodeDocument(d), query)).ToArray();

        Assert.Equal(0, Array.IndexOf(scores, scores.Max()));
    }

    /// <summary>
    /// ★ 落盘往返后权重必须完全一致。
    /// <para>
    /// 不持久化的后果不是报错，而是重启后查询侧与入库侧的 IDF 对不上，
    /// 检索结果静默劣化 —— §5.1 第 4 项标记的「W2 第一件事」就是它。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 落盘往返后查询权重不变()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bm25-{Guid.NewGuid():N}.json");
        try
        {
            var before = Fitted();
            await SparseModelStore.SaveAsync(path, before, "test-embedder", 1024, before.NormalizerName);

            var loaded = await SparseModelStore.LoadAsync(path);
            var reloaded = new Bm25SparseEncoder(PassthroughTermNormalizer.Instance, loaded.Model);

            Assert.Equal(
                QueryWeight(before, "0x22010201"),
                QueryWeight(reloaded, "0x22010201"));
            Assert.Equal("test-embedder", loaded.EmbeddingServiceName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float QueryWeight(ISparseEncoder encoder, string term)
    {
        var v = encoder.EncodeQuery(term);
        return v.NnZ == 0 ? 0f : v.Values.Max();
    }

    private static float DocWeight(Bm25SparseEncoder encoder, string text, string term)
    {
        var doc = encoder.EncodeDocument(text);
        var id = encoder.TermId(term) ?? throw new InvalidOperationException($"词表里没有 {term}");
        var at = Array.IndexOf(doc.Indices, id);
        return at < 0 ? 0f : doc.Values[at];
    }

    private static float Dot(SparseVector a, SparseVector b)
    {
        var sum = 0f;
        for (var i = 0; i < a.Indices.Length; i++)
        {
            var at = Array.IndexOf(b.Indices, a.Indices[i]);
            if (at >= 0)
            {
                sum += a.Values[i] * b.Values[at];
            }
        }

        return sum;
    }

    /// <summary>
    /// ★ 对称性由构造保证：normalizer 注入编码器之后，入库侧和查询侧走的是同一条路径，
    /// 不可能出现「只做了一侧」的情况 —— 而只做一侧比两侧都不做更糟。
    /// 这里验证：用 0x6041 训练、用 6041 查询，能对上。
    /// </summary>
    [Fact]
    public void 规范化后入库侧与查询侧的词面能对上()
    {
        var encoder = new Bm25SparseEncoder(new HexTermNormalizer());
        encoder.Fit(["对象字典 0x6041 状态字"]);

        var query = encoder.EncodeQuery("6041 是什么");

        Assert.NotEmpty(query.Indices);
        Assert.Equal(encoder.TermId("6041"), query.Indices[0]);
    }

    /// <summary>
    /// 反证：不带规范化时，0x6041 与 6041 是两个不同的词，查询打不中 ——
    /// 这正是 README 里那个公开挂着的失败用例。
    /// </summary>
    [Fact]
    public void 不规范化时0x6041与6041对不上()
    {
        var encoder = new Bm25SparseEncoder(PassthroughTermNormalizer.Instance);
        encoder.Fit(["对象字典 0x6041 状态字"]);

        Assert.Null(encoder.TermId("6041"));
    }
}
