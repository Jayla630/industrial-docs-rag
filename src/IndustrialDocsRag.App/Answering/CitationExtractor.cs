using System.Text.RegularExpressions;
using IndustrialDocsRag.Core.Answering;
using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.App.Answering;

/// <param name="Citations">已确认真实存在的引用，按在答案中首次出现的顺序排列。</param>
/// <param name="RejectedIds">答案里出现但不在召回集中的编号，即模型编造的引用。</param>
public sealed record CitationExtraction(
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<string> RejectedIds);

/// <summary>
/// 从答案正文里提取 <c>[chunk-id]</c> 标记，并与本轮召回集求交集。
/// <para>
/// <b>交集校验不能省。</b> 模型一定会写出格式合法但不存在的编号 —— 有时是把
/// <c>#000</c> 记成 <c>#001</c>，有时是凭空造一个看起来很像的。不校验的话，
/// Week4 点击引用会直接崩，而且答案的可信度是假的：读者以为每句话都有出处，
/// 实际上出处指向不存在的块。
/// </para>
/// </summary>
public static partial class CitationExtractor
{
    /// <summary>引用标记里放进 Citation.Quote 的原文预览长度。</summary>
    private const int QuoteLength = 120;

    public static CitationExtraction Extract(string answer, IReadOnlyList<RetrievedChunk> hits)
    {
        var byId = hits.ToDictionary(h => h.Chunk.Id, h => h.Chunk, StringComparer.Ordinal);

        var citations = new List<Citation>();
        var rejected = new List<string>();
        var seenValid = new HashSet<string>(StringComparer.Ordinal);
        var seenRejected = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in BracketPattern().Matches(answer))
        {
            // 一个方括号里可能塞了多个编号（"[a, b]"），逐个拆开验证
            foreach (var token in SeparatorPattern().Split(match.Groups[1].Value))
            {
                var id = token.Trim();
                if (id.Length == 0)
                {
                    continue;
                }

                if (byId.TryGetValue(id, out var chunk))
                {
                    if (seenValid.Add(id))
                    {
                        citations.Add(new Citation(
                            chunk.Id,
                            chunk.SourceFile,
                            chunk.PageNumber,
                            chunk.SectionPath,
                            Preview(chunk.Text)));
                    }
                }
                else if (LooksLikeChunkId(id) && seenRejected.Add(id))
                {
                    // 只把"长得像 chunk id"的算作幻觉引用。
                    // 模型偶尔会写 "[1]"、"[表5-2]" 这类普通标注，那不是编造的编号，不该计入。
                    rejected.Add(id);
                }
            }
        }

        return new CitationExtraction(citations, rejected);
    }

    /// <summary>
    /// chunk id 的形状是 <c>{doc}#p{page}#{section}#{ordinal}</c>，
    /// 即含至少两个 <c>#</c> 且全为 ASCII。
    /// </summary>
    private static bool LooksLikeChunkId(string value)
        => value.Count(c => c == '#') >= 2 && value.All(char.IsAscii);

    private static string Preview(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= QuoteLength ? flat : string.Concat(flat.AsSpan(0, QuoteLength), "…");
    }

    // 半角和全角方括号都要认：中文模型输出全角【】的情况不罕见
    [GeneratedRegex(@"[\[【]([^\]】\r\n]{1,200})[\]】]")]
    private static partial Regex BracketPattern();

    [GeneratedRegex(@"[,，、;；\s]+")]
    private static partial Regex SeparatorPattern();
}
