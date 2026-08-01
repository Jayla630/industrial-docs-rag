namespace IndustrialDocsRag.Core.Normalization;

/// <summary>
/// 工业术语的书写规范化：<c>0x6041</c> / <c>6041h</c> / <c>6041H</c> / <c>0X6041</c> / <c>60 41h</c>
/// 在同一本手册里都会出现，用户提问时的写法更乱。
/// <para>
/// 必须在入库侧和查询侧对称调用 —— 只做一侧比两侧都不做更糟，会把本来能对上的写法拆散。
/// </para>
/// <para>
/// Week1 是 <see cref="PassthroughTermNormalizer"/> 占位；Week3 换成真实现，
/// 把「开 / 关规范化的 Top-5 召回率差异」做成一组对比实验。
/// </para>
/// </summary>
public interface ITermNormalizer
{
    /// <summary>写进评测报告的标识，例 <c>passthrough</c>、<c>hex-unify</c>。</summary>
    string Name { get; }

    string Normalize(string text);
}
