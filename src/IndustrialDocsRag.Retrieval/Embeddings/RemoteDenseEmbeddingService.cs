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
/// Week1 刻意不做重试（§5.1 第 11 项），前提是只有十几个块。Week2 语料涨到 1667 块、
/// 分批 32 条即 53 次请求，免费额度的 QPS 限制会真的撞上，所以补了最小退避重试：
/// 只重试 429 与 5xx，指数退避，最多三次。不引 Polly —— 二十行的事。
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

    private const int MaxAttempts = 4;

    private async Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        var request = new EmbeddingRequest(_model, texts);

        using var response = await SendWithBackoffAsync(request, ct);

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

    /// <summary>
    /// 只对 429 与 5xx 退避重试 —— 这两类是「过一会儿再来」，其余（401 鉴权、400 参数错）
    /// 重试多少次都一样，重试只会拖长排查时间。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithBackoffAsync(
        EmbeddingRequest request,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await _http.PostAsJsonAsync("embeddings", request, JsonDefaults.Options, ct);

            var retryable = response.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500;

            if (!retryable || attempt >= MaxAttempts)
            {
                return response;
            }

            response.Dispose();

            var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
            await Task.Delay(delay, ct);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    public void Dispose() => _http.Dispose();

    // ---- 线上协议的 DTO。字段名由 JsonDefaults 的 camelCase 策略映射。 ----

    private sealed record EmbeddingRequest(string Model, IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(float[] Embedding, int Index);
}
