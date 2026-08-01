using IndustrialDocsRag.Core.Documents;

namespace IndustrialDocsRag.Ingestion;

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(string ChunkId, ValidationSeverity Severity, string Message);

/// <summary>
/// 入库前的静态校验。只报告问题，不修改数据 —— 悄悄"修正"手写文件会让人失去对数据的掌控。
/// </summary>
public sealed class ChunkValidator
{
    /// <summary>低于此长度的块基本没有检索价值，多半是切分切碎了。</summary>
    public int MinTextLength { get; init; } = 15;

    /// <summary>
    /// 超过此长度告警。块太大有两个害处：召回后 LLM 要在一大段里找目标；
    /// 且一个块里混了多个主题，稠密向量会被平均成"什么都不像"。
    /// </summary>
    public int MaxTextLength { get; init; } = 2000;

    public IReadOnlyList<ValidationIssue> Validate(IReadOnlyList<DocumentChunk> chunks)
    {
        var issues = new List<ValidationIssue>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var id = string.IsNullOrWhiteSpace(c.Id) ? $"<第 {i + 1} 条，无 id>" : c.Id;

            if (string.IsNullOrWhiteSpace(c.Id))
            {
                issues.Add(new(id, ValidationSeverity.Error, "Id 为空"));
            }
            else if (seen.TryGetValue(c.Id, out var first))
            {
                // id 重复是最危险的一类错误：入库时后者会静默覆盖前者，
                // 表现为"某个块怎么都检索不到"，排查起来极其费时。
                issues.Add(new(id, ValidationSeverity.Error,
                    $"Id 重复，与第 {first + 1} 条冲突"));
            }
            else
            {
                seen[c.Id] = i;
            }

            if (string.IsNullOrWhiteSpace(c.Text))
            {
                issues.Add(new(id, ValidationSeverity.Error, "Text 为空"));
            }
            else
            {
                if (c.Text.Length < MinTextLength)
                {
                    issues.Add(new(id, ValidationSeverity.Warning,
                        $"Text 仅 {c.Text.Length} 字符，短于 {MinTextLength}，检索价值可能很低"));
                }

                if (c.Text.Length > MaxTextLength)
                {
                    issues.Add(new(id, ValidationSeverity.Warning,
                        $"Text 长 {c.Text.Length} 字符，超过 {MaxTextLength}，建议再切细"));
                }
            }

            if (string.IsNullOrWhiteSpace(c.SourceFile))
            {
                issues.Add(new(id, ValidationSeverity.Error, "SourceFile 为空"));
            }

            if (string.IsNullOrWhiteSpace(c.SectionPath))
            {
                // SectionPath 是没有页码时唯一的溯源手段，不能省
                issues.Add(new(id, ValidationSeverity.Error, "SectionPath 为空"));
            }

            if (c.PageNumber is <= 0)
            {
                issues.Add(new(id, ValidationSeverity.Error,
                    $"PageNumber 为 {c.PageNumber}，页码从 1 开始；解析不出页码时应留空而非填 0"));
            }

            if (c.CharOffset < 0)
            {
                issues.Add(new(id, ValidationSeverity.Error, $"CharOffset 为 {c.CharOffset}，不能为负"));
            }
        }

        return issues;
    }
}
