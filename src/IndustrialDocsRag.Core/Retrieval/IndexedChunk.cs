using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Embeddings;

namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>已完成嵌入、准备写入索引的块。</summary>
public sealed record IndexedChunk(DocumentChunk Chunk, EmbeddingResult Embedding);
