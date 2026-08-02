namespace IndustrialDocsRag.Retrieval.Sparse;

/// <summary>
/// 工业手册的分词器：ASCII 术语整体保留，中文按相邻二元组切。
/// <para>
/// 这是本项目稀疏侧的地基。通用分词器会把 <c>0x22010201</c>、<c>H06-08</c> 拆成碎片，
/// 而这些低频、高 IDF 的术语恰恰是 BM25 唯一能压过稠密向量的地方
/// （稠密向量对数字最不敏感，见 §9.12 实测）。拆碎了，混合检索就没有意义了。
/// </para>
/// </summary>
public static class IndustrialTokenizer
{
    /// <summary>
    /// 分词规则的标识，写进 BM25 模型文件。规则一改，词表 id 的语义就变了、
    /// 落盘的权重全部失效，所以加载时要比对它。改了规则记得改这个版本号。
    /// </summary>
    public const string Name = "industrial-v1";

    public static IEnumerable<string> Tokenize(string text)
    {
        var i = 0;

        while (i < text.Length)
        {
            if (IsTermChar(text[i]))
            {
                var start = i;
                while (i < text.Length && IsTermChar(text[i]))
                {
                    i++;
                }

                foreach (var token in EmitTerm(text[start..i]))
                {
                    yield return token;
                }
            }
            else if (IsCjkIdeograph(text[i]))
            {
                var start = i;
                while (i < text.Length && IsCjkIdeograph(text[i]))
                {
                    i++;
                }

                foreach (var token in EmitBigrams(text[start..i]))
                {
                    yield return token;
                }
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// 中文按相邻二元组切 —— 无词典的中文 IR 经典做法。
    /// <para>
    /// 单字歧义太大（「制」「动」分开几乎没有区分度），二元组才有；
    /// 而引词典分词器要多一个依赖，且通用词典对「制动电阻」「转矩限制」这类
    /// 工业专有词未必切得对，反而需要自建词典补救。
    /// </para>
    /// <para>标点不参与组合：<c>编码器、电缆</c> 不该产出「器电」这种跨标点的伪词。</para>
    /// </summary>
    private static IEnumerable<string> EmitBigrams(string run)
    {
        if (run.Length == 1)
        {
            // 被标点夹住的孤立单字，不成词就等于内容凭空丢了
            yield return run;
            yield break;
        }

        for (var i = 0; i + 1 < run.Length; i++)
        {
            yield return run.Substring(i, 2);
        }
    }

    /// <summary>
    /// 只认中日韩表意文字本身，标点（、。（）) 一律当分隔符。
    /// </summary>
    private static bool IsCjkIdeograph(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    /// <summary>
    /// 术语内部允许点和连字符：<c>E201.2</c>、<c>H06-08</c>、<c>2006-09h</c> 都是一个词。
    /// </summary>
    private static bool IsTermChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';

    private static IEnumerable<string> EmitTerm(string raw)
    {
        // 术语两端的点和连字符是标点，不是词的一部分（句末的「。」已由分隔逻辑挡掉，
        // 但 ASCII 的 `.` 会粘在词尾，如 `H06-08.`）
        var term = raw.Trim('.', '-', '_').ToLowerInvariant();

        if (term.Length == 0)
        {
            yield break;
        }

        yield return term;

        if (FaultCodeFamily(term) is { } family)
        {
            yield return family;
        }
    }

    /// <summary>
    /// 故障码的族号：<c>e201.2</c> → <c>e201</c>。
    /// <para>
    /// 用户会问「E201 是什么故障」，而手册的故障码表里 <c>E201</c> 只出现在合并单元格的首行，
    /// 其余各行的「故障」列是空的（靠前向填充补上）。不额外产出族号，
    /// 按 <c>E201</c> 查就只能命中一行。代价是 <c>e201</c> 的 IDF 被稀释一点。
    /// </para>
    /// </summary>
    private static string? FaultCodeFamily(string term)
    {
        var dot = term.IndexOf('.');
        if (dot <= 1 || dot == term.Length - 1)
        {
            return null;
        }

        var head = term[..dot];

        // 形如「字母 + 数字」，且小数点后全是数字
        return char.IsAsciiLetter(head[0])
               && head.Skip(1).All(char.IsAsciiDigit)
               && head.Length > 1
               && term[(dot + 1)..].All(char.IsAsciiDigit)
            ? head
            : null;
    }
}
