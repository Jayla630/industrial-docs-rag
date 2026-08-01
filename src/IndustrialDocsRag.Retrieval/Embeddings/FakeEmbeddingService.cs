using IndustrialDocsRag.Core.Embeddings;

namespace IndustrialDocsRag.Retrieval.Embeddings;

/// <summary>
/// 不调用任何外部服务的嵌入实现，用哈希技巧（hashing trick）把词袋映射到固定维度。
/// <para>
/// 它存在的意义是让整条流水线在没有 API key、没有网络的情况下就能跑通并调试，
/// 把"端点格式对不对"和"管道通不通"这两个问题分开 —— 三件事同时接通再排查最费时间。
/// </para>
/// <para>
/// 因为是词袋，它给出的相似度只反映字面重合，完全没有语义。用它验证排序逻辑、
/// 落盘加载、Top-K 截断都没问题；用它评价召回质量则毫无意义。
/// </para>
/// </summary>
public sealed class FakeEmbeddingService(int denseDimension = 1024) : IEmbeddingService
{
    public string Name => "fake-hashing-bow";

    public int DenseDimension { get; } = denseDimension > 0
        ? denseDimension
        : throw new ArgumentOutOfRangeException(nameof(denseDimension));

    public bool ProducesSparse => false;

    public Task<EmbeddingResult> EmbedQueryAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new EmbeddingResult(Encode(text), null));

    public Task<IReadOnlyList<EmbeddingResult>> EmbedDocumentsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var results = new List<EmbeddingResult>(texts.Count);
        foreach (var t in texts)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(new EmbeddingResult(Encode(t), null));
        }

        return Task.FromResult<IReadOnlyList<EmbeddingResult>>(results);
    }

    private float[] Encode(string text)
    {
        var vector = new float[DenseDimension];

        foreach (var token in Tokenize(text))
        {
            var hash = StableHash.Of(token);
            var index = (int)(hash % (uint)DenseDimension);

            // 用哈希的最高位决定符号。若所有 token 都往正方向累加，
            // 任意两个长文本都会因为"分量普遍为正"而余弦偏高，区分度垮掉。
            var sign = (hash & 0x8000_0000u) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        VectorMath.NormalizeInPlace(vector);

        // 全零向量（空文本）会让余弦恒为 0，退化成随机排序。给一个固定方向避免这种情况。
        if (vector.All(v => v == 0f))
        {
            vector[0] = 1f;
        }

        return vector;
    }

    /// <summary>
    /// ASCII 字母数字连续段作为一个词（<c>0x6041</c>、<c>P0-08</c> 里的段），
    /// 中日韩字符则同时取单字和双字组合 —— 中文没有空格分词，双字组合能带来基本的词组区分度。
    /// </summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var buffer = new System.Text.StringBuilder();
        char? previousCjk = null;

        foreach (var raw in text)
        {
            var ch = char.ToLowerInvariant(raw);

            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer.Append(ch);
                previousCjk = null;
                continue;
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (IsCjk(ch))
            {
                yield return ch.ToString();

                if (previousCjk is { } prev)
                {
                    yield return $"{prev}{ch}";
                }

                previousCjk = ch;
            }
            else
            {
                previousCjk = null;
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static bool IsCjk(char ch)
        => ch is >= '一' and <= '鿿'    // 中日韩统一表意文字
            or >= '㐀' and <= '䶿';     // 扩展 A
}
