using IndustrialDocsRag.Eval;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 锚点解析是整套评测的地基。解析错了不会崩，只会让某道题永远判 0 或判中错块 ——
/// 而错的数字比错的代码难发现得多，所以这里一律硬失败。
/// </summary>
public class AnchorResolverTests
{
    /// <summary>三页的合成语料，页标记格式与 tools/pdf_to_md.py 的产出一致。</summary>
    private const string Markdown =
        """
        <!-- page:1 label:i -->
        封面与版本声明。

        <!-- page:2 label:1 -->
        故障: E201 | 显示: E201.2 | 辅助码(203Fh): 0x22010201

        <!-- page:3 label:2 -->
        使用外接制动电阻时请注意散热。
        制动电阻表面温度可达 100℃。
        """;

    private static EvalQuestion Question(string qid, params Anchor[] anchors) => new()
    {
        Qid = qid,
        Stratum = Stratum.Prose,   // 本类不关心分层，取任意合法值
        Query = "irrelevant",
        Anchors = anchors,
    };

    [Fact]
    public void Resolve_偏移指向needle在全文中的真实位置()
    {
        var resolved = AnchorResolver.Resolve(Markdown, [Question("q01", new Anchor(2, "0x22010201"))]);

        var one = Assert.Single(resolved);
        Assert.Equal(Markdown.IndexOf("0x22010201", StringComparison.Ordinal), one.CharOffset);
    }

    /// <summary>
    /// 同一个串在别的页出现不影响本页定位 —— 搜索范围必须约束在页区间内。
    /// </summary>
    [Fact]
    public void Resolve_搜索范围约束在指定页内()
    {
        const string md =
            """
            <!-- page:1 label:1 -->
            制动电阻

            <!-- page:2 label:2 -->
            制动电阻
            """;

        var resolved = AnchorResolver.Resolve(md, [Question("q01", new Anchor(2, "制动电阻"))]);

        var one = Assert.Single(resolved);
        Assert.Equal(md.LastIndexOf("制动电阻", StringComparison.Ordinal), one.CharOffset);
    }

    [Fact]
    public void Resolve_页内找不到时硬失败并报出qid与needle()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => AnchorResolver.Resolve(Markdown, [Question("q07", new Anchor(2, "不存在的串"))]));

        Assert.Contains("q07", ex.Message);
        Assert.Contains("不存在的串", ex.Message);
    }

    /// <summary>
    /// 页内出现两次 → 判定可能命中错块，得到的召回率是假的。宁可崩溃。
    /// </summary>
    [Fact]
    public void Resolve_页内不唯一时硬失败()
    {
        const string md =
            """
            <!-- page:1 label:1 -->
            制动电阻的选型
            制动电阻的接线
            """;

        var ex = Assert.Throws<InvalidDataException>(
            () => AnchorResolver.Resolve(md, [Question("q09", new Anchor(1, "制动电阻"))]));

        Assert.Contains("q09", ex.Message);

        // 断言完整短语而不是光断言 "2"：错误消息里还有「1 处锚点无法解析」「p.1」这些数字，
        // 只匹配一个孤立字符的断言会在消息措辞变化时假通过。
        Assert.Contains("出现 2 次", ex.Message);
    }

    [Fact]
    public void Resolve_页码不存在时硬失败()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => AnchorResolver.Resolve(Markdown, [Question("q11", new Anchor(99, "任意"))]));

        Assert.Contains("q11", ex.Message);
        Assert.Contains("99", ex.Message);
    }

    /// <summary>
    /// 一次报全部错误，不是报第一个就退出 —— 修题时一轮就能改完。
    /// </summary>
    [Fact]
    public void Resolve_多处错误一次全部报出()
    {
        var ex = Assert.Throws<InvalidDataException>(() => AnchorResolver.Resolve(
            Markdown,
            [
                Question("q01", new Anchor(2, "没有这个")),
                Question("q02", new Anchor(3, "也没有这个")),
            ]));

        Assert.Contains("q01", ex.Message);
        Assert.Contains("q02", ex.Message);
    }

    [Fact]
    public void Resolve_一题多锚点全部解析()
    {
        var resolved = AnchorResolver.Resolve(
            Markdown,
            [Question("q01", new Anchor(3, "散热"), new Anchor(3, "100℃"))]);

        Assert.Equal(2, resolved.Count);
        Assert.All(resolved, r => Assert.Equal("q01", r.Qid));
    }

    /// <summary>
    /// ★ 页标记自身的注释文本不属于该页内容。
    /// <para>
    /// MarkdownChunker 消费掉标记行后直接 continue，从不把它产出成 chunk，
    /// 所以落在标记内部的偏移永远不会被任何 chunk 覆盖 —— 那道题会对所有配置永远判 0 且不报错。
    /// 反向的坑同样真实：标记里含页码本身，needle 是短数字时会被误判成「不唯一」。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("page:2")]
    [InlineData("label:1")]
    [InlineData("<!--")]
    public void Resolve_页标记自身的文本不参与匹配(string needle)
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => AnchorResolver.Resolve(Markdown, [Question("q13", new Anchor(2, needle))]));

        Assert.Contains("找不到", ex.Message);
    }

    /// <summary>
    /// 手写评测集时 needle 漏填是完全可能的。行为要确定（报找不到），不能崩也不能误判成命中。
    /// </summary>
    [Fact]
    public void Resolve_空needle报找不到而不是崩溃()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => AnchorResolver.Resolve(Markdown, [Question("q14", new Anchor(2, ""))]));

        Assert.Contains("q14", ex.Message);
        Assert.Contains("找不到", ex.Message);
    }

    /// <summary>
    /// 两个页标记紧挨着（该页零内容）。修了起点偏移之后这一页的区间长度为 0，
    /// 搜索必须安全退化成「找不到」，不能下标越界。
    /// </summary>
    [Fact]
    public void Resolve_零内容的页安全退化()
    {
        const string md =
            """
            <!-- page:1 label:1 -->
            <!-- page:2 label:2 -->
            正文在第二页。
            """;

        var ex = Assert.Throws<InvalidDataException>(
            () => AnchorResolver.Resolve(md, [Question("q15", new Anchor(1, "正文"))]));

        Assert.Contains("找不到", ex.Message);
    }

    /// <summary>
    /// 全部或没有：只要有一个锚点解析失败，同一次调用里已经解析成功的也一并丢弃。
    /// 部分返回会让调用方拿到一份「看起来正常」的锚点集，而缺的那几道题静默判 0。
    /// </summary>
    [Fact]
    public void Resolve_有一个失败时成功的也不返回()
    {
        var ex = Assert.Throws<InvalidDataException>(() => AnchorResolver.Resolve(
            Markdown,
            [
                Question("q01", new Anchor(2, "0x22010201")),   // 这个本来能解析成功
                Question("q02", new Anchor(2, "没有这个")),
            ]));

        Assert.Contains("q02", ex.Message);
        Assert.DoesNotContain("q01", ex.Message);   // q01 没有错误，但它的结果也不会返回
    }
}
