namespace IndustrialDocsRag.Retrieval.Sparse;

/// <summary>
/// BM25 的全语料统计量。可序列化 —— <b>这是它存在的全部理由</b>。
/// <para>
/// 不持久化的后果不是报错，而是重启后查询侧的 IDF 与入库时的不一致，
/// 检索结果<b>静默劣化</b>：分数照算、Top-K 照出、答案照样像模像样，只是变差。
/// 这类失效在 `plan-week1.md` §5.1 第 4 项被标为「W2 第一件事就是它」。
/// </para>
/// </summary>
/// <param name="Vocabulary">词 → 词表 id。id 即 <c>SparseVector.Indices</c> 的取值。</param>
/// <param name="DocumentFrequency">词表 id → 出现过该词的文档数。</param>
/// <param name="DocumentCount">语料文档总数 N。</param>
/// <param name="AverageLength">平均文档长度（token 数）。</param>
public sealed record Bm25Model(
    IReadOnlyDictionary<string, uint> Vocabulary,
    IReadOnlyDictionary<uint, int> DocumentFrequency,
    int DocumentCount,
    double AverageLength,
    double K1 = 1.2,
    double B = 0.75);
