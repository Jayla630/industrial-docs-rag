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
    long ElapsedMs,
    string SparseEncoderName = "none",
    int SparseNonZeroAverage = 0)
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
    ISparseEncoder sparseEncoder,
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
        var textsToEmbed = chunks.Select(c => normalizer.Normalize(c.Text)).ToList();

        // BM25 是语料级模型：必须先看过全部文档统计 df / N / avgdl，才能编码任何一条。
        // 这正是它与逐条独立的稠密嵌入的本质区别，也是 ISparseEncoderTrainer 存在的理由。
        if (sparseEncoder is ISparseEncoderTrainer trainer)
        {
            progress?.Report(new("训练稀疏模型", 0, chunks.Count));
            trainer.Fit(textsToEmbed);
        }

        progress?.Report(new("嵌入", 0, chunks.Count));
        var embeddings = await embedder.EmbedDocumentsAsync(textsToEmbed, ct);

        if (embeddings.Count != chunks.Count)
        {
            throw new InvalidOperationException(
                $"嵌入服务返回 {embeddings.Count} 条，与输入的 {chunks.Count} 条不匹配（{embedder.Name}）");
        }

        progress?.Report(new("入库", 0, chunks.Count));
        var indexed = new List<IndexedChunk>(chunks.Count);
        var sparseNonZero = 0L;

        for (var i = 0; i < chunks.Count; i++)
        {
            var embedding = embeddings[i];

            // 嵌入服务自带稀疏向量时以它为准（BGE-M3 的 lexical weights）；
            // §9.12 实测硅基流动的端点不返回稀疏字段，所以实际走的是自建 BM25 这条路。
            if (!embedder.ProducesSparse)
            {
                var sparse = sparseEncoder.EncodeDocument(textsToEmbed[i]);
                sparseNonZero += sparse.NnZ;
                embedding = embedding with { Sparse = sparse };
            }
            else
            {
                sparseNonZero += embedding.Sparse?.NnZ ?? 0;
            }

            indexed.Add(new IndexedChunk(chunks[i], embedding));
        }

        await index.ResetAsync(ct);
        await index.UpsertAsync(indexed, ct);
        progress?.Report(new("入库", chunks.Count, chunks.Count));

        sw.Stop();
        return new(
            chunks.Count,
            issues,
            source.Name,
            embedder.Name,
            embedder.DenseDimension,
            sw.ElapsedMilliseconds,
            sparseEncoder.Name,
            chunks.Count == 0 ? 0 : (int)(sparseNonZero / chunks.Count));
    }
}
