using IndustrialDocsRag.Retrieval.Sparse;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 分词是「稀疏侧能不能接住稠密侧接不住的精确术语」这个核心论点的地基。
/// 通用分词器会把 0x22010201 拆成碎片，那样 BM25 的高 IDF 优势就没了。
/// </summary>
public class IndustrialTokenizerTests
{
    private static List<string> Tokens(string text) => IndustrialTokenizer.Tokenize(text).ToList();

    [Fact]
    public void Tokenize_十六进制值整体成词()
    {
        Assert.Contains("0x22010201", Tokens("辅助码（203Fh）: 0x22010201"));
    }

    [Fact]
    public void Tokenize_h后缀的十六进制写法整体成词()
    {
        Assert.Contains("603fh", Tokens("错误码（603Fh）"));
    }

    /// <summary>
    /// E201.2 要整体成词（精确匹配），同时额外产出族号 e201 ——
    /// 用户会问「E201 是什么故障」，而手册里 E201 只在合并单元格的首行出现。
    /// </summary>
    [Fact]
    public void Tokenize_故障码整体成词并额外产出族号()
    {
        var tokens = Tokens("显示: E201.2");

        Assert.Contains("e201.2", tokens);
        Assert.Contains("e201", tokens);
    }

    [Fact]
    public void Tokenize_参数号不被连字符拆开()
    {
        Assert.Contains("h06-08", Tokens("参数: H06-08"));
    }

    [Fact]
    public void Tokenize_大小写归一()
    {
        Assert.Contains("uint16", Tokens("数据类型: UINT16"));
    }

    /// <summary>
    /// `0~0xFFFF` 里的 `~` 是分隔符，不能把两个数值粘成一个词。
    /// </summary>
    [Fact]
    public void Tokenize_范围写法按分隔符切开()
    {
        var tokens = Tokens("数据范围: 0~0xFFFF");

        Assert.Contains("0", tokens);
        Assert.Contains("0xffff", tokens);
    }

    /// <summary>
    /// 中文按相邻二元组切 —— 无词典的中文 IR 经典做法。
    /// 单字歧义太大（「制」「动」分开几乎没有区分度），二元组才有。
    /// </summary>
    [Fact]
    public void Tokenize_中文按相邻二元组切()
    {
        var tokens = Tokens("过流");

        Assert.Equal(["过流"], tokens);
    }

    [Fact]
    public void Tokenize_中文长串产出重叠的二元组()
    {
        Assert.Equal(["制动", "动电", "电阻"], Tokens("制动电阻"));
    }

    [Fact]
    public void Tokenize_中英混排各按各的规则()
    {
        var tokens = Tokens("V 相过流");

        Assert.Contains("v", tokens);
        Assert.Contains("相过", tokens);
        Assert.Contains("过流", tokens);
    }

    /// <summary>
    /// 标点是分隔符，不能跨过它组二元组 —— 「编码器、电缆」不该产出「器电」这种伪词。
    /// </summary>
    [Fact]
    public void Tokenize_标点不参与二元组()
    {
        var tokens = Tokens("编码器、电缆");

        Assert.DoesNotContain("器电", tokens);
        Assert.Contains("编码", tokens);
        Assert.Contains("电缆", tokens);
    }

    /// <summary>
    /// 单个汉字被标点夹住时仍要成词，否则内容凭空丢失。
    /// </summary>
    [Fact]
    public void Tokenize_孤立单字也成词()
    {
        Assert.Contains("否", Tokens("能否复位: 否"));
    }
}
