namespace IndustrialDocsRag.Retrieval.Qdrant;

/// <summary>
/// Qdrant 连接与 collection 配置。
/// <para>
/// 这些参数通过构造函数注入解决，<see cref="Core.Retrieval.IChunkIndex"/> 的签名不需要动 ——
/// `plan-week1.md` §5.1 第 6 项把这里标为「W1→W2 最可能返工的接口」，结论是不用返工。
/// </para>
/// </summary>
public sealed record QdrantOptions
{
    public string Host { get; init; } = "localhost";

    /// <summary>gRPC 端口。6333 是 REST，Qdrant.Client 走 6334。</summary>
    public int Port { get; init; } = 6334;

    public string Collection { get; init; } = "sv660n";

    /// <summary>命名向量的键。两路检索各查一个。</summary>
    public const string DenseVectorName = "dense";

    public const string SparseVectorName = "bm25";
}
