using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.Embeddings;
using IndustrialDocsRag.Retrieval.InMemory;

namespace IndustrialDocsRag.Tests;

public class InMemoryChunkIndexTests
{
    /// <summary>
    /// 这是本项目最容易踩的静默失效：用 FakeEmbeddingService 灌了库，换成真实端点后忘了重新 ingest。
    /// 此时查询向量是真的、库里向量是假的，余弦照样算得出分数、Top-K 照样有结果、答案照样像模像样，
    /// 但检索完全是噪声。必须在这里硬拦下来。
    /// </summary>
    [Fact]
    public async Task EnsureCompatibleWith_嵌入服务不同_抛异常()
    {
        var index = await BuildIndexAsync(new FakeEmbeddingService(8));

        var ex = Assert.Throws<InvalidOperationException>(
            () => index.EnsureCompatibleWith(new StubEmbeddingService("real-remote", 8)));

        Assert.Contains("重新执行", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureCompatibleWith_维度不同_抛异常()
    {
        var index = await BuildIndexAsync(new FakeEmbeddingService(8));

        Assert.Throws<InvalidOperationException>(
            () => index.EnsureCompatibleWith(new StubEmbeddingService("fake-hashing-bow", 1024)));
    }

    [Fact]
    public async Task EnsureCompatibleWith_同一服务_不抛异常()
    {
        var embedder = new FakeEmbeddingService(8);
        var index = await BuildIndexAsync(embedder);

        index.EnsureCompatibleWith(embedder);
    }

    [Fact]
    public void EnsureCompatibleWith_空索引_抛异常()
    {
        var index = new InMemoryChunkIndex();

        Assert.Throws<InvalidOperationException>(
            () => index.EnsureCompatibleWith(new FakeEmbeddingService(8)));
    }

    [Fact]
    public async Task 落盘再加载_内容与元信息都保持一致()
    {
        var embedder = new FakeEmbeddingService(8);
        var original = await BuildIndexAsync(embedder);

        var path = Path.Combine(Path.GetTempPath(), $"idr-index-{Guid.NewGuid():N}.json");
        try
        {
            await original.SaveAsync(path, embedder.Name);

            var loaded = new InMemoryChunkIndex();
            await loaded.LoadAsync(path);

            Assert.Equal(await original.CountAsync(), await loaded.CountAsync());
            Assert.Equal(embedder.Name, loaded.EmbeddingServiceName);
            Assert.Equal(8, loaded.DenseDimension);

            var chunk = loaded.Items.Single().Chunk;
            Assert.Equal("demo#p1#1#000", chunk.Id);
            Assert.Equal(ChunkKind.TableRow, chunk.Kind);
            Assert.Equal(33, chunk.PageNumber);
            Assert.Equal("27", chunk.Metadata["pageLabel"]);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<InMemoryChunkIndex> BuildIndexAsync(IEmbeddingService embedder)
    {
        var chunk = new DocumentChunk
        {
            Id = "demo#p1#1#000",
            SourceFile = "demo.pdf",
            PageNumber = 33,
            SectionPath = "3 现场总线通信 > 3.3 状态字",
            CharOffset = 120,
            Kind = ChunkKind.TableRow,
            Text = "索引 0x6041 | 名称 Statusword | 数据类型 UINT16 | 访问权限 RO",
            Metadata = new Dictionary<string, string> { ["pageLabel"] = "27" },
        };

        var embedding = (await embedder.EmbedDocumentsAsync([chunk.Text]))[0];

        var index = new InMemoryChunkIndex();
        await index.UpsertAsync([new IndexedChunk(chunk, embedding)]);

        // SaveAsync 之外没有别的途径设置 EmbeddingServiceName，这里用一次内存往返模拟入库后的状态
        var tmp = Path.Combine(Path.GetTempPath(), $"idr-seed-{Guid.NewGuid():N}.json");
        await index.SaveAsync(tmp, embedder.Name);
        File.Delete(tmp);

        return index;
    }

    private sealed class StubEmbeddingService(string name, int dimension) : IEmbeddingService
    {
        public string Name => name;

        public int DenseDimension => dimension;

        public bool ProducesSparse => false;

        public Task<EmbeddingResult> EmbedQueryAsync(string text, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EmbeddingResult>> EmbedDocumentsAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
