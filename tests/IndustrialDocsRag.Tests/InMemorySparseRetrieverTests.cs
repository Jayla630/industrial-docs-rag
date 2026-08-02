using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.InMemory;
using IndustrialDocsRag.Retrieval.Sparse;

namespace IndustrialDocsRag.Tests;

public class InMemorySparseRetrieverTests
{
    private static readonly string[] Texts =
    [
        "第5章 故障处理 故障: E201 | 显示: E201.2 | 故障名称: V相过流 | 辅助码: 0x22010201",
        "第5章 故障处理 故障: E208 | 显示: E208.2 | 故障名称: 编码器通讯超时 | 辅助码: 0x22080208",
        "第5章 故障处理 故障: E420 | 显示: E420.0 | 故障名称: 缺相故障 | 辅助码: 0x04200420",
        "第2章 安装 伺服驱动器应安装在无腐蚀性气体、无粉尘的电控柜内，并保证足够的散热空间。",
    ];

    private static (InMemorySparseRetriever Retriever, InMemoryChunkIndex Index) Build()
    {
        var encoder = new Bm25SparseEncoder();
        encoder.Fit(Texts);

        var index = new InMemoryChunkIndex();
        index.UpsertAsync(Texts.Select((t, i) => new IndexedChunk(
            new DocumentChunk
            {
                Id = $"doc#p{i + 1}#5-2#{i:D3}",
                SourceFile = "sv660n-manual.pdf",
                PageNumber = i + 1,
                SectionPath = "第5章 故障处理 > 5.2 通信故障和警告代码一览表",
                Kind = ChunkKind.TableRow,
                Text = t,
            },
            new EmbeddingResult([], encoder.EncodeDocument(t)))).ToList()).GetAwaiter().GetResult();

        return (new InMemorySparseRetriever(index, encoder), index);
    }

    /// <summary>
    /// 这条就是项目的核心论点在稀疏侧的体现：`0x22010201` 是全语料唯一一次出现的稀有词，
    /// IDF 极高，BM25 必须把它顶到第一 —— 而 §9.12 实测稠密向量在这题上只排第 3。
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_精确术语命中的块排第一()
    {
        var (retriever, _) = Build();

        var hits = await retriever.RetrieveAsync(new RetrievalQuery { Text = "E201.2 的辅助码是多少", TopK = 3 });

        Assert.Contains("0x22010201", hits[0].Chunk.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 稀疏侧一个词都不命中就是 0 分。0 分的块留在结果里毫无意义，
    /// 还会在混合检索里挤掉另一路的候选。
    /// </summary>
    [Fact]
    public async Task RetrieveAsync_零分的块不进结果()
    {
        var (retriever, _) = Build();

        var hits = await retriever.RetrieveAsync(new RetrievalQuery { Text = "0x22010201", TopK = 10 });

        Assert.Single(hits);
        Assert.All(hits, h => Assert.True(h.Score > 0));
    }

    [Fact]
    public async Task RetrieveAsync_打上sparse标签供Week3归因()
    {
        var (retriever, _) = Build();

        var hits = await retriever.RetrieveAsync(new RetrievalQuery { Text = "故障", TopK = 5 });

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("sparse", h.RetrieverTag));
    }

    [Fact]
    public async Task RetrieveAsync_TopK限制生效()
    {
        var (retriever, _) = Build();

        var hits = await retriever.RetrieveAsync(new RetrievalQuery { Text = "故障", TopK = 2 });

        Assert.Equal(2, hits.Count);
    }
}
