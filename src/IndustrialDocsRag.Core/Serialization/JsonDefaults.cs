using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndustrialDocsRag.Core.Serialization;

/// <summary>
/// 全项目共用的 JSON 设置：<c>chunks.jsonl</c>、索引落盘、Week3 的 topk dump 都用它。
/// <para>
/// 放在 Core 是因为它是跨层的数据契约而非某一层的实现细节。它只依赖 BCL，
/// Core 的零 NuGet 红线不受影响。
/// </para>
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,

        // 中文原样输出，不转义成 \uXXXX。这些文件（chunks.jsonl、评测集）是要人眼审阅和手工编辑的，
        // 转义之后完全没法看。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // 枚举写成字符串。手写 chunks.jsonl 时 "kind": "TableRow" 一眼能懂，
        // 写成 1 则既要查表又会在枚举顺序调整时静默错位。
        Converters = { new JsonStringEnumConverter() },
    };

    public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };
}
