namespace IndustrialDocsRag.App.Configuration;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>手工切好的块文件。相对路径按仓库根目录解析，见 <see cref="RepoPaths"/>。</summary>
    public string ChunksPath { get; set; } = "data/chunks.jsonl";

    /// <summary>内存索引的落盘位置。</summary>
    public string IndexPath { get; set; } = "data/index.week1.json";

    public int TopK { get; set; } = 5;

    /// <summary><c>dense</c> 或 <c>shuffle</c>。命令行 <c>--retriever</c> 可覆盖。</summary>
    public string RetrieverKind { get; set; } = "dense";

    public EmbeddingOptions Embedding { get; set; } = new();

    public ChatOptions Chat { get; set; } = new();
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
