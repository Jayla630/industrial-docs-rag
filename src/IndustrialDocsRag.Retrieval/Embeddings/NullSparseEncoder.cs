using IndustrialDocsRag.Core.Embeddings;

namespace IndustrialDocsRag.Retrieval.Embeddings;

/// <summary>Week1 的占位实现，恒返回空向量。Week2 换成 BM25。</summary>
public sealed class NullSparseEncoder : ISparseEncoder
{
    public static NullSparseEncoder Instance { get; } = new();

    public string Name => "none";

    public SparseVector EncodeDocument(string text) => SparseVector.Empty;

    public SparseVector EncodeQuery(string text) => SparseVector.Empty;
}
