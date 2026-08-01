using System.Text;

namespace IndustrialDocsRag.Ingestion;

/// <summary>
/// 生成稳定可读的块 id，格式 <c>{docId}#p{page}#{sectionSlug}#{ordinal}</c>。
/// <para>
/// 刻意保持 ASCII：这个 id 要由 LLM 在答案里原样复述，再被正则提取出来做引用校验。
/// 中文字符能用，但会增加模型复述出错的概率，收益不值得。
/// 人可读的章节标题保留在 <c>SectionPath</c> 里单独展示，信息并没有丢。
/// </para>
/// </summary>
public static class ChunkIdFactory
{
    private const string NoPage = "px";
    private const string NoSection = "sec";

    public static string Create(string docId, int? page, string sectionPath, int ordinal)
    {
        var doc = Sanitize(docId, fallback: "doc");
        var pagePart = page is { } p and > 0 ? $"p{p}" : NoPage;
        var section = SectionSlug(sectionPath);

        return $"{doc}#{pagePart}#{section}#{ordinal:D3}";
    }

    /// <summary>
    /// 从章节路径提取编号做 slug：<c>"7 故障处理 &gt; 7.3 状态字"</c> → <c>"7-3"</c>。
    /// 取最后一段（最具体的那级），只保留其中的数字与点，再把点换成连字符。
    /// 章节没有编号时退回 <c>sec</c>，靠 ordinal 区分。
    /// </summary>
    private static string SectionSlug(string sectionPath)
    {
        if (string.IsNullOrWhiteSpace(sectionPath))
        {
            return NoSection;
        }

        var last = sectionPath.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];

        var sb = new StringBuilder();
        foreach (var ch in last)
        {
            if (char.IsAsciiDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is '.' or '-' && sb.Length > 0)
            {
                sb.Append('-');
            }
            else if (sb.Length > 0)
            {
                // 编号后面开始出现文字，编号部分到此为止
                break;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : NoSection;
    }

    private static string Sanitize(string value, string fallback)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var result = sb.ToString().Trim('-');
        return result.Length > 0 ? result : fallback;
    }
}
