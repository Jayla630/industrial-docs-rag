namespace IndustrialDocsRag.Core.Documents;

/// <summary>
/// 切块的类型。决定 Ingestion 侧如何拼装 <see cref="DocumentChunk.Text"/>，
/// 也让 Week3 的评测能按类型分组统计召回率 —— 散文段落和表格行的召回难度差异很大，
/// 混在一个数字里看不出问题出在哪。
/// </summary>
public enum ChunkKind
{
    /// <summary>普通正文段落。</summary>
    Text,

    /// <summary>
    /// 对象字典 / 寄存器表里的单独一行，且已上下文化：
    /// Text 中含表标题 + 表头 + 行内容，使这一行脱离原表也能被独立理解。
    /// 裸行（"0x6041 | Statusword | UINT16 | RO"）语义太稀薄，稠密向量之间几乎没有区分度。
    /// </summary>
    TableRow,

    /// <summary>
    /// 整张表作为一块。谨慎使用：块太大时召回后 LLM 要在几百行里找目标，容易淹没。
    /// </summary>
    TableBlock,

    /// <summary>代码片段、参数列表、接线说明等结构化但非表格的内容。</summary>
    CodeOrList,
}
