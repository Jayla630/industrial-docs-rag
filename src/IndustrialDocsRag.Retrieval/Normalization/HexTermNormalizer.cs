using System.Text.RegularExpressions;
using IndustrialDocsRag.Core.Normalization;

namespace IndustrialDocsRag.Retrieval.Normalization;

/// <summary>
/// 十六进制记法归一：<c>0x6041</c> / <c>0X6041</c> / <c>603Fh</c> / <c>603FH</c> → <c>6041</c> / <c>603f</c>。
/// <para>
/// 靶子是 README 里公开挂着的失败用例：用户打 <c>0x6041</c>，手册索引列写的是 <c>6041</c>，
/// 词面对不上，BM25 返回 0 结果（<c>plan-week2.md</c> §8.8 实测）。
/// </para>
/// <para>
/// <b>只认明确的十六进制记法，不做通用数字归一。</b>
/// 参数号 <c>H06-08</c>、型号 <c>MS1H1-75B30CB</c>、对象字典写法 <c>2006-09h</c>、
/// 故障码 <c>E201.2</c> 一律不动 —— 规范化做坏了比不做更糟，
/// 这一条 <see cref="ITermNormalizer"/> 的注释从 Week1 就写着。
/// </para>
/// <para>
/// <b>只作用于稀疏侧。</b> <c>plan-week1.md</c> §9.10 的修正确认规范化对稠密向量价值不大，
/// 而 Week3 进一步要求它<b>不能</b>影响稠密侧，否则规范化 A/B 就不是单变量实验了。
/// 所以它经由 <c>Bm25SparseEncoder</c> 的构造参数进入系统，
/// <b>不注册成 DI 里的 <see cref="ITermNormalizer"/></b>（那样命令层会用它改写查询文本，
/// 而那段文本同时喂给稠密侧）。
/// </para>
/// </summary>
public sealed partial class HexTermNormalizer : ITermNormalizer
{
    public string Name => "hex-unify";

    /// <summary>两种写法各一条正则，依次施加。分开写而不是一条带分支的，是为了避免同名捕获组的歧义。</summary>
    public string Normalize(string text)
        => HexSuffix().Replace(HexPrefix().Replace(text, Lower), Lower);

    private static string Lower(Match match) => match.Groups["digits"].Value.ToLowerInvariant();

    /// <summary>
    /// <c>0x6041</c> / <c>0X6041</c> → <c>6041</c>。
    /// <para>
    /// 两侧的否定环视要求<b>不紧邻术语字符</b>（字母 / 数字 / <c>.</c> / <c>-</c> / <c>_</c>）——
    /// 与 <c>IndustrialTokenizer.IsTermChar</c> 同一套判据，保证被改写的一定是一个完整的 token。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9._-])0[xX](?<digits>[0-9A-Fa-f]+)(?![A-Za-z0-9._-])")]
    private static partial Regex HexPrefix();

    /// <summary>
    /// <c>603Fh</c> / <c>603FH</c> → <c>603f</c>。
    /// <para>
    /// 除了同样的边界约束，还有两条判据，每一条都对应一个真实的误伤：
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>必须含至少一个十进制数字</b>（<c>(?=[0-9A-Fa-f]*[0-9])</c>）——
    /// <c>each</c> 的 <c>e</c> <c>a</c> <c>c</c> 全是合法十六进制字符、后面正好跟着 <c>h</c>，
    /// 不加这条会被改成 <c>eac</c>。<c>bath</c> / <c>dash</c> 同理。
    /// </item>
    /// <item>
    /// <b>至少两位</b>（<c>{2,}</c>）—— 手册里的 <c>1h</c> 更可能是「1 小时」而不是十六进制值。
    /// </item>
    /// </list>
    /// <para>
    /// 参数号 <c>H06-08</c>、型号 <c>MS1H1-75B30CB</c>、对象字典写法 <c>2006-09h</c>、
    /// 故障码 <c>E201.2</c> 则都被边界约束挡下：它们的候选片段左右都紧邻术语字符。
    /// </para>
    /// <para>
    /// <b>已知误报（实测，接受）</b>：手册 p.67 的电池寿命算例 <c>24H*10uA</c> 里，
    /// <c>24H</c> 是「24 小时」不是十六进制，会被归一成 <c>24</c>。
    /// 全文扫描确认这是唯一一处（大写 <c>H</c> 后缀全文仅 2 处：<c>0DH</c> 真十六进制 + 这个 <c>24H</c>；
    /// 小写 <c>h</c> 后缀 613 处，才是手册的实际惯例）。
    /// 不为它收窄规则，是因为规范化在<b>查询侧</b>同样生效，而用户敲 <c>6041H</c> 完全正常 ——
    /// 弥合记法差异正是本类存在的理由。要真正区分需要语义信息，超出 token 级正则能做的事。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9._-])(?<digits>(?=[0-9A-Fa-f]*[0-9])[0-9A-Fa-f]{2,})[hH](?![A-Za-z0-9._-])")]
    private static partial Regex HexSuffix();
}
