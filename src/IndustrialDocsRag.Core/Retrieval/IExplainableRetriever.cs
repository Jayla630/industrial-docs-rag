namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>
/// 能解释自己是怎么召回的检索器。
/// <para>
/// 单开一个接口、不动 <see cref="IRetriever"/> 的签名，是因为「检索层可替换」这个主张
/// 靠的就是那个签名从 Week1 到接 Qdrant 一行没改。单路检索器与 ShuffleRetriever 不实现它 ——
/// 单路没有「融合」可解释，轨迹会退化成结果本身，没有信息量。
/// </para>
/// <para>
/// ★ 实现者必须让 <see cref="IRetriever.RetrieveAsync"/> 直接取
/// <see cref="RetrieveWithTraceAsync"/> 的 <see cref="RetrievalTrace.Fused"/>，不能各写一份。
/// 两条路径分叉的表现是「轨迹显示的 Top-K 跟实际检索的对不上」，不抛异常、只是判错。
/// </para>
/// </summary>
public interface IExplainableRetriever : IRetriever
{
    Task<RetrievalTrace> RetrieveWithTraceAsync(
        RetrievalQuery query,
        CancellationToken ct = default);
}
