using System.Text;
using System.Text.RegularExpressions;

namespace IndustrialDocsRag.Eval;

/// <summary>
/// 把「页码 + 关键串」的锚点解析成源 Markdown 里的字符偏移。
/// <para>
/// 三种情况一律硬失败：页内找不到、页内不唯一、页码不存在。
/// 理由与 <c>SparseModelStore</c> 的同源守卫一致 —— <b>静默劣化比崩溃危险</b>：
/// 解析不出的锚点会让那道题对所有配置永远判 0，静默拉低整张表，
/// 而看数字的人只会以为「这道题确实难」。
/// </para>
/// </summary>
public static partial class AnchorResolver
{
    /// <summary>
    /// 页锚点，格式由 <c>tools/pdf_to_md.py</c> 产出，与 <c>MarkdownChunker.PageAnchorPattern</c> 认的是同一个标记。
    /// <para>
    /// 故意不用 <c>^</c>/<c>$</c> 逐行锚定：<c>MarkdownChunkSource</c> 解析 <c>source:</c> 头时早年吃过一次亏——
    /// 正则收尾写成 <c>--&gt;$</c>，LF 语料下单测全绿，真实 CRLF 语料里 <c>$</c> 前面卡着一个 <c>\r</c> 导致匹配失败
    /// （见 docs/plan-week2.md §8.12）。这里直接在全文里找子串，不依赖行结构，天然对 CRLF/LF 都成立。
    /// </para>
    /// <para>
    /// label 捕获组用 <c>\S*</c>，与 <c>MarkdownChunker.PageAnchorPattern</c> 保持字面一致而不是各写各的——
    /// Eval 只依赖 Core，Ingestion 在另一个项目，两边没法共享同一个 <see cref="Regex"/> 实例，
    /// 只能靠两处正则的文本长得一样来保证它们认的是同一套标记语法。哪天标记格式改了，
    /// 全局搜 <c>page:(\d+)</c> 至少能同时翻出这两处，不容易漏改一处。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"<!--\s*page:(\d+)\s+label:(\S*)\s*-->")]
    private static partial Regex PageMarkerPattern { get; }

    /// <summary>
    /// 解析一批题目的全部锚点。
    /// <para>
    /// 一次性收集所有失败再统一抛出，不是遇到第一个错误就退出 —— 评测集是手写的，
    /// 页码或关键串写错是常态，一轮改完比来回跑好几轮省事得多。
    /// </para>
    /// </summary>
    /// <param name="markdown">语料全文，须与产出 <c>DocumentChunk.CharOffset</c> 的那份字符串完全一致。</param>
    /// <param name="questions">待解析的题目集合。</param>
    /// <exception cref="InvalidDataException">
    /// 任意锚点的页码不存在、页内找不到 needle、或 needle 在页内不唯一时抛出，
    /// 消息里包含全部出错的 qid，一条不漏。
    /// </exception>
    public static IReadOnlyList<ResolvedAnchor> Resolve(
        string markdown, IReadOnlyList<EvalQuestion> questions)
    {
        var pages = SplitPages(markdown);
        var resolved = new List<ResolvedAnchor>();
        var errors = new List<string>();

        foreach (var question in questions)
        {
            foreach (var anchor in question.Anchors)
            {
                if (!pages.TryGetValue(anchor.Page, out var range))
                {
                    errors.Add($"[{question.Qid}] 页码 {anchor.Page} 在语料中不存在。");
                    continue;
                }

                var span = markdown.AsSpan(range.Start, range.Length);
                var count = Count(span, anchor.Needle);

                switch (count)
                {
                    case 0:
                        errors.Add(
                            $"[{question.Qid}] needle `{anchor.Needle}` 在 p.{anchor.Page} 内找不到。" +
                            "改用该页确实出现的串，或核对页码。");
                        break;

                    case > 1:
                        errors.Add(
                            $"[{question.Qid}] needle `{anchor.Needle}` 在 p.{anchor.Page} 内出现 {count} 次，" +
                            "不唯一。换一个更长或更具区分度的串 —— 不要放宽判定去迁就。");
                        break;

                    default:
                        var offsetInPage = span.IndexOf(anchor.Needle, StringComparison.Ordinal);
                        resolved.Add(new ResolvedAnchor(
                            question.Qid, anchor.Page, anchor.Needle, range.Start + offsetInPage));
                        break;
                }
            }
        }

        if (errors.Count > 0)
        {
            var message = new StringBuilder($"{errors.Count} 处锚点无法解析：");
            foreach (var error in errors)
            {
                message.Append(Environment.NewLine).Append("  ").Append(error);
            }

            throw new InvalidDataException(message.ToString());
        }

        return resolved;
    }

    /// <summary>
    /// 页码 → 该页在全文中的区间。
    /// <para>
    /// 区间从页标记<b>结束之后</b>开始，到下一个页标记之前为止 —— 标记自身的注释文本
    /// （<c>&lt;!-- page:129 label:128 --&gt;</c>）不属于该页内容，不能算进搜索范围。
    /// <c>MarkdownChunker</c> 消费掉标记行后直接 <c>continue</c>，从不把它产出成 chunk，
    /// 若这里把标记文本也纳入页区间，落在标记内部的偏移会永远解析「成功」，
    /// 但没有任何 chunk 能覆盖它——那道题会对所有配置永远判 0 且不报任何错，
    /// 正是这个类存在的理由要防的那种失效。反过来，标记里还含着页码本身的数字，
    /// 短数字 needle 会在标记文本和正文里各命中一次，被误判成「不唯一」而硬失败——
    /// 一道原本没问题的题会被逼着重写。两个方向的坑都要求标记文本必须被排除在页区间外。
    /// </para>
    /// </summary>
    private static Dictionary<int, (int Start, int Length)> SplitPages(string markdown)
    {
        var pages = new Dictionary<int, (int Start, int Length)>();
        var matches = PageMarkerPattern.Matches(markdown);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var page = int.Parse(matches[i].Groups[1].Value);

            pages[page] = (start, end - start);
        }

        return pages;
    }

    /// <summary>
    /// needle 在 haystack 内的出现次数，允许重叠计数（每次命中只前进 1 个字符再找下一次）。
    /// 这里只关心「唯一 vs 不唯一」，重叠算法比按 needle 长度跳跃更保守，不会把本该判「不唯一」
    /// 的情况漏判成唯一。
    /// </summary>
    private static int Count(ReadOnlySpan<char> haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var from = 0;

        while (from <= haystack.Length - needle.Length)
        {
            var at = haystack[from..].IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            count++;
            from += at + 1;
        }

        return count;
    }
}
