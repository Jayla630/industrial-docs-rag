using IndustrialDocsRag.Core.Documents;

namespace IndustrialDocsRag.Ingestion;

/// <summary>
/// 块的来源。只负责产出 <see cref="DocumentChunk"/>，不做校验、不做嵌入。
/// Week1 唯一实现是 <see cref="JsonlChunkSource"/>（读手工切好的 jsonl）；
/// Week2 增加从 Markdown 自动切分的实现，产出同一 schema，调用方不变。
/// </summary>
public interface IChunkSource
{
    string Name { get; }

    IAsyncEnumerable<DocumentChunk> ReadAsync(CancellationToken ct = default);
}
