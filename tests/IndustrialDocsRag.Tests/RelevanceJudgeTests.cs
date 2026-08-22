using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Eval;

namespace IndustrialDocsRag.Tests;

/// <summary>
/// 命中判定是把「召回结果」翻译成「对/错」的那一步，整份评测报告的每个数字都从它长出来。
/// 判错了不会崩，只会让召回率整体偏高或偏低 —— 而错的数字比错的代码难发现得多。
/// </summary>
public class RelevanceJudgeTests
{
    private static DocumentChunk Chunk(int charOffset, int sourceLength, string text = "文本") => new()
    {
        Id = "doc#p1#s#000",
        SourceFile = "m.pdf",
        SectionPath = "第1章",
        CharOffset = charOffset,
        SourceLength = sourceLength,
        Text = text,
    };

    private static ResolvedAnchor At(int offset) => new("q01", 1, "needle", offset);

    [Fact]
    public void Covers_锚点落在区间内为命中()
    {
        Assert.True(RelevanceJudge.Covers(Chunk(100, 50), At(120)));
    }

    [Fact]
    public void Covers_区间起点算命中()
    {
        Assert.True(RelevanceJudge.Covers(Chunk(100, 50), At(100)));
    }

    /// <summary>区间是左闭右开：起点 + 长度那一位属于下一块。</summary>
    [Fact]
    public void Covers_区间终点不算命中()
    {
        Assert.False(RelevanceJudge.Covers(Chunk(100, 50), At(150)));
    }

    [Fact]
    public void Covers_锚点在区间之前不算命中()
    {
        Assert.False(RelevanceJudge.Covers(Chunk(100, 50), At(99)));
    }

    /// <summary>
    /// ★ 这条是本类的全部意义：TableRow 的 Text 是拼装出来的（补了章节路径和表头字段名），
    /// 比源行长得多。用 Text.Length 算区间会一路盖到后面几行，
    /// 而表格行占本项目语料的 94% —— 会系统性抬高所有配置的召回率，且不报任何错。
    /// </summary>
    [Fact]
    public void Covers_用SourceLength而不是TextLength()
    {
        // 源行 30 字符，拼装后的 Text 有 200 字符
        var chunk = Chunk(100, 30, new string('x', 200));

        Assert.False(RelevanceJudge.Covers(chunk, At(150)));
    }

    /// <summary>
    /// 长度为 0 的块（理论上不该出现，但 ChunkValidator 只警告不拦截）不能覆盖任何锚点，
    /// 否则它会成为一块「命中一切」的万能砖。
    /// </summary>
    [Fact]
    public void Covers_零长度块不覆盖任何锚点()
    {
        Assert.False(RelevanceJudge.Covers(Chunk(100, 0), At(100)));
    }
}
