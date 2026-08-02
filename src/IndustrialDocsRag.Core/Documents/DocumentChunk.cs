namespace IndustrialDocsRag.Core.Documents;

/// <summary>
/// 检索与引用的最小单位。整条流水线（切分 → 嵌入 → 入库 → 召回 → 引用溯源）都围绕它。
/// </summary>
public sealed record DocumentChunk
{
    /// <summary>
    /// 稳定且可读的标识，格式 <c>{docId}#p{page}#{sectionSlug}#{ordinal}</c>，
    /// 例：<c>sv660n#p142#7-3-statusword#003</c>。
    /// 不用自增或 GUID —— LLM 要在答案里引用它，人也要能一眼看懂它指向哪。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>原始文件名，例 <c>sv660n-manual.pdf</c>。</summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// PDF 的「物理」页码（从 1 开始），Week4 用它驱动阅读器跳转 —— 阅读器只认物理页。
    /// 手册的「印刷」页码放在 <see cref="Metadata"/> 的 pageLabel 键下：
    /// 手册前面有封面、版本声明、目录，两者通常错位好几页，展示给人看要用印刷页码。
    /// 解析不出页码时为 null，此时溯源退化为章节级。
    /// </summary>
    public int? PageNumber { get; init; }

    /// <summary>章节标题路径，例 <c>7 故障处理 &gt; 7.3 状态字</c>。没有页码时的兜底定位手段。</summary>
    public required string SectionPath { get; init; }

    /// <summary>
    /// 本块在源 Markdown 中的起始字符偏移。
    /// 这不是装饰字段：Week3 的评测集把 ground truth 绑在「页码 + 关键字符串」而非 chunk id 上，
    /// 判定命中的方式是看召回块的 <c>[CharOffset, CharOffset + Text.Length)</c> 区间是否覆盖锚点。
    /// 有了它，后续调整切分规则不会让整套评测集失效。
    /// </summary>
    public int CharOffset { get; init; }

    /// <summary>
    /// 本块在源 Markdown 中占据的字符数，覆盖区间是 <c>[CharOffset, CharOffset + SourceLength)</c>。
    /// <para>
    /// 不能用 <c>Text.Length</c> 代替：<see cref="ChunkKind.TableRow"/> 的 <see cref="Text"/> 是
    /// 拼装出来的（补了章节路径和表头字段名），比源文本长得多，用它算区间会一路盖到后面几行，
    /// Week3 判命中就会误判。而表格行占了本项目语料的绝大多数。
    /// </para>
    /// </summary>
    public int SourceLength { get; init; }

    /// <summary>见 <see cref="ChunkKind"/>。</summary>
    public ChunkKind Kind { get; init; } = ChunkKind.Text;

    /// <summary>
    /// 送进嵌入模型和 prompt 的最终文本。
    /// 对 <see cref="ChunkKind.TableRow"/> 是已上下文化的结果，拼装动作发生在 Ingestion。
    /// </summary>
    public required string Text { get; init; }

    /// <summary>开放的附加字段，目前用到的键：pageLabel（印刷页码）。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
