namespace IndustrialDocsRag.App.Configuration;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>手工切好的块文件。相对路径按仓库根目录解析，见 <see cref="RepoPaths"/>。</summary>
    public string ChunksPath { get; set; } = "data/chunks.jsonl";

    /// <summary>内存索引的落盘位置。</summary>
    public string IndexPath { get; set; } = "data/index.week1.json";

    public int TopK { get; set; } = 5;

    /// <summary><c>dense</c> / <c>sparse</c> / <c>hybrid</c> / <c>shuffle</c>。命令行 <c>--retriever</c> 可覆盖。</summary>
    public string RetrieverKind { get; set; } = "dense";

    /// <summary>
    /// BM25 模型的落盘位置。这个文件同时是索引清单 ——
    /// 它记录产出稠密向量的嵌入服务名与维度，换到 Qdrant 后由它接管同源校验。
    /// </summary>
    public string SparseModelPath { get; set; } = "data/bm25.week2.json";

    /// <summary>
    /// <c>memory</c> 或 <c>qdrant</c>。命令行 <c>--backend</c> 可覆盖。
    /// 两者的检索器种类完全一样，所以「同一个 retriever、换后端」的对照是一行开关的事。
    /// </summary>
    public string Backend { get; set; } = "memory";

    public QdrantSettings Qdrant { get; set; } = new();

    /// <summary><c>rrf</c> 或 <c>weighted</c>。Week3 的对比实验维度之一。</summary>
    public string FusionKind { get; set; } = "rrf";

    /// <summary>加权融合的稠密侧权重。仅 <c>FusionKind = weighted</c> 时有意义。</summary>
    public double FusionAlpha { get; set; } = 0.5;

    /// <summary>
    /// <c>fixed</c>（固定流水线）或 <c>agent</c>（LLM 自主检索）。命令行 <c>--answerer</c> 可覆盖。
    /// 这个开关同时是 Week3 的一条实验维度。
    /// </summary>
    public string AnswererKind { get; set; } = "fixed";

    public EmbeddingOptions Embedding { get; set; } = new();

    public ChatOptions Chat { get; set; } = new();
}

public sealed class QdrantSettings
{
    public string Host { get; set; } = "localhost";

    /// <summary>gRPC 端口。6333 是 REST，Qdrant.Client 走 6334。</summary>
    public int Port { get; set; } = 6334;

    public string Collection { get; set; } = "sv660n";
}

public sealed class EmbeddingOptions
{
    /// <summary>
    /// <c>fake</c>（默认，不调外部服务）或 <c>remote</c>。
    /// 施工第 7 步之前一直用 fake，把"管道通不通"和"端点格式对不对"分开排查。
    /// </summary>
    public string Provider { get; set; } = "fake";

    public string Endpoint { get; set; } = "https://api.siliconflow.cn/v1";

    public string Model { get; set; } = "BAAI/bge-m3";

    /// <summary>BGE-M3 是 1024，已由官方文档确认。</summary>
    public int DenseDimension { get; set; } = 1024;

    /// <summary>批量嵌入的分批大小。这个值尚未实测，端点接上后按实际响应调整。</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>只从 user-secrets 或环境变量读，绝不写进 appsettings.json。</summary>
    public string? ApiKey { get; set; }
}

public sealed class ChatOptions
{
    public string Endpoint { get; set; } = "https://api.deepseek.com";

    public string Model { get; set; } = "deepseek-chat";

    public string? ApiKey { get; set; }
}
