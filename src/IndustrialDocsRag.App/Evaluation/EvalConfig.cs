namespace IndustrialDocsRag.App.Evaluation;

/// <summary>
/// 评测矩阵里的一个配置。一个配置 = 一套完整的检索侧设定，跑出评测表里的一行。
/// </summary>
/// <param name="Name">报告里的行名。</param>
/// <param name="Retriever">dense / sparse / hybrid / shuffle。</param>
/// <param name="Fusion">rrf / weighted。仅 hybrid 有意义。</param>
/// <param name="Alpha">加权融合的稠密侧权重。仅 weighted 有意义。</param>
/// <param name="Normalizer">passthrough / hex。只作用于稀疏侧。</param>
/// <param name="IndexPath">内存索引路径。规范化组是另一套。</param>
/// <param name="SparseModelPath">BM25 模型路径。必须与 IndexPath 同源，守卫会校验。</param>
public sealed record EvalConfig(
    string Name,
    string Retriever,
    string Fusion = "rrf",
    double Alpha = 0.5,
    string Normalizer = "passthrough",
    string IndexPath = EvalConfig.BaselineIndex,
    string SparseModelPath = EvalConfig.BaselineSparseModel)
{
    public const string BaselineIndex = "data/index.week3.json";
    public const string BaselineSparseModel = "data/bm25.week3.json";
    public const string NormIndex = "data/index.week3.norm.json";
    public const string NormSparseModel = "data/bm25.week3.norm.json";

    /// <summary>
    /// 全矩阵。顺序即报告里的行序。
    /// <para>
    /// <c>norm-dense</c> 是<b>自检行</b>：规范化只注入稀疏侧，稠密侧不该有任何变化，
    /// 所以它必须与 <c>dense</c> 逐题完全一致。不一致就说明规范化泄漏了，规范化组的全部数字作废。
    /// </para>
    /// <para>
    /// <c>shuffle</c> 是另一个自检行：1667 块下 Recall@5 应 ≈ 0。
    /// 明显大于 0 就是判定逻辑有 bug，不是运气。
    /// </para>
    /// </summary>
    public static IReadOnlyList<EvalConfig> All { get; } =
    [
        new("dense", "dense"),
        new("sparse", "sparse"),
        new("hybrid-rrf", "hybrid"),
        new("hybrid-w03", "hybrid", "weighted", 0.3),
        new("hybrid-w05", "hybrid", "weighted", 0.5),
        new("hybrid-w07", "hybrid", "weighted", 0.7),
        new("shuffle", "shuffle"),

        new("norm-dense", "dense", Normalizer: "hex",
            IndexPath: NormIndex, SparseModelPath: NormSparseModel),
        new("norm-sparse", "sparse", Normalizer: "hex",
            IndexPath: NormIndex, SparseModelPath: NormSparseModel),
        new("norm-hybrid-rrf", "hybrid", Normalizer: "hex",
            IndexPath: NormIndex, SparseModelPath: NormSparseModel),
    ];

    public static IReadOnlyList<EvalConfig> Select(string? names)
        => string.IsNullOrWhiteSpace(names) || names == "all"
            ? All
            : names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(name => All.FirstOrDefault(c => c.Name == name)
                                   ?? throw new InvalidOperationException(
                                       $"未知的配置：{name}。可选：{string.Join(" / ", All.Select(c => c.Name))}"))
                   .ToList();
}
