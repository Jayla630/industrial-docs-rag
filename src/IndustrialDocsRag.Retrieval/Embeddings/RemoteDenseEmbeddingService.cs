using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.Retrieval.Embeddings;

/// <summary>
/// 调用 OpenAI 兼容的 <c>/v1/embeddings</c> 端点取稠密向量。当前对接硅基流动的 BGE-M3。
/// <para>
/// 实测（2026-08-01）：<c>input</c> 支持数组批量，返回 1024 维，向量在服务端已做 L2 归一化，
/// 响应里<b>没有</b>稀疏向量字段 —— OpenAI 的响应格式本就没有装稀疏权重的位置，
/// 所以 <see cref="ProducesSparse"/> 恒为 false，混合检索的稀疏侧必须自建。
/// </para>
/// <para>
/// 刻意不做重试、退避、限流（见 docs/plan-week1.md §5.1 第 11 项）。Week1 只有十几个块，
/// 撞上 429 手动重跑即可；过早加韧性代码会掩盖端点本身的问题。
/// </para>
/// </summary>
public sealed class RemoteDenseEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _batchSize;

    public RemoteDenseEmbeddingService(
        string endpoint,
        string model,
        string apiKey,
        int denseDimension,
        int batchSize = 32)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "缺少 Embedding 的 API Key。请执行：\n" +
                "  dotnet user-secrets set \"Rag:Embedding:ApiKey\" \"<你的key>\" --project src/IndustrialDocsRag.App");
        }

        _model = model;
        _batchSize = Math.Max(1, batchSize);
        DenseDimension = denseDimension;

        _http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string Name => $"remote:{_model}";

    public int DenseDimension { get; }

    public bool ProducesSparse => false;

    public async Task<EmbeddingResult> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<EmbeddingResult>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var all = new List<EmbeddingResult>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(_batchSize).ToList();
            all.AddRange(await EmbedBatchAsync(batch, ct));
        }

        return all;
    }

    private async Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        var request = new EmbeddingRequest(_model, texts);

        using var response = await _http.PostAsJsonAsync("embeddings", request, JsonDefaults.Options, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"嵌入接口返回 {(int)response.StatusCode} {response.ReasonPhrase}。" +
                $"响应：{Truncate(body, 400)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonDefaults.Options, ct)
                      ?? throw new HttpRequestException("嵌入接口返回了空响应体。");

        if (payload.Data is null || payload.Data.Count != texts.Count)
        {
            throw new HttpRequestException(
                $"嵌入接口返回 {payload.Data?.Count ?? 0} 条，与请求的 {texts.Count} 条不匹配。");
        }

        // 按 index 排序：契约要求返回顺序与输入一一对应，而这个顺序不该靠端点的实现细节来保证
        var ordered = payload.Data.OrderBy(d => d.Index).ToList();

        foreach (var item in ordered)
        {
            if (item.Embedding.Length != DenseDimension)
            {
                throw new HttpRequestException(
                    $"嵌入接口返回 {item.Embedding.Length} 维，配置里写的是 {DenseDimension} 维。" +
                    "请修正 appsettings.json 的 Rag:Embedding:DenseDimension。");
            }
        }

        return ordered.Select(d => new EmbeddingResult(d.Embedding, null)).ToList();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    public void Dispose() => _http.Dispose();

    // ---- 线上协议的 DTO。字段名由 JsonDefaults 的 camelCase 策略映射。 ----

    private sealed record EmbeddingRequest(string Model, IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(float[] Embedding, int Index);
}
