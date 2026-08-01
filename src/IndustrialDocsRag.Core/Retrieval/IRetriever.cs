namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>
/// 检索层的唯一入口。
/// <para>
/// 整个项目的核心工程主张就是这个接口可替换：Week1 是内存余弦 Top-K，Week2 换成 Qdrant 稠密 + 稀疏
/// 混合检索，Week3 在外面套一层重排装饰器，而调用方（问答服务、评测脚本）一行都不用改。
/// </para>
/// <para>
/// 重排装饰器不需要改接口：内部构造 <c>query with { TopK = query.TopK * 4 }</c> 扩大候选集，
/// 重排后再截断到原始 TopK 即可。
/// </para>
/// </summary>
public interface IRetriever
{
    /// <summary>评测报告的分组键，例 <c>dense-only</c>、<c>hybrid-rrf</c>、<c>shuffle</c>。</summary>
    string Name { get; }

    /// <summary>按相关性降序返回，最多 <see cref="RetrievalQuery.TopK"/> 条。</summary>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default);
}
