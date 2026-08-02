using System.Security.Cryptography;
using Google.Protobuf.Collections;
using IndustrialDocsRag.Core.Documents;
using Qdrant.Client.Grpc;

namespace IndustrialDocsRag.Retrieval.Qdrant;

/// <summary>
/// <see cref="DocumentChunk"/> 与 Qdrant payload 之间的映射。
/// <para>
/// 块的全部字段都进 payload，检索时直接还原 —— 不需要另一个存储放正文，
/// 也就不会出现「向量库和正文库不同步」这类问题。
/// </para>
/// </summary>
internal static class QdrantPayload
{
    private const string ChunkId = "chunkId";
    private const string SourceFile = "sourceFile";
    private const string PageNumber = "pageNumber";
    private const string PageLabel = "pageLabel";
    private const string SectionPath = "sectionPath";
    private const string CharOffset = "charOffset";
    private const string SourceLength = "sourceLength";
    private const string Kind = "kind";
    private const string Text = "text";

    /// <summary>
    /// Qdrant 的点 id 只收 uint64 或 UUID，而我们的 chunk id 是
    /// <c>sv660n#p129#5-2#842</c> 这种可读字符串。
    /// <para>
    /// 于是从 chunk id 派生一个确定性 UUID（MD5，按 RFC 4122 v3 置版本位），
    /// 原 id 照旧存进 payload。确定性是必须的：重跑 ingest 要能覆盖同一个点，
    /// 而不是每次都插入一批新点。这里 MD5 只做 id 派生，不涉及任何安全用途。
    /// </para>
    /// </summary>
    public static Guid PointId(string chunkId)
    {
        var hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(chunkId));

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);   // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // RFC 4122 variant

        return new Guid(hash);
    }

    public static void Fill(MapField<string, Value> payload, DocumentChunk chunk)
    {
        payload[ChunkId] = chunk.Id;
        payload[SourceFile] = chunk.SourceFile;
        payload[SectionPath] = chunk.SectionPath;
        payload[CharOffset] = chunk.CharOffset;
        payload[SourceLength] = chunk.SourceLength;
        payload[Kind] = chunk.Kind.ToString();
        payload[Text] = chunk.Text;

        if (chunk.PageNumber is { } page)
        {
            payload[PageNumber] = page;
        }

        if (chunk.Metadata.TryGetValue("pageLabel", out var label))
        {
            payload[PageLabel] = label;
        }
    }

    public static DocumentChunk ToChunk(MapField<string, Value> payload)
    {
        var metadata = new Dictionary<string, string>();
        if (payload.TryGetValue(PageLabel, out var label))
        {
            metadata["pageLabel"] = label.StringValue;
        }

        return new DocumentChunk
        {
            Id = payload[ChunkId].StringValue,
            SourceFile = payload[SourceFile].StringValue,
            PageNumber = payload.TryGetValue(PageNumber, out var page) ? (int)page.IntegerValue : null,
            SectionPath = payload[SectionPath].StringValue,
            CharOffset = (int)payload[CharOffset].IntegerValue,
            SourceLength = (int)payload[SourceLength].IntegerValue,
            Kind = Enum.Parse<ChunkKind>(payload[Kind].StringValue),
            Text = payload[Text].StringValue,
            Metadata = metadata,
        };
    }
}
