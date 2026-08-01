namespace IndustrialDocsRag.Core.Retrieval;

/// <summary>
/// 索引的写入侧。
/// <para>
/// 这里刻意不提供「按向量查询」的方法 —— 读取一律走 <see cref="IRetriever"/>。
/// 原因是不同后端的查询能力差异太大（内存实现是全量扫描，Qdrant 是 HNSW + 稀疏倒排 + payload 过滤），
/// 硬要在这一层统一，抽象一定会变形成某个后端的形状。
/// 写入侧则相反：「把这批带向量的块存进去」在任何后端都是同一件事。
/// </para>
/// </summary>
public interface IChunkIndex
{
    /// <summary>按 <c>Chunk.Id</c> 插入或更新。</summary>
    Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken ct = default);

    /// <summary>索引是否已存在（Qdrant 下即 collection 是否已创建）。</summary>
    Task<bool> ExistsAsync(CancellationToken ct = default);

    /// <summary>清空并重建。切分规则或规范化策略变更后必须全量重灌。</summary>
    Task ResetAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}
