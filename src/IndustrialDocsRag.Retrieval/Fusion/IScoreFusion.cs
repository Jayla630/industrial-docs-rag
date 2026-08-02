using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Retrieval.Fusion;

/// <summary>
/// 把两路召回合成一路。纯函数，与存储无关 —— 内存路径和 Qdrant 路径共用同一个融合器。
/// <para>
/// 融合放在客户端而不是用 Qdrant 内置的 Query API，是因为服务端只支持 RRF 与 DBSF，
/// 做不了 <see cref="WeightedFusion"/> 那种 α 加权，而「RRF vs 加权」正是 Week3 要做的实验（§7.4）。
/// </para>
/// </summary>
public interface IScoreFusion
{
    /// <summary>写进评测报告的标识，同时也是融合结果的 <c>RetrieverTag</c>。</summary>
    string Name { get; }

    IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<RetrievedChunk> dense,
        IReadOnlyList<RetrievedChunk> sparse,
        int topK);
}
