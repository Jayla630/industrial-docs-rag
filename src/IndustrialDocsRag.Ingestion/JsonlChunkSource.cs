using System.Runtime.CompilerServices;
using System.Text.Json;
using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.Ingestion;

/// <summary>
/// 从 JSON Lines 文件读取手工切好的块，一行一个 <see cref="DocumentChunk"/>。
/// 允许空行和以 <c>//</c> 开头的整行注释 —— 这个文件是人手写的，注释能省很多事。
/// </summary>
public sealed class JsonlChunkSource(string path) : IChunkSource
{
    public string Name => $"jsonl:{Path.GetFileName(path)}";

    public async IAsyncEnumerable<DocumentChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到块文件：{path}", path);
        }

        var lineNo = 0;

        await foreach (var raw in File.ReadLinesAsync(path, ct))
        {
            lineNo++;
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            DocumentChunk chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<DocumentChunk>(line, JsonDefaults.Options)
                        ?? throw new JsonException("反序列化结果为 null");
            }
            catch (JsonException ex)
            {
                // 带上行号，否则在一个几十行的手写文件里找错位置很痛苦
                throw new InvalidDataException($"{path} 第 {lineNo} 行解析失败：{ex.Message}", ex);
            }

            yield return chunk;
        }
    }
}
