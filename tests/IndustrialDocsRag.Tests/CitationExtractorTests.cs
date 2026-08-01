using IndustrialDocsRag.App.Answering;
using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Tests;

public class CitationExtractorTests
{
    [Fact]
    public void 提取答案里的有效引用()
    {
        var hits = Hits("sv660n#p129#5-2#000", "sv660n#p204#6#000");

        var result = CitationExtractor.Extract(
            "E201.2 的辅助码是 0x22010201 [sv660n#p129#5-2#000]。", hits);

        var citation = Assert.Single(result.Citations);
        Assert.Equal("sv660n#p129#5-2#000", citation.ChunkId);
        Assert.Equal(129, citation.PageNumber);
        Assert.Empty(result.RejectedIds);
    }

    /// <summary>
    /// 这是本类存在的理由：模型一定会写出格式合法但不存在的编号。
    /// 不拦下来的话，读者以为每句话都有出处，实际出处指向不存在的块 —— 比没有引用更危险。
    /// </summary>
    [Fact]
    public void 编造的编号被丢弃且单独报出()
    {
        var hits = Hits("sv660n#p129#5-2#000");

        var result = CitationExtractor.Extract(
            "甲 [sv660n#p129#5-2#000]。乙 [sv660n#p129#5-2#007]。", hits);

        Assert.Single(result.Citations);
        Assert.Equal("sv660n#p129#5-2#007", Assert.Single(result.RejectedIds));
    }

    [Fact]
    public void 同一编号出现多次只产生一条引用()
    {
        var hits = Hits("sv660n#p129#5-2#000");

        var result = CitationExtractor.Extract(
            "甲 [sv660n#p129#5-2#000]。乙 [sv660n#p129#5-2#000]。丙 [sv660n#p129#5-2#000]。", hits);

        Assert.Single(result.Citations);
    }

    [Fact]
    public void 引用按首次出现顺序排列()
    {
        var hits = Hits("a#p1#1#000", "b#p2#2#000", "c#p3#3#000");

        var result = CitationExtractor.Extract("[c#p3#3#000] [a#p1#1#000] [b#p2#2#000]", hits);

        Assert.Equal(
            ["c#p3#3#000", "a#p1#1#000", "b#p2#2#000"],
            result.Citations.Select(c => c.ChunkId));
    }

    /// <summary>中文模型输出全角括号并不罕见。</summary>
    [Fact]
    public void 认全角方括号()
    {
        var hits = Hits("sv660n#p129#5-2#000");

        var result = CitationExtractor.Extract("辅助码是 0x22010201【sv660n#p129#5-2#000】", hits);

        Assert.Single(result.Citations);
    }

    [Fact]
    public void 一个括号里多个编号能全部拆出()
    {
        var hits = Hits("a#p1#1#000", "b#p2#2#000");

        var result = CitationExtractor.Extract("见 [a#p1#1#000, b#p2#2#000]", hits);

        Assert.Equal(2, result.Citations.Count);
    }

    /// <summary>
    /// 模型偶尔会写 "[1]"、"[表5-2]" 这类普通标注。那不是编造的 chunk id，
    /// 计入幻觉会让这个指标失真。
    /// </summary>
    [Theory]
    [InlineData("参见第一条 [1]。")]
    [InlineData("见 [表5-2]。")]
    [InlineData("如 [注] 所述。")]
    public void 普通方括号标注不计入幻觉(string answer)
    {
        var result = CitationExtractor.Extract(answer, Hits("a#p1#1#000"));

        Assert.Empty(result.Citations);
        Assert.Empty(result.RejectedIds);
    }

    [Fact]
    public void 引用预览会截断过长原文()
    {
        var hits = Hits("a#p1#1#000");

        var result = CitationExtractor.Extract("[a#p1#1#000]", hits);

        var quote = Assert.Single(result.Citations).Quote;
        Assert.True(quote.Length <= 121, $"预览长度 {quote.Length} 超出预期");
        Assert.EndsWith("…", quote, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有任何标记时返回空()
    {
        var result = CitationExtractor.Extract("手册中未找到相关内容。", Hits("a#p1#1#000"));

        Assert.Empty(result.Citations);
        Assert.Empty(result.RejectedIds);
    }

    private static IReadOnlyList<RetrievedChunk> Hits(params string[] ids)
        => ids.Select((id, i) => new RetrievedChunk(
            new DocumentChunk
            {
                Id = id,
                SourceFile = "sv660n-manual.pdf",
                PageNumber = int.TryParse(new string(id.Split('#')[1].Skip(1).ToArray()), out var p) ? p : null,
                SectionPath = "第5章 故障处理 > 5.2 通信故障和警告代码一览表",
                CharOffset = i * 100,
                Kind = ChunkKind.TableRow,
                Text = new string('故', 200),
            },
            0.5 - i * 0.01,
            "dense")).ToList();
}
