using System.Text;
using System.Text.RegularExpressions;
using IndustrialDocsRag.Core.Documents;

namespace IndustrialDocsRag.Ingestion;

/// <summary>
/// 把 <c>tools/pdf_to_md.py</c> 产出的带页码锚点的 Markdown 切成 <see cref="DocumentChunk"/>。
/// </summary>
/// <param name="targetChars">
/// 正文块的目标长度。表格行不受它约束 —— 一行就是一块，长短由手册决定。
/// </param>
public sealed partial class MarkdownChunker(int targetChars = 500)
{
    /// <summary>
    /// 页码锚点：<c>&lt;!-- page:129 label:128 --&gt;</c>。
    /// 必须精确匹配 <c>page:</c>，否则文件头的 <c>&lt;!-- pages: 212 --&gt;</c> 会被误读成第 212 页。
    /// </summary>
    [GeneratedRegex(@"^<!--\s*page:(\d+)\s+label:(\S*)\s*-->$")]
    private static partial Regex PageAnchorPattern { get; }

    /// <summary>Markdown 表格的分隔行，形如 <c>| --- | --- |</c>。</summary>
    [GeneratedRegex(@"^\|[\s|:-]+\|$")]
    private static partial Regex TableSeparatorPattern { get; }

    /// <summary>
    /// 目录行：一长串点号后跟页码，如 <c>3.2.1 主电路端子介绍......46</c>。
    /// 正文里不会出现连续四个以上的点，所以这个判据不会误伤内容。
    /// </summary>
    [GeneratedRegex(@"\.{4,}\s*\d+$")]
    private static partial Regex TableOfContentsPattern { get; }

    /// <summary>
    /// 手册正文前的封面、前言与安全注意事项不属于任何章节。
    /// 安全注意事项是有价值的内容，不能因为没有章节就丢掉；而 SectionPath 又不允许为空。
    /// </summary>
    private const string FrontMatterSection = "手册前置部分";

    public IReadOnlyList<DocumentChunk> Split(string markdown, string docId, string sourceFile)
    {
        var state = new SplitState(docId, sourceFile, targetChars);
        var tableBlock = new List<SourceLine>();
        var offset = 0;

        foreach (var raw in markdown.Split('\n'))
        {
            // 行首空白不算进块的覆盖区间，否则偏移会指到内容前面去
            var lineStart = offset + (raw.Length - raw.TrimStart().Length);
            offset += raw.Length + 1;

            var line = raw.TrimEnd('\r').Trim();
            var source = new SourceLine(line, lineStart);

            if (line.StartsWith('|'))
            {
                tableBlock.Add(source);
                continue;
            }

            if (tableBlock.Count > 0)
            {
                FlushTable(tableBlock, state);
                tableBlock.Clear();
            }

            if (line.Length == 0 || TableOfContentsPattern.IsMatch(line))
            {
                continue;
            }

            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                if (PageAnchorPattern.Match(line) is { Success: true } anchor)
                {
                    state.SetPage(
                        int.Parse(anchor.Groups[1].ValueSpan),
                        anchor.Groups[2].Value is { Length: > 0 } label ? label : null);
                }

                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var title = line[3..].Trim();
                if (TryParseChapterNumber(title, out var number))
                {
                    state.FlushProse();
                    state.EnterChapter(title, number);
                }
                else
                {
                    state.AddProse(source with { Text = title });
                }

                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var title = line[4..].Trim();
                if (IsRealSectionHeading(title, state.ChapterNumber))
                {
                    state.FlushProse();
                    state.EnterSection(title);
                }
                else
                {
                    // §9.6 的教训：判为非标题的行交回正文，不能丢
                    state.AddProse(source with { Text = title });
                }

                continue;
            }

            state.AddProse(source);
        }

        if (tableBlock.Count > 0)
        {
            FlushTable(tableBlock, state);
        }

        state.FlushProse();
        return state.Chunks;
    }

    /// <summary>
    /// 一个表格块 → 每个数据行一条 <see cref="ChunkKind.TableRow"/>。
    /// <para>
    /// 跨页的长表在每页都会重复一次表头（实测 p.129 / p.130 的故障码表就是如此），
    /// 所以每个页内片段自足，不需要跨页拼接表头。
    /// </para>
    /// </summary>
    private static void FlushTable(List<SourceLine> block, SplitState state)
    {
        // 表头 + 分隔行 + 至少一个数据行，缺一不成表；不成表就当正文，不能丢
        if (block.Count < 3 || !TableSeparatorPattern.IsMatch(block[1].Text))
        {
            foreach (var line in block)
            {
                state.AddProse(line);
            }

            return;
        }

        state.FlushProse();

        var headers = ParseRow(block[0].Text).Select(NormalizeCjkGaps).ToArray();
        var dataStart = MergeCompositeHeader(headers, block);
        string[]? previous = null;

        foreach (var line in block.Skip(dataStart))
        {
            var cells = ParseRow(line.Text);
            if (cells.Length == 0)
            {
                continue;
            }

            ForwardFillLeadingCells(cells, previous);
            previous = cells;

            state.AddTableRow(RenderRow(headers, cells, state.SectionPath), line);
        }
    }

    private static string[] ParseRow(string line)
        => line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    /// <summary>
    /// 把复合表头的续行并进表头，返回数据行的起始下标。
    /// <para>
    /// 参数一览表是三行表头，PDF 转换只认了第一行，真正的列名落在后两行里，
    /// 未命名的列被填成 <c>列2</c>、<c>列3</c> 这类占位符：
    /// </para>
    /// <code>
    /// | 参数组 | 列2   | 列3    | 名称 | …
    /// | 十六进制 |      | 十进制  |     | …   ← 表头续行，不是数据
    /// | 组别   | 索引码 | 参数   |     | …   ← 表头续行，不是数据
    /// | 2006  | 09h   | H06-08 | 速度正向限制 | …
    /// </code>
    /// <para>
    /// 实测全语料 39% 的块受此影响。不并的话，列名丢成 <c>列3: H06-08</c>，
    /// 检索侧就匹配不到「索引码」「参数」这些用户真会打的词，还多出两条无意义的块。
    /// </para>
    /// <para>
    /// 判据有三重，避免把真数据行吃掉：表头本来就有未命名列；续行的<b>末列为空</b>
    /// （真数据行会把最后一列填满）；且该行确实为某个未命名列提供了名字。
    /// </para>
    /// </summary>
    private static int MergeCompositeHeader(string[] headers, List<SourceLine> block)
    {
        const int maxContinuationRows = 2;

        var unnamed = headers
            .Select((h, i) => (Header: h, Index: i))
            .Where(x => IsUnnamedHeader(x.Header))
            .Select(x => x.Index)
            .ToHashSet();

        if (unnamed.Count == 0)
        {
            return 2;
        }

        var dataStart = 2;

        for (var consumed = 0; consumed < maxContinuationRows && dataStart < block.Count; consumed++)
        {
            var cells = ParseRow(block[dataStart].Text);

            // 真数据行会把最后一列填满，表头续行不会
            if (cells.Length == 0 || cells[^1].Length > 0)
            {
                break;
            }

            // 后出现的续行更具体，覆盖先前的取值（十进制 → 参数）
            var contributed = false;
            foreach (var i in unnamed)
            {
                if (i < cells.Length && cells[i].Length > 0)
                {
                    headers[i] = NormalizeCjkGaps(cells[i]);
                    contributed = true;
                }
            }

            if (!contributed)
            {
                break;
            }

            dataStart++;
        }

        return dataStart;
    }

    /// <summary>
    /// 空表头，或 <c>tools/pdf_to_md.py</c> 给无名列填的 <c>列N</c> 占位符。
    /// </summary>
    private static bool IsUnnamedHeader(string header)
        => header.Length == 0
            || (header.StartsWith('列') && header.Length > 1 && header.Skip(1).All(char.IsAsciiDigit));

    /// <summary>
    /// 合并单元格的前向填充，<b>只作用于行首连续的空列</b>。
    /// <para>
    /// 故障码表里 <c>E201.1</c>、<c>E201.2</c> 那几行的「故障」列与上方 <c>E201</c> 合并而为空，
    /// 不填充，这一行离开原表就丢了 <c>E201</c> 这个最关键的检索词。
    /// </para>
    /// <para>
    /// 但填充必须止于第一个非空列。中间列为空就照抄上一行，等于凭空给这一行造了一个值 ——
    /// 那正是 §9.6 记的那类事故（用坏数据静默替换好数据），在工业手册场景下不可接受。
    /// </para>
    /// </summary>
    private static void ForwardFillLeadingCells(string[] cells, string[]? previous)
    {
        if (previous is null)
        {
            return;
        }

        for (var i = 0; i < cells.Length && cells[i].Length == 0; i++)
        {
            if (i < previous.Length)
            {
                cells[i] = previous[i];
            }
        }
    }

    /// <summary>
    /// 拼成自足的一行：章节路径 + 「表头: 值」。空值不产出字段 ——
    /// 否则每行都拖着一串 <c>单位: | 数据范围: |</c>，把本来就稀薄的区分度进一步稀释。
    /// </summary>
    private static string RenderRow(string[] headers, string[] cells, string sectionPath)
    {
        var fields = new List<string>(cells.Length);

        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i].Length == 0)
            {
                continue;
            }

            var header = i < headers.Length ? headers[i] : $"列{i + 1}";
            fields.Add(header.Length > 0 ? $"{header}: {cells[i]}" : cells[i]);
        }

        var body = string.Join(" | ", fields);
        return sectionPath.Length > 0 ? $"{sectionPath}\n{body}" : body;
    }

    /// <summary>
    /// 去掉中日韩字符之间的空白：<c>错误码 （603Fh）</c> → <c>错误码（603Fh）</c>。
    /// 这是 PDF 表头跨行断字的残留（<c>可访\n问性</c> 一类）。
    /// <para>
    /// 只在两侧都是中日韩字符时去，ASCII 之间的空格必须保留 ——
    /// 否则 <c>0~0xFFFF</c>、<c>UINT16</c>、<c>P 相过流</c> 会被破坏。
    /// </para>
    /// </summary>
    private static string NormalizeCjkGaps(string cell)
    {
        var sb = new StringBuilder(cell.Length);

        for (var i = 0; i < cell.Length; i++)
        {
            if (char.IsWhiteSpace(cell[i])
                && sb.Length > 0
                && IsCjk(sb[^1])
                && FindNext(cell, i) is { } next
                && IsCjk(next))
            {
                continue;
            }

            sb.Append(cell[i]);
        }

        return sb.ToString().Trim();

        static char? FindNext(string s, int from)
        {
            for (var i = from; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i]))
                {
                    return s[i];
                }
            }

            return null;
        }
    }

    private static bool IsCjk(char c)
        => c is >= '⺀' and <= '鿿'    // 中日韩部首扩展 ~ 中日韩统一表意文字
            or >= '豈' and <= '﫿'      // 兼容表意文字
            or >= '︰' and <= '﹏'      // 中日韩兼容形式
            or >= '＀' and <= '･';     // 全角形式（含 （） 、 ：）

    /// <summary>「第5章 故障处理」→ 5。不是这个形状就不是真章节标题。</summary>
    private static bool TryParseChapterNumber(string title, out int number)
    {
        number = 0;

        if (!title.StartsWith('第'))
        {
            return false;
        }

        var end = 1;
        while (end < title.Length && char.IsAsciiDigit(title[end]))
        {
            end++;
        }

        return end > 1
            && end < title.Length
            && title[end] == '章'
            && int.TryParse(title[1..end], out number);
    }

    /// <summary>
    /// 真小节标题的判据：节号的首段必须等于当前章号。
    /// <para>
    /// PDF 转换会把规格表里的粗体行判成标题，实测有 <c>### 0.75 kW 220 V 3000 r/min</c>、
    /// <c>### 2.39 N·m 250 Hz 4.8 A</c> 这种。它们同样以数字开头，光看「像不像编号」分不出来；
    /// 但它们出现在第 1 章里而首段是 0 和 2，与章号一比就露馅了。
    /// </para>
    /// </summary>
    private static bool IsRealSectionHeading(string title, int chapterNumber)
    {
        if (chapterNumber == 0)
        {
            return false;
        }

        var end = 0;
        while (end < title.Length && char.IsAsciiDigit(title[end]))
        {
            end++;
        }

        return end > 0
            && int.TryParse(title[..end], out var first)
            && first == chapterNumber;
    }

    /// <summary>源文件里的一行，连同它在文件中的起始偏移。</summary>
    private readonly record struct SourceLine(string Text, int Offset);

    /// <summary>切分过程中的游标：当前页码、标题栈、正文缓冲、已产出的块。</summary>
    private sealed class SplitState(string docId, string sourceFile, int targetChars)
    {
        private readonly List<ProseLine> _prose = [];
        private string? _chapter;
        private string? _section;
        private int? _currentPage;
        private string? _currentLabel;

        /// <summary>缓冲区里仅剩的那个段落是上一块留下的重叠，不是新内容。</summary>
        private bool _carried;

        public List<DocumentChunk> Chunks { get; } = [];

        public int ChapterNumber { get; private set; }

        public string SectionPath => (_chapter, _section) switch
        {
            (null, null) => FrontMatterSection,
            (not null, null) => _chapter,
            (null, not null) => _section,
            _ => $"{_chapter} > {_section}",
        };

        public void SetPage(int page, string? label)
        {
            _currentPage = page;
            _currentLabel = label;
        }

        public void EnterChapter(string title, int number)
        {
            _chapter = title;
            ChapterNumber = number;
            _section = null;
        }

        public void EnterSection(string title) => _section = title;

        public void AddProse(SourceLine line)
        {
            _prose.Add(new ProseLine(line, _currentPage, _currentLabel));

            if (_prose.Sum(p => p.Source.Text.Length) < targetChars)
            {
                return;
            }

            // 攒够长度就出块，并把最后一个段落留给下一块作重叠：
            // 事实横跨切分点时，至少有一个块完整包含它。
            var carry = _prose[^1];
            EmitProse();
            _prose.Add(carry);
            _carried = true;
        }

        public void AddTableRow(string text, SourceLine source)
            => Emit(text, ChunkKind.TableRow, _currentPage, _currentLabel, source.Offset, source.Text.Length);

        public void FlushProse()
        {
            // 只剩一个重叠段落时不再单独出块 —— 它的内容已经完整落在上一块里了
            if (_prose.Count > 1 || (_prose.Count == 1 && !_carried))
            {
                EmitProse();
            }

            _prose.Clear();
            _carried = false;
        }

        private void EmitProse()
        {
            var start = _prose[0].Source.Offset;
            var end = _prose[^1].Source.Offset + _prose[^1].Source.Text.Length;

            Emit(
                string.Join('\n', _prose.Select(p => p.Source.Text)),
                ChunkKind.Text,
                _prose[0].Page,
                _prose[0].PageLabel,
                start,
                end - start);

            _prose.Clear();
            _carried = false;
        }

        /// <summary>正文缓冲里的一行，连同它所在的页 —— 块的页码取它第一行所在的页。</summary>
        private readonly record struct ProseLine(SourceLine Source, int? Page, string? PageLabel);

        private void Emit(
            string text, ChunkKind kind, int? page, string? pageLabel, int charOffset, int sourceLength)
        {
            var sectionPath = SectionPath;

            Chunks.Add(new DocumentChunk
            {
                Id = ChunkIdFactory.Create(docId, page, sectionPath, Chunks.Count),
                SourceFile = sourceFile,
                PageNumber = page,
                SectionPath = sectionPath,
                CharOffset = charOffset,
                SourceLength = sourceLength,
                Kind = kind,
                Text = text,
                Metadata = pageLabel is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["pageLabel"] = pageLabel },
            });
        }
    }
}
