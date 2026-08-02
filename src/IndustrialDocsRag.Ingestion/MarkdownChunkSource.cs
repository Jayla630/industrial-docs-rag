using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IndustrialDocsRag.Core.Documents;

namespace IndustrialDocsRag.Ingestion;

/// <summary>
/// 从 <c>tools/pdf_to_md.py</c> 产出的 Markdown 自动切块。Week2 的默认来源，
/// 与 Week1 手工切好的 <see cref="JsonlChunkSource"/> 产出同一 schema，下游不变。
/// </summary>
public sealed partial class MarkdownChunkSource(string path, string docId, int targetChars = 500) : IChunkSource
{
    /// <summary>
    /// 文件头的来源声明：<c>&lt;!-- source: sv660n-manual.pdf --&gt;</c>。
    /// <para>
    /// 收尾必须写成 <c>--&gt;\s*$</c> 而不是 <c>--&gt;$</c>：真实的 <c>.md</c> 是 CRLF 换行，
    /// 多行模式下 <c>$</c> 匹配在 <c>\n</c> 之前，<c>\r</c> 还留在行尾，
    /// 紧贴的锚点会匹配失败。而单测用的原始字符串字面量是 LF —— 测试全绿、线上失效。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^<!--\s*source:\s*(.+?)\s*-->\s*$", RegexOptions.Multiline)]
    private static partial Regex SourceDeclarationPattern { get; }

    public string Name => $"markdown:{Path.GetFileName(path)}";

    public async IAsyncEnumerable<DocumentChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到 Markdown：{path}。先用 tools/pdf_to_md.py 从 PDF 生成。", path);
        }

        var markdown = await File.ReadAllTextAsync(path, ct);

        foreach (var chunk in new MarkdownChunker(targetChars).Split(markdown, docId, ResolveSourceFile(markdown)))
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    /// <summary>
    /// 引用要指向手册 PDF —— Week4 的阅读器跳页认的是 PDF，<c>.md</c> 只是中间产物。
    /// 文件头没声明来源时退回 Markdown 自己的文件名。
    /// </summary>
    private string ResolveSourceFile(string markdown)
        => SourceDeclarationPattern.Match(markdown) is { Success: true } m
            ? m.Groups[1].Value
            : Path.GetFileName(path);
}
