using System.Text.Json;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.Eval;

/// <summary>
/// 评测集的读写。一行一题的 jsonl，与 <c>chunks.jsonl</c> 同一套 JSON 设置。
/// <para>
/// 用 jsonl 而不是单个 JSON 数组，是为了让 pooling 播种时能逐行 diff ——
/// 补一个锚点只应该改动一行，评审改动量时一眼能看清。
/// </para>
/// </summary>
public static class EvalQuestionStore
{
    public static async Task<IReadOnlyList<EvalQuestion>> LoadAsync(
        string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到评测集：{path}", path);
        }

        var questions = new List<EvalQuestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;

        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            EvalQuestion question;
            try
            {
                question = JsonSerializer.Deserialize<EvalQuestion>(line, JsonDefaults.Options)
                           ?? throw new InvalidDataException($"{path} 第 {lineNumber} 行解析为空。");
            }
            catch (JsonException ex)
            {
                // JsonException 自带的 LineNumber 是这一行字符串内部的偏移，不是文件里的物理行号，
                // 直接抛出去会把人引到错误的位置。评测集是手写的，打错逗号、漏字段是常态。
                throw new InvalidDataException($"{path} 第 {lineNumber} 行不是合法的题目 JSON：{ex.Message}", ex);
            }

            // qid 重复的后果是两道题的结果互相覆盖，且不会有任何报错
            if (!seen.Add(question.Qid))
            {
                throw new InvalidDataException(
                    $"{path} 第 {lineNumber} 行的 qid `{question.Qid}` 与前面重复。qid 必须唯一。");
            }

            questions.Add(question);
        }

        return questions;
    }

    public static async Task SaveAsync(
        string path, IReadOnlyList<EvalQuestion> questions, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var lines = questions.Select(q => JsonSerializer.Serialize(q, JsonDefaults.Options));
        await File.WriteAllLinesAsync(path, lines, ct);
    }
}
