using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Ingestion;

namespace IndustrialDocsRag.Tests;

public class MarkdownChunkerTests
{
    private const string Doc = "sv660n";
    private const string SourceFile = "sv660n-manual.pdf";

    private static IReadOnlyList<IndustrialDocsRag.Core.Documents.DocumentChunk> Split(string markdown)
        => new MarkdownChunker().Split(markdown, Doc, SourceFile);

    [Fact]
    public void Split_章节标题栈拼成SectionPath()
    {
        const string md = """
            <!-- page:126 label:125 -->

            ## 第5章 故障处理

            ### 5.1 故障和警告分类

            伺服驱动器的故障和警告按严重程度分为三类，不同类别的复位方式各不相同，请对照下表处理。
            """;

        var chunks = Split(md);

        var chunk = Assert.Single(chunks);
        Assert.Equal("第5章 故障处理 > 5.1 故障和警告分类", chunk.SectionPath);
    }

    /// <summary>
    /// PDF 转换把规格表里的粗体当成了标题，实测有 `### 0.75 kW 220 V 3000 r/min` 这种。
    /// 判据：节号的首段必须等于当前章号。
    /// </summary>
    [Fact]
    public void Split_节号与章号不符的标题不采信()
    {
        var chunks = Split(FalseHeadingMarkdown);

        Assert.All(chunks, c => Assert.Equal("第1章 伺服系统选型 > 1.1 伺服电机与驱动器型号说明", c.SectionPath));
    }

    /// <summary>
    /// §9.6 的教训：判为非标题的行必须交回正文，不能丢弃 —— 当时的事故是用坏数据静默替换好数据。
    /// </summary>
    [Fact]
    public void Split_误检标题的文字并入正文而不是被丢弃()
    {
        var chunks = Split(FalseHeadingMarkdown);

        var allText = string.Join('\n', chunks.Select(c => c.Text));
        Assert.Contains("0.75 kW 220 V 3000 r/min", allText, StringComparison.Ordinal);
        Assert.Contains("2.39 N·m 250 Hz 4.8 A", allText, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_从锚点取物理页码与印刷页码()
    {
        const string md = """
            <!-- page:129 label:128 -->

            ## 第5章 故障处理

            ### 5.2 通信故障和警告代码一览表

            下表列出了伺服驱动器全部故障码及其对应的错误码与辅助码，供上位机诊断时对照使用。
            """;

        var chunk = Assert.Single(Split(md));

        Assert.Equal(129, chunk.PageNumber);
        Assert.Equal("128", chunk.Metadata["pageLabel"]);
    }

    /// <summary>封面等页没有印刷页码，锚点是 <c>label:</c> 空值，不能塞一个空字符串进 Metadata。</summary>
    [Fact]
    public void Split_印刷页码为空时不写pageLabel()
    {
        const string md = """
            <!-- page:1 label: -->

            ## 第1章 伺服系统选型

            ### 1.1 型号说明

            伺服电机的型号由若干字段组合而成，各字段的含义与取值范围见下文说明。
            """;

        var chunk = Assert.Single(Split(md));

        Assert.Equal(1, chunk.PageNumber);
        Assert.False(chunk.Metadata.ContainsKey("pageLabel"));
    }

    [Fact]
    public void Split_表格每个数据行产出一个TableRow块()
    {
        var chunks = Split(FaultTableMarkdown);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.TableRow, c.Kind));
    }

    /// <summary>
    /// 表格行必须自足：脱离原表后仍能被读懂，所以要带上章节、表头字段名和值。
    /// </summary>
    [Fact]
    public void Split_表格行文本带章节与表头字段()
    {
        var row = Split(FaultTableMarkdown)[2];

        Assert.Contains("第5章 故障处理 > 5.2 通信故障和警告代码一览表", row.Text, StringComparison.Ordinal);
        Assert.Contains("显示: E201.2", row.Text, StringComparison.Ordinal);
        Assert.Contains("辅助码（203Fh）: 0x22010201", row.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// E201.1 / E201.2 那几行的「故障」列与上方 E201 合并，原文是空的。
    /// 不前向填充，这一行离开原表就丢了 E201 这个最关键的检索词。
    /// </summary>
    [Fact]
    public void Split_合并单元格前向填充()
    {
        var row = Split(FaultTableMarkdown)[2];

        Assert.Contains("故障: E201", row.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ 前向填充只作用于行首连续的空列（合并单元格的实际形状）。
    /// 中间列为空就照抄上一行，等于凭空给这一行造了个值 —— 那正是 §9.6 那类事故。
    /// </summary>
    [Fact]
    public void Split_中间列为空时不前向填充()
    {
        const string md = """
            <!-- page:204 label:203 -->

            ## 第6章 对象组一览表

            ### 6.1 对象组一览表

            | 索引 | 子索引 | 名称 | 数据类型 | 单位 |
            | --- | --- | --- | --- | --- |
            | 6040 | 00 | 控制字 | UINT16 | ms |
            | 6041 | 00 | 状态字 |  |  |
            """;

        var row = Split(md)[1];

        Assert.Contains("名称: 状态字", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("UINT16", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("单位", row.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表头跨行断字的残留：`错误码 （603Fh）`。只在中日韩字符之间去空白，
    /// ASCII 之间的空格要保留，否则 `0~0xFFFF`、`UINT16` 会被破坏。
    /// </summary>
    [Fact]
    public void Split_表头中日韩字符间的空白被去掉()
    {
        var row = Split(FaultTableMarkdown)[0];

        Assert.Contains("错误码（603Fh）:", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("错误码 （603Fh）", row.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Week3 的评测集把 ground truth 绑在「页码 + 关键字符串」上，判命中靠 CharOffset 区间覆盖锚点。
    /// 偏移是估算的，整套评测就是废的。
    /// </summary>
    [Fact]
    public void Split_CharOffset指向块在源Markdown中的起始位置()
    {
        var row = Split(FaultTableMarkdown)[2];

        Assert.StartsWith(
            "|  | E201.2 | V 相过流 |",
            FaultTableMarkdown[row.CharOffset..],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// TableRow 的 Text 是拼装出来的（加了章节和表头字段名），比源文本长得多，
    /// 所以覆盖区间不能用 Text.Length 算 —— 那会一路盖到后面几行去，Week3 判命中会误判。
    /// </summary>
    [Fact]
    public void Split_SourceLength是源文本长度而非拼装后长度()
    {
        var row = Split(FaultTableMarkdown)[2];

        var span = FaultTableMarkdown.Substring(row.CharOffset, row.SourceLength);

        Assert.Equal("|  | E201.2 | V 相过流 | NO.1 | 否 | 轴故障 | 0x2312 | 0x22010201 |", span);
        Assert.True(row.SourceLength < row.Text.Length, "拼装后的文本应当比源行长");
    }

    [Fact]
    public void Split_正文按目标长度切成多块()
    {
        var chunks = SplitProse(targetChars: 60);

        Assert.True(chunks.Count >= 3, $"应当切成多块，实际 {chunks.Count} 块");
        Assert.All(chunks, c => Assert.Equal(ChunkKind.Text, c.Kind));
    }

    /// <summary>
    /// 块间重叠一个段落：事实横跨切分点时，至少有一个块完整包含它。
    /// </summary>
    [Fact]
    public void Split_相邻正文块重叠一个段落()
    {
        var chunks = SplitProse(targetChars: 60);

        for (var i = 1; i < chunks.Count; i++)
        {
            var firstParagraph = chunks[i].Text.Split('\n')[0];
            Assert.Contains(firstParagraph, chunks[i - 1].Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 正文块不能跨小节 —— 跨了的话 SectionPath 只能取其一，另一半的溯源就是错的。
    /// </summary>
    [Fact]
    public void Split_正文块不跨小节标题()
    {
        const string md = """
            <!-- page:92 label:91 -->

            ## 第4章 调试与运行

            ### 4.1 上电前检查

            上电之前请确认主回路接线正确、编码器电缆已可靠连接、且驱动器与电机的功率等级相匹配。

            ### 4.2 点动试运行

            点动试运行用于确认电机能否正常旋转，操作前请务必确保机械负载已脱开或处于安全状态。
            """;

        var chunks = Split(md);

        Assert.Equal(2, chunks.Count);
        Assert.EndsWith("4.1 上电前检查", chunks[0].SectionPath, StringComparison.Ordinal);
        Assert.EndsWith("4.2 点动试运行", chunks[1].SectionPath, StringComparison.Ordinal);
        Assert.DoesNotContain("点动", chunks[0].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 参数一览表是三行复合表头，PDF 转换只认了第一行，真正的列名（组别 / 索引码 / 参数）
    /// 落到了前两个「数据行」里，未命名的列则被填成 `列2` `列3` 占位符。
    /// 实测全语料 39% 的块（678/1737、73 张表）受此影响。
    /// </summary>
    [Fact]
    public void Split_复合表头的续行并入表头而不是当成数据行()
    {
        var chunks = Split(ParameterTableMarkdown);

        Assert.Equal(2, chunks.Count);
        Assert.DoesNotContain(chunks, c => c.Text.Contains("十六进制", StringComparison.Ordinal));
    }

    [Fact]
    public void Split_复合表头续行提供的列名取代占位符()
    {
        var row = Split(ParameterTableMarkdown)[0];

        Assert.Contains("索引码: 09h", row.Text, StringComparison.Ordinal);
        Assert.Contains("参数: H06-08", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("列2", row.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("列3", row.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反向守卫：表头完整时不能把第一个数据行吃掉当表头。
    /// </summary>
    [Fact]
    public void Split_表头完整时不吞掉数据行()
    {
        var chunks = Split(FaultTableMarkdown);

        Assert.Equal(3, chunks.Count);
        Assert.Contains(chunks, c => c.Text.Contains("E201.0", StringComparison.Ordinal));
    }

    private const string ParameterTableMarkdown = """
        <!-- page:184 label:183 -->

        ## 第6章 对象组一览表

        | 参数组 | 列2 | 列3 | 名称 | 设定范围 | 出厂值 | 生效方式 |
        | --- | --- | --- | --- | --- | --- | --- |
        | 十六进制 |  | 十进制 |  |  |  |  |
        | 组别 | 索引码 | 参数 |  |  |  |  |
        | 2006 | 09h | H06-08 | 速度正向限制 | 0~6000 | 6000 | 立即生效 |
        |  | 0Ah | H06-09 | 速度反向限制 | 0~6000 | 6000 | 立即生效 |
        """;

    /// <summary>
    /// 目录页是纯检索噪声：它把全书每个标题重复一遍，query 打什么都能蹭上，
    /// 却没有任何实质内容。判据是「一长串点号 + 页码」，正文里不会出现这种形状。
    /// </summary>
    [Fact]
    public void Split_目录行被丢弃()
    {
        const string md = """
            <!-- page:4 label:3 -->

            3.2.1 主电路端子介绍..........................................................................................46
            3.2.2 制动电阻接线举例......................................................................................50
            4.4.3 制动设置................................................................................................... 101

            <!-- page:11 label:10 -->

            ## 第1章 伺服系统选型

            ### 1.1 型号说明

            伺服电机的型号由若干字段组合而成，各字段的含义与取值范围见下文说明。
            """;

        var chunk = Assert.Single(Split(md));

        Assert.DoesNotContain("主电路端子介绍", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("制动设置", chunk.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 手册正文前是封面、前言和安全注意事项。安全注意事项是有价值的内容
    /// （「切断电源后请至少等待 15 分钟」这类），不能因为它不属于任何章节就丢掉；
    /// 但 SectionPath 又不能为空 —— 那是 ChunkValidator 的硬错误。
    /// </summary>
    [Fact]
    public void Split_章节之前的内容归入前置部分而不是被丢弃()
    {
        const string md = """
            <!-- page:8 label:7 -->

            接线前，请切断所有设备的电源。切断电源后设备内部电容有残余电压，请至少等待15 分钟再进行接线等操作。

            <!-- page:11 label:10 -->

            ## 第1章 伺服系统选型

            ### 1.1 型号说明

            伺服电机的型号由若干字段组合而成，各字段的含义与取值范围见下文说明。
            """;

        var chunks = Split(md);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("手册前置部分", chunks[0].SectionPath);
        Assert.Contains("等待15 分钟", chunks[0].Text, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DocumentChunk> SplitProse(int targetChars)
    {
        const string md = """
            <!-- page:126 label:125 -->

            ## 第5章 故障处理

            ### 5.1 故障和警告分类

            段落甲：伺服驱动器的故障按严重程度分为一类故障和二类故障，两者的停机方式并不相同。

            段落乙：一类故障发生时驱动器立即封锁输出，电机自由停车，此类故障通常不可复位。

            段落丙：二类故障发生时驱动器按设定的减速时间停机，故障原因排除后可通过面板复位。

            段落丁：警告不会导致停机，但会通过面板与上位机提示，长期忽略可能演变为故障。
            """;

        return new MarkdownChunker(targetChars).Split(md, Doc, SourceFile);
    }

    private const string FaultTableMarkdown = """
        <!-- page:129 label:128 -->

        ## 第5章 故障处理

        ### 5.2 通信故障和警告代码一览表

        | 故障 | 显示 | 故障名称 | 故障类型 | 能否复位 | 故障范围 | 错误码 （603Fh） | 辅助码 （203Fh） |
        | --- | --- | --- | --- | --- | --- | --- | --- |
        | E201 | E201.0 | P 相过流 | NO.1 | 否 | 整机故障 | 0x2312 | 0x02010201 |
        |  | E201.1 | U 相过流 | NO.1 | 否 | 轴故障 | 0x2312 | 0x12010201 |
        |  | E201.2 | V 相过流 | NO.1 | 否 | 轴故障 | 0x2312 | 0x22010201 |
        """;

    private const string FalseHeadingMarkdown = """
        <!-- page:11 label:10 -->

        ## 第1章 伺服系统选型

        ### 1.1 伺服电机与驱动器型号说明

        伺服电机的型号由若干字段组合而成，各字段的含义与取值范围见下文说明。

        ### 0.75 kW 220 V 3000 r/min

        ### 2.39 N·m 250 Hz 4.8 A
        """;
}
