using System.Text.Json;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.Retrieval.InMemory;

/// <summary>
/// Week1 的索引实现：全部放内存，另可落盘成一个 JSON。
/// <para>
/// 落盘不是为了持久化需求，是为了开发效率 —— 否则每调一次 <c>ask</c> 都要把整批文本重新
/// 送去嵌入，烧掉时间和 API 额度。十来行代码换回整个周末的迭代速度。
/// </para>
/// <para>Week2 换成 QdrantChunkIndex，<see cref="IChunkIndex"/> 签名不变。</para>
/// </summary>
public sealed class InMemoryChunkIndex : IChunkIndex
{
    private const int CurrentFormatVersion = 1;

    private readonly Dictionary<string, IndexedChunk> _items = new(StringComparer.Ordinal);

    /// <summary>产出本索引里向量的嵌入服务标识。从落盘文件加载后才有值。</summary>
    public string? EmbeddingServiceName { get; private set; }

    /// <summary>本索引里向量的维度。空索引时为 0。</summary>
    public int DenseDimension { get; private set; }

    /// <summary>供检索器遍历。内存实现是全量扫描，这在十几到几千个块的规模下完全够用。</summary>
    public IReadOnlyCollection<IndexedChunk> Items => _items.Values;

    public Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken ct = default)
    {
        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();
            _items[c.Chunk.Id] = c;

            if (DenseDimension == 0)
            {
                DenseDimension = c.Embedding.Dense.Length;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(CancellationToken ct = default) => Task.FromResult(_items.Count > 0);

    public Task ResetAsync(CancellationToken ct = default)
    {
        _items.Clear();
        DenseDimension = 0;
        EmbeddingServiceName = null;
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_items.Count);

    public async Task SaveAsync(string path, string embeddingServiceName, CancellationToken ct = default)
    {
        EmbeddingServiceName = embeddingServiceName;

        var payload = new IndexFile(
            CurrentFormatVersion,
            embeddingServiceName,
            DenseDimension,
            DateTimeOffset.Now,
            _items.Values.ToList());

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, JsonDefaults.Options, ct);
    }

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到索引文件：{path}。先执行 `idr ingest` 建索引。", path);
        }

        await using var stream = File.OpenRead(path);
        var payload = await JsonSerializer.DeserializeAsync<IndexFile>(stream, JsonDefaults.Options, ct)
                      ?? throw new InvalidDataException($"索引文件内容为空：{path}");

        if (payload.Version != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"索引文件格式版本为 {payload.Version}，当前程序需要 {CurrentFormatVersion}。请重新执行 `idr ingest`。");
        }

        _items.Clear();
        foreach (var c in payload.Chunks)
        {
            _items[c.Chunk.Id] = c;
        }

        EmbeddingServiceName = payload.EmbeddingServiceName;
        DenseDimension = payload.DenseDimension;
    }

    /// <summary>
    /// 确认索引里的向量和当前查询用的嵌入服务是同一个产出的。
    /// <para>
    /// 这个检查不是形式主义。开发过程中一定会发生"用 FakeEmbeddingService 灌了库，
    /// 换成真实端点后忘了重新 ingest"：此时查询向量是真的、库里向量是假的，
    /// 余弦照样能算出分数、Top-K 照样有结果、答案照样像模像样 —— 但检索完全是噪声。
    /// 这类静默失效正是本项目最该防的东西。
    /// </para>
    /// </summary>
    public void EnsureCompatibleWith(IEmbeddingService embedder)
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("索引为空。先执行 `idr ingest` 建索引。");
        }

        if (EmbeddingServiceName is { } name && !string.Equals(name, embedder.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"索引由 `{name}` 产出，当前查询用的是 `{embedder.Name}`。" +
                "两者的向量空间不可比，结果会是纯噪声。请重新执行 `idr ingest`。");
        }

        if (DenseDimension != embedder.DenseDimension)
        {
            throw new InvalidOperationException(
                $"索引向量维度 {DenseDimension}，当前嵌入服务维度 {embedder.DenseDimension}。请重新执行 `idr ingest`。");
        }
    }

    private sealed record IndexFile(
        int Version,
        string EmbeddingServiceName,
        int DenseDimension,
        DateTimeOffset CreatedAt,
        IReadOnlyList<IndexedChunk> Chunks);
}
