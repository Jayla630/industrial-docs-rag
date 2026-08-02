using IndustrialDocsRag.Core.Embeddings;
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
        var encoder = new Bm25SparseEncoder();
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
        var encoder = new Bm25SparseEncoder();

        var ex = Assert.Throws<InvalidOperationException>(() => encoder.EncodeDocument("故障"));
        Assert.Contains("Fit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeQuery_未训练时抛异常()
    {
        var encoder = new Bm25SparseEncoder();

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
            await SparseModelStore.SaveAsync(path, before, "test-embedder", 1024);

            var loaded = await SparseModelStore.LoadAsync(path);

            Assert.Equal(
                QueryWeight(before, "0x22010201"),
                QueryWeight(loaded.Encoder, "0x22010201"));
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
}
