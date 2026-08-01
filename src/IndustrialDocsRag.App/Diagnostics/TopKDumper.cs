using System.Text.Json;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Core.Serialization;

namespace IndustrialDocsRag.App.Diagnostics;

/// <summary>
/// 把每次检索的 Top-K 追加写成 jsonl，供 Week3 的 pooling 标注使用。
/// <para>
/// 用法：多组配置（dense / sparse / hybrid、开关规范化…）各跑一遍同一份题目，
/// 把各组的 Top-K 取<b>并集</b>做候选池，人工只判"相关 / 不相关"。
/// 这是 IR 领域的标准做法（TREC 几十年如此），比从全库找答案快 5 倍以上，
/// 把 25 题的标注时间从 3~4 小时压到 1 小时左右。
/// </para>
/// <para>
/// <b>已知局限</b>：池外的相关块会漏标，导致召回率被系统性高估。
/// 这一条必须写进 README 的 Limitations —— 主动写出方法论局限比藏着更能说明问题。
/// </para>
/// </summary>
public static class TopKDumper
{
    public static async Task AppendAsync(
        string path,
        string query,
        string retrieverName,
        IReadOnlyList<RetrievedChunk> hits,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var qid = QuestionId(query);
        var lines = hits.Select((h, i) => JsonSerializer.Serialize(new TopKRecord(
            qid,
            query,
            retrieverName,
            i + 1,
            h.Chunk.Id,
            h.Chunk.PageNumber,
            h.Chunk.Metadata.GetValueOrDefault("pageLabel"),
            h.Chunk.SectionPath,
            h.Chunk.Kind.ToString(),
            Math.Round(h.Score, 6),
            h.RetrieverTag), JsonDefaults.Options));

        await File.AppendAllLinesAsync(path, lines, ct);
    }

    /// <summary>
    /// 题目 id。Week3 的评测集会自带 qid，这里先从问题文本派生一个稳定值，
    /// 使同一道题在不同配置下跑出来的记录能被归到一组。
    /// </summary>
    private static string QuestionId(string query)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(query.Trim()));
        return "q" + Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private sealed record TopKRecord(
        string Qid,
        string Query,
        string Retriever,
        int Rank,
        string ChunkId,
        int? Page,
        string? PageLabel,
        string Section,
        string Kind,
        double Score,
        string Tag);
}
