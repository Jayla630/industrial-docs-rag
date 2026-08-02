using IndustrialDocsRag.Ingestion;

namespace IndustrialDocsRag.Tests;

public class MarkdownChunkSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("idr-md-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteMarkdown(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<List<IndustrialDocsRag.Core.Documents.DocumentChunk>> ReadAllAsync(IChunkSource source)
    {
        var chunks = new List<IndustrialDocsRag.Core.Documents.DocumentChunk>();
        await foreach (var c in source.ReadAsync())
        {
            chunks.Add(c);
        }

        return chunks;
    }

    /// <summary>
    /// 引用要指向手册 PDF —— Week4 的阅读器跳页认的是 PDF。
    /// `.md` 只是中间产物，不该出现在引用里。
    /// </summary>
    [Fact]
    public async Task ReadAsync_SourceFile取文件头声明的原始文件名()
    {
        var path = WriteMarkdown("sv660n-manual.md", """
            <!-- source: sv660n-manual.pdf -->
            <!-- pages: 212 -->

            <!-- page:126 label:125 -->

            ## 第5章 故障处理

            ### 5.1 故障和警告分类

            伺服驱动器的故障按严重程度分为一类故障和二类故障，两者的停机方式并不相同。
            """);

        var chunks = await ReadAllAsync(new MarkdownChunkSource(path, "sv660n"));

        Assert.All(chunks, c => Assert.Equal("sv660n-manual.pdf", c.SourceFile));
    }

    /// <summary>
    /// ★ 真实的 .md 是 CRLF 换行（仓库有 .gitattributes 固化行尾）。
    /// 单测用 C# 原始字符串字面量，行尾是 LF —— 两者不一致，正则里 <c>-->$</c> 这种
    /// 收尾锚点在 LF 下过、在 CRLF 下不过，测试全绿而线上失效。
    /// </summary>
    [Fact]
    public async Task ReadAsync_CRLF换行的文件也能取到source声明()
    {
        var path = WriteMarkdown("crlf-manual.md", string.Join("\r\n",
        [
            "<!-- source: sv660n-manual.pdf -->",
            "<!-- pages: 212 -->",
            "",
            "<!-- page:126 label:125 -->",
            "",
            "## 第5章 故障处理",
            "",
            "### 5.1 故障和警告分类",
            "",
            "伺服驱动器的故障按严重程度分为一类故障和二类故障，两者的停机方式并不相同。",
        ]));

        var chunk = Assert.Single(await ReadAllAsync(new MarkdownChunkSource(path, "sv660n")));

        Assert.Equal("sv660n-manual.pdf", chunk.SourceFile);
        Assert.Equal(126, chunk.PageNumber);
    }

    [Fact]
    public async Task ReadAsync_没有source声明时退回Markdown文件名()
    {
        var path = WriteMarkdown("adhoc.md", """
            <!-- page:1 label: -->

            ## 第1章 概述

            ### 1.1 范围

            本章说明手册的适用范围与读者对象，以及各章节的组织方式和阅读建议。
            """);

        var chunks = await ReadAllAsync(new MarkdownChunkSource(path, "adhoc"));

        Assert.All(chunks, c => Assert.Equal("adhoc.md", c.SourceFile));
    }

    /// <summary>
    /// 文件头的 <c>&lt;!-- pages: 212 --&gt;</c> 长得很像页码锚点，不能被当成第 212 页。
    /// </summary>
    [Fact]
    public async Task ReadAsync_文件头的pages声明不被当成页码锚点()
    {
        var path = WriteMarkdown("sv660n-manual.md", """
            <!-- source: sv660n-manual.pdf -->
            <!-- pages: 212 -->

            <!-- page:126 label:125 -->

            ## 第5章 故障处理

            ### 5.1 故障和警告分类

            伺服驱动器的故障按严重程度分为一类故障和二类故障，两者的停机方式并不相同。
            """);

        var chunk = Assert.Single(await ReadAllAsync(new MarkdownChunkSource(path, "sv660n")));

        Assert.Equal(126, chunk.PageNumber);
    }
}
