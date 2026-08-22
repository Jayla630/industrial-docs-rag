using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;

namespace IndustrialDocsRag.Retrieval.Sparse;

/// <summary>
/// 自建 BM25 稀疏编码器。
/// <para>
/// 权重按检索时的惯例拆到两侧：<b>文档侧</b>存词频与长度归一化部分，<b>查询侧</b>存 IDF。
/// 两者点积恰好等于 BM25 分数，Qdrant 的稀疏向量检索算的就是这个点积。
/// </para>
/// <para>
/// 之所以自建而不是用 BGE-M3 的 lexical weights：§9.12 实测确认硅基流动的
/// <c>/v1/embeddings</c> 响应里<b>没有</b>稀疏字段，稀疏侧只能自己做。
/// </para>
/// </summary>
/// <param name="normalizer">
/// 术语规范化器，在分词之前调用。
/// <para>
/// 注入到编码器里而不是放在流水线的某个全局位置，是为了让<b>入库侧与查询侧的对称性由构造保证</b> ——
/// <c>ITermNormalizer</c> 的注释写着「只做一侧比两侧都不做更糟」，靠两处调用点各自记得调迟早会破。
/// 同时这样规范化只影响稀疏侧，稠密侧一个字都不改，规范化 A/B 才是干净的单变量实验。
/// </para>
/// </param>
public sealed class Bm25SparseEncoder(ITermNormalizer normalizer, Bm25Model? model = null)
    : ISparseEncoder, ISparseEncoderTrainer
{
    private Bm25Model? _model = model;

    /// <summary>规范化器名进 Name：它和分词器一样会改变词表 id 的语义，是索引身份的一部分。</summary>
    public string Name => $"bm25:{IndustrialTokenizer.Name}:{normalizer.Name}";

    /// <summary>规范化器标识，落盘时写进模型文件供加载时比对。</summary>
    public string NormalizerName => normalizer.Name;

    /// <summary>训练好的模型。未训练时为 null。</summary>
    public Bm25Model? Model => _model;

    /// <summary>
    /// 装入已落盘的模型。查询侧靠它拿到与入库时一致的 IDF ——
    /// 拿单条查询重新 Fit 得到的是垃圾权重，而且不报错。
    /// </summary>
    public void Load(Bm25Model trained) => _model = trained;

    /// <summary>分词的唯一入口 —— 规范化必须发生在这里，其它地方不能绕过它。</summary>
    private IEnumerable<string> Tokens(string text)
        => IndustrialTokenizer.Tokenize(normalizer.Normalize(text));

    public void Fit(IReadOnlyList<string> documents)
    {
        var vocabulary = new Dictionary<string, uint>(StringComparer.Ordinal);
        var documentFrequency = new Dictionary<uint, int>();
        var totalLength = 0L;

        foreach (var document in documents)
        {
            var seen = new HashSet<uint>();

            foreach (var token in Tokens(document))
            {
                totalLength++;

                if (!vocabulary.TryGetValue(token, out var id))
                {
                    id = (uint)vocabulary.Count;
                    vocabulary[token] = id;
                }

                if (seen.Add(id))
                {
                    documentFrequency[id] = documentFrequency.GetValueOrDefault(id) + 1;
                }
            }
        }

        _model = new Bm25Model(
            vocabulary,
            documentFrequency,
            documents.Count,
            documents.Count == 0 ? 0 : (double)totalLength / documents.Count);
    }

    /// <summary>
    /// 文档侧：<c>tf·(k1+1) / (tf + k1·(1 - b + b·dl/avgdl))</c>，不含 IDF。
    /// </summary>
    public SparseVector EncodeDocument(string text)
    {
        var m = Require();
        var counts = CountKnownTerms(text, m);

        if (counts.Count == 0)
        {
            return SparseVector.Empty;
        }

        var length = counts.Values.Sum();
        var norm = m.AverageLength <= 0 ? 1.0 : 1 - m.B + (m.B * length / m.AverageLength);

        return Build(counts, (_, tf) => (float)(tf * (m.K1 + 1) / (tf + (m.K1 * norm))));
    }

    /// <summary>
    /// 查询侧：只有 IDF。用 Lucene 那支恒为正的变体，避免高频词产生负权重把分数拉反。
    /// </summary>
    public SparseVector EncodeQuery(string text)
    {
        var m = Require();
        var counts = CountKnownTerms(text, m);

        return counts.Count == 0
            ? SparseVector.Empty
            : Build(counts, (id, _) =>
            {
                var df = m.DocumentFrequency.GetValueOrDefault(id);
                return (float)Math.Log(1 + ((m.DocumentCount - df + 0.5) / (df + 0.5)));
            });
    }

    /// <summary>词表 id，供测试与诊断使用。</summary>
    public uint? TermId(string term)
        => Require().Vocabulary.TryGetValue(term, out var id) ? id : null;

    private Dictionary<uint, int> CountKnownTerms(string text, Bm25Model m)
    {
        var counts = new Dictionary<uint, int>();

        foreach (var token in Tokens(text))
        {
            // 词表外的词对点积贡献恒为 0，直接丢
            if (m.Vocabulary.TryGetValue(token, out var id))
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    private static SparseVector Build(Dictionary<uint, int> counts, Func<uint, int, float> weight)
    {
        var indices = counts.Keys.Order().ToArray();
        var values = new float[indices.Length];

        for (var i = 0; i < indices.Length; i++)
        {
            values[i] = weight(indices[i], counts[indices[i]]);
        }

        return new SparseVector(indices, values);
    }

    /// <summary>
    /// 未训练就编码必须硬失败。默默返回一个「看起来正常」的向量，
    /// 检索会照样有分数、照样有 Top-K，但权重全是错的 —— 正是本项目最该防的静默失效。
    /// </summary>
    private Bm25Model Require()
        => _model ?? throw new InvalidOperationException(
            "BM25 尚未训练。先调用 Fit(documents)，或从 data/bm25.*.json 加载已训练的模型。");
}
