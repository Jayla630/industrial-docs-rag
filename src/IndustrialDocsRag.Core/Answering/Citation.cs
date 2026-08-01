namespace IndustrialDocsRag.Core.Answering;

/// <summary>答案里的一条引用，已通过与召回集求交集校验、确认不是 LLM 编造的。</summary>
/// <param name="ChunkId">被引用的块 id。</param>
/// <param name="SourceFile">原始文件名。</param>
/// <param name="PageNumber">PDF 物理页码，Week4 用它跳页；为 null 时退化为按 <paramref name="SectionPath"/> 定位。</param>
/// <param name="SectionPath">章节标题路径。</param>
/// <param name="Quote">被引用的原文片段，Week4 用来在原文里高亮。</param>
public sealed record Citation(
    string ChunkId,
    string SourceFile,
    int? PageNumber,
    string SectionPath,
    string Quote);
