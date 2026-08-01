using IndustrialDocsRag.Core.Documents;

namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>一条召回结果。</summary>
/// <param name="Chunk">召回的块。</param>
/// <param name="Score">相关性分数。语义由具体实现决定（余弦、BM25、RRF 融合分…），跨实现不可直接比较。</param>
/// <param name="RetrieverTag">
/// 这一条是被哪一路召回的：<c>dense</c> / <c>sparse</c> / <c>rrf</c> / <c>shuffle</c>。
/// 看着不起眼，但 Week3 分析「哪些题只有稀疏侧能召回、稠密侧完全捞不到」时，它是唯一的数据来源，
/// 而那正是本项目要证明的核心论点（错误码这类精确术语，纯向量检索召回极差）。
/// </param>
public sealed record RetrievedChunk(DocumentChunk Chunk, double Score, string RetrieverTag);
