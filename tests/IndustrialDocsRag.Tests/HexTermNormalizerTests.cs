using IndustrialDocsRag.Retrieval.Normalization;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 靶子是 README 里公开挂着的失败用例：用户打 `0x6041`，手册索引列写的是 `6041`，
/// 词面对不上，BM25 直接 0 结果（plan-week2.md §8.8 实测）。
/// <para>
/// 误伤比不做更糟 —— ITermNormalizer 的注释从 Week1 就写着这一条。
/// 所以下面反向用例（不该被改的）比正向用例还重要。
/// </para>
/// </summary>
public class HexTermNormalizerTests
{
    private static string N(string text) => new HexTermNormalizer().Normalize(text);

    [Fact]
    public void Name是hexunify()
    {
        Assert.Equal("hex-unify", new HexTermNormalizer().Name);
    }

    [Fact]
    public void 去掉0x前缀()
    {
        Assert.Equal("6041", N("0x6041"));
    }

    [Fact]
    public void 大写0X前缀同样处理()
    {
        Assert.Equal("6041", N("0X6041"));
    }

    [Fact]
    public void 去掉h后缀并小写()
    {
        Assert.Equal("603f", N("603Fh"));
        Assert.Equal("603f", N("603FH"));
    }

    [Fact]
    public void 长十六进制值同样归一()
    {
        Assert.Equal("22010201", N("0x22010201"));
    }

    [Fact]
    public void 句子里的十六进制值被归一其余原样()
    {
        Assert.Equal("辅助码(203f): 22010201", N("辅助码(203Fh): 0x22010201"));
    }

    // ↓ 以下是反向守卫：这些都不该被改动

    [Fact]
    public void 参数号不被改动()
    {
        Assert.Equal("H06-08", N("H06-08"));
    }

    [Fact]
    public void 型号不被改动()
    {
        Assert.Equal("MS1H1-75B30CB", N("MS1H1-75B30CB"));
    }

    /// <summary>
    /// `2006-09h` 的 `09h` 是索引码，不是独立的十六进制值。
    /// 整体是一个参数号 token，拆开归一会把它毁掉。
    /// </summary>
    [Fact]
    public void 带连字符的对象字典写法不被改动()
    {
        Assert.Equal("2006-09h", N("2006-09h"));
    }

    /// <summary>故障码 `E201.2` 里的数字不是十六进制值。</summary>
    [Fact]
    public void 故障码不被改动()
    {
        Assert.Equal("E201.2", N("E201.2"));
    }

    /// <summary>纯十进制不带十六进制标记时不动 —— 否则 `6000` 这类出厂值会被误当成十六进制。</summary>
    [Fact]
    public void 裸十进制数不被改动()
    {
        Assert.Equal("出厂值 6000", N("出厂值 6000"));
    }

    /// <summary>
    /// ★ `h` 结尾的英文单词不是十六进制。
    /// <c>each</c> 特别阴险：<c>e</c> <c>a</c> <c>c</c> 全是合法的十六进制字符，后面正好跟着 <c>h</c>，
    /// 只按「十六进制字符 + h」判会把它改成 <c>eac</c>。所以判据里必须要求至少含一个十进制数字。
    /// </summary>
    [Theory]
    [InlineData("each")]
    [InlineData("bath")]
    [InlineData("dash")]
    public void h结尾的英文单词不被改动(string word)
    {
        Assert.Equal(word, N(word));
    }

    /// <summary><c>1h</c> 在手册里更可能是「1 小时」而不是十六进制值 —— 后缀形式要求至少两位。</summary>
    [Fact]
    public void 单位十六进制后缀不被改动()
    {
        Assert.Equal("1h", N("1h"));
    }

    /// <summary>规范化必须幂等：入库侧和查询侧都调它，跑两遍结果不能变。</summary>
    [Fact]
    public void 幂等()
    {
        const string text = "对象 0x6041 与 603Fh 及参数 H06-08";

        Assert.Equal(N(text), N(N(text)));
    }

    /// <summary>
    /// 已知误报，刻意接受：手册 p.67 电池寿命算例里的 <c>24H</c> 是「24 小时」不是十六进制。
    /// <para>
    /// 全文扫描确认仅此一处 —— 大写 <c>H</c> 后缀全文只有 2 处（<c>0DH</c> 真十六进制 + 这个 <c>24H</c>），
    /// 而小写 <c>h</c> 后缀有 613 处，才是手册的实际惯例。按语料统计，收窄到只认小写是「少归一 1 个、
    /// 少误伤 1 个」，打平。
    /// </para>
    /// <para>
    /// 不收窄的理由与语料无关：规范化<b>入库侧和查询侧对称调用</b>，入库侧跟手册惯例走，
    /// 但查询侧是用户想怎么打就怎么打 —— 用户敲 <c>6041H</c> 完全正常，而弥合记法差异
    /// 正是本类存在的全部理由。为语料里 1 处误伤砍掉查询侧的大写支持，是拿主要用例换次要用例。
    /// </para>
    /// <para>这条测试钉住现状：若将来有人改规则让它「修好」，请先确认查询侧大写支持的代价。</para>
    /// </summary>
    [Fact]
    public void 已知误报_小时单位的大写H会被当成十六进制后缀()
    {
        Assert.Equal("24*10uA", N("24H*10uA"));
    }
}
