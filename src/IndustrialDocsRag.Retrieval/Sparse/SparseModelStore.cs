using System.Text.Json;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.Retrieval.Sparse;

/// <summary>加载结果：训练好的编码器，加上产出这批索引的嵌入服务标识。</summary>
public sealed record LoadedSparseModel(
    Bm25SparseEncoder Encoder,
    string TokenizerName,
    string EmbeddingServiceName,
    int DenseDimension)
{
    /// <summary>
    /// 确认索引里的稠密向量与当前查询用的嵌入服务同源。
    /// <para>
    /// Week1 这个守卫长在 <c>InMemoryChunkIndex</c> 上；换到 Qdrant 之后那个载体没了，
    /// 由本文件接管。防的是同一件事：用 Fake 灌了库、换真实端点后忘了重新 ingest，
    /// 此时余弦照样有分数、Top-K 照样有结果、答案照样像模像样，但检索是纯噪声。
    /// 2300 块规模下这种失效比 15 块时更难察觉。
    /// </para>
    /// </summary>
    public void EnsureCompatibleWith(IEmbeddingService embedder)
    {
        if (!string.Equals(EmbeddingServiceName, embedder.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"索引由 `{EmbeddingServiceName}` 产出，当前查询用的是 `{embedder.Name}`。" +
                "两者的向量空间不可比，结果会是纯噪声。请重新执行 `idr ingest`。");
        }

        if (DenseDimension != embedder.DenseDimension)
        {
            throw new InvalidOperationException(
                $"索引向量维度 {DenseDimension}，当前嵌入服务维度 {embedder.DenseDimension}。请重新执行 `idr ingest`。");
        }
    }
}

/// <summary>
/// BM25 模型的落盘与加载。这个文件同时是<b>索引清单</b> —— 见 <see cref="LoadedSparseModel.EnsureCompatibleWith"/>。
/// </summary>
public static class SparseModelStore
{
    private const int CurrentFormatVersion = 1;

    public static async Task SaveAsync(
        string path,
        Bm25SparseEncoder encoder,
        string embeddingServiceName,
        int denseDimension,
        CancellationToken ct = default)
    {
        var model = encoder.Model
                    ?? throw new InvalidOperationException("编码器尚未训练，没有可落盘的模型。");

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var payload = new SparseModelFile(
            CurrentFormatVersion,
            IndustrialTokenizer.Name,
            embeddingServiceName,
            denseDimension,
            DateTimeOffset.Now,
            model);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, JsonDefaults.Options, ct);
    }

    public static async Task<LoadedSparseModel> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到 BM25 模型：{path}。先执行 `idr ingest` 建索引。", path);
        }

        await using var stream = File.OpenRead(path);
        var payload = await JsonSerializer.DeserializeAsync<SparseModelFile>(stream, JsonDefaults.Options, ct)
                      ?? throw new InvalidDataException($"BM25 模型文件内容为空：{path}");

        if (payload.Version != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"BM25 模型格式版本为 {payload.Version}，当前程序需要 {CurrentFormatVersion}。请重新执行 `idr ingest`。");
        }

        // 分词规则一改，词表 id 的语义就变了，落盘的权重全部失效
        if (!string.Equals(payload.TokenizerName, IndustrialTokenizer.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"模型由分词器 `{payload.TokenizerName}` 产出，当前是 `{IndustrialTokenizer.Name}`。请重新执行 `idr ingest`。");
        }

        return new LoadedSparseModel(
            new Bm25SparseEncoder(payload.Model),
            payload.TokenizerName,
            payload.EmbeddingServiceName,
            payload.DenseDimension);
    }

    private sealed record SparseModelFile(
        int Version,
        string TokenizerName,
        string EmbeddingServiceName,
        int DenseDimension,
        DateTimeOffset CreatedAt,
        Bm25Model Model);
}
