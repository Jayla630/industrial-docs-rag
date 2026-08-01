using IndustrialDocsRag.Ingestion;

namespace IndustrialDocsRag.Tests;

public class ChunkIdFactoryTests
{
    [Fact]
    public void Create_从章节路径提取编号做slug()
    {
        var id = ChunkIdFactory.Create("demo-servo", 33, "3 现场总线通信 > 3.3 状态字", 2);

        Assert.Equal("demo-servo#p33#3-3#002", id);
    }

    [Fact]
    public void Create_取最后一级章节而非第一级()
    {
        var id = ChunkIdFactory.Create("m", 1, "7 故障处理 > 7.2 故障复位", 0);

        Assert.Equal("m#p1#7-2#000", id);
    }

    [Fact]
    public void Create_无页码时用px占位()
    {
        var id = ChunkIdFactory.Create("m", null, "1 概述", 5);

        Assert.Equal("m#px#1#005", id);
    }

    [Fact]
    public void Create_章节无编号时退回sec()
    {
        var id = ChunkIdFactory.Create("m", 10, "附录 > 术语表", 1);

        Assert.Equal("m#p10#sec#001", id);
    }

    /// <summary>
    /// id 要由 LLM 在答案里原样复述，再被正则提取出来做引用校验，所以必须是 ASCII。
    /// 中文章节标题不能漏进 id 里。
    /// </summary>
    [Fact]
    public void Create_结果不含非ASCII字符()
    {
        var id = ChunkIdFactory.Create("汇川 SV660N 手册", 42, "7 故障处理 > 7.3 状态字", 3);

        Assert.All(id, ch => Assert.True(char.IsAscii(ch), $"id 中出现非 ASCII 字符：{ch}"));
    }

    [Fact]
    public void Create_docId被规范化为小写连字符形式()
    {
        var id = ChunkIdFactory.Create("Demo_Servo Manual", 1, "1 概述", 0);

        Assert.StartsWith("demo-servo-manual#", id, StringComparison.Ordinal);
    }
}
