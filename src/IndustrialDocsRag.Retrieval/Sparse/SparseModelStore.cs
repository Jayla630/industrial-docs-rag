using System.Text.Json;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.Retrieval.Sparse;

/// <summary>加载结果：训练好的统计量，加上产出这批索引的嵌入服务与规范化器标识。</summary>
public sealed record LoadedSparseModel(
    Bm25Model Model,
    string TokenizerName,
    string NormalizerName,
    string EmbeddingServiceName,
    int DenseDimension)
{
    /// <summary>
    /// 确认索引与当前查询侧的配置同源。守的是两类都<b>不报错、只是变差</b>的失效：
    /// <list type="number">
    /// <item>用 Fake 灌了库、换真实端点后忘了重新 ingest —— 余弦照样有分数，但检索是纯噪声</item>
    /// <item>用规范化训练的词表配不带规范化的查询侧 —— 词面对不上，召回静默劣化</item>
    /// </list>
    /// Week1 这个守卫长在 <c>InMemoryChunkIndex</c> 上；换到 Qdrant 之后那个载体没了，由本文件接管。
    /// </summary>
    public void EnsureCompatibleWith(IEmbeddingService embedder, ITermNormalizer normalizer)
        => EnsureCompatibleWith(embedder, normalizer.Name);

    /// <summary>
    /// 只收规范化器名字的重载。调用方（<c>SparseModelLoader</c>）手上有的正是
    /// <c>Bm25SparseEncoder.NormalizerName</c>，不必为了校验再造一个规范化器实例。
    /// </summary>
    public void EnsureCompatibleWith(IEmbeddingService embedder, string normalizerName)
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

        if (!string.Equals(NormalizerName, normalizerName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"词表由规范化器 `{NormalizerName}` 产出，当前查询侧是 `{normalizerName}`。" +
                "两侧词面对不上，召回会静默劣化。请用匹配的 --normalizer，或重新执行 `idr ingest`。");
        }
    }
}

/// <summary>
/// BM25 模型的落盘与加载。这个文件同时是<b>索引清单</b> —— 见 <see cref="LoadedSparseModel.EnsureCompatibleWith(IEmbeddingService, string)"/>。
/// </summary>
public static class SparseModelStore
{
    // v1 没有 normalizerName 字段。版本号在 Week3 从 1 升到 2 之后，
    // v1 文件一律在下面的版本校验处硬失败，绝不当成 passthrough 默默放行 ——
    // 那样等于没加规范化器守卫。
    private const int CurrentFormatVersion = 2;

    public static async Task SaveAsync(
        string path,
        Bm25SparseEncoder encoder,
        string embeddingServiceName,
        int denseDimension,
        string normalizerName,
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
            normalizerName,
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
            payload.Model,
            payload.TokenizerName,
            payload.NormalizerName,
            payload.EmbeddingServiceName,
            payload.DenseDimension);
    }

    private sealed record SparseModelFile(
        int Version,
        string TokenizerName,
        string NormalizerName,
        string EmbeddingServiceName,
        int DenseDimension,
        DateTimeOffset CreatedAt,
        Bm25Model Model);
}
