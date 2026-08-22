using IndustrialDocsRag.Core.Documents;

namespace IndustrialDocsRag.Eval;

/// <summary>
/// 命中判定：召回块在源 Markdown 中占据的区间是否覆盖锚点。
/// <para>
/// 用 <see cref="DocumentChunk.SourceLength"/> 而不是 <c>Text.Length</c> ——
/// <see cref="ChunkKind.TableRow"/> 的 <c>Text</c> 是拼装出来的（补了章节路径和表头字段名），
/// 比源行长得多，用它算区间会一路盖到后面几行。表格行占本项目语料的 94%，
/// 这个错误会<b>系统性抬高所有配置的召回率</b>，而且不报任何错。
/// <c>DocumentChunk.SourceLength</c> 这个字段在 Week2 存在的唯一理由就是今天这一步。
/// </para>
/// </summary>
public static class RelevanceJudge
{
    /// <summary>
    /// 区间左闭右开：起点算命中，<c>起点 + 长度</c> 那一位属于下一块。
    /// 长度为 0 的块不覆盖任何锚点 —— 否则它会成为一块「命中一切」的万能砖。
    /// </summary>
    public static bool Covers(DocumentChunk chunk, ResolvedAnchor anchor)
        => anchor.CharOffset >= chunk.CharOffset
           && anchor.CharOffset < chunk.CharOffset + chunk.SourceLength;
}
