using System.Diagnostics;
using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Embeddings;
using IndustrialDocsRag.Core.Normalization;
using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Ingestion;

public sealed record IngestionProgress(string Stage, int Done, int Total);

public sealed record IngestionReport(
    int ChunkCount,
    IReadOnlyList<ValidationIssue> Issues,
    string SourceName,
    string EmbeddingServiceName,
    int DenseDimension,
    long ElapsedMs)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
}

/// <summary>
/// 入库流水线：读取 → 校验 → 规范化 → 嵌入 → 写索引。
/// <para>
/// 它不知道索引是内存还是 Qdrant，只见 <see cref="IChunkIndex"/>；也不知道嵌入来自远程还是本地。
/// Week2 换后端时这个类一行不改。
/// </para>
/// </summary>
public sealed class IngestionPipeline(
    IChunkSource source,
    ITermNormalizer normalizer,
    IEmbeddingService embedder,
    IChunkIndex index)
{
    public async Task<IngestionReport> RunAsync(
        IProgress<IngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        progress?.Report(new("读取", 0, 0));
        var chunks = new List<DocumentChunk>();
        await foreach (var c in source.ReadAsync(ct))
        {
            chunks.Add(c);
        }

        progress?.Report(new("校验", chunks.Count, chunks.Count));
        var issues = new ChunkValidator().Validate(chunks);
        if (issues.Any(i => i.Severity == ValidationSeverity.Error))
        {
            // 有 Error 就不入库：带着重复 id 或空文本入库，后面排查成本远高于现在停下来
            return new(chunks.Count, issues, source.Name, embedder.Name, embedder.DenseDimension, sw.ElapsedMilliseconds);
        }

        // 规范化只作用于送去嵌入的文本，落库的 DocumentChunk 保留原文。
        // 引用溯源要展示原文，若把规范化后的文本存下来，用户看到的引用和手册对不上。
        progress?.Report(new("嵌入", 0, chunks.Count));
        var textsToEmbed = chunks.Select(c => normalizer.Normalize(c.Text)).ToList();
        var embeddings = await embedder.EmbedDocumentsAsync(textsToEmbed, ct);

        if (embeddings.Count != chunks.Count)
        {
            throw new InvalidOperationException(
                $"嵌入服务返回 {embeddings.Count} 条，与输入的 {chunks.Count} 条不匹配（{embedder.Name}）");
        }

        progress?.Report(new("入库", 0, chunks.Count));
        var indexed = new List<IndexedChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            indexed.Add(new IndexedChunk(chunks[i], embeddings[i]));
        }

        await index.ResetAsync(ct);
        await index.UpsertAsync(indexed, ct);
        progress?.Report(new("入库", chunks.Count, chunks.Count));

        sw.Stop();
        return new(chunks.Count, issues, source.Name, embedder.Name, embedder.DenseDimension, sw.ElapsedMilliseconds);
    }
}
