namespace IndustrialDocsRag.Core.Embeddings;

/// <summary>
/// 稀疏向量，只存非零分量。
/// <para>
/// 这个形状对两种来源都成立：BGE-M3 输出的 lexical weights（<see cref="Indices"/> 是 tokenizer 的
/// token id），以及自建 BM25（<see cref="Indices"/> 是自建词表 id）。两者 id 空间不同但结构一致，
/// Qdrant 也不关心 id 的语义。
/// </para>
/// <para>
/// 但要记住：正因为 id 空间不同，同一个 Qdrant collection 不能混用两种来源。
/// Week3 做「BGE-M3 稀疏 vs 自建 BM25」对比实验时，得建两个 collection。
/// </para>
/// </summary>
/// <remarks>
/// 注意这是 record struct 且成员是数组，编译器生成的相等性比较对数组走的是引用相等。
/// 这里不需要值相等语义，若将来需要请显式重写 Equals。
/// </remarks>
public readonly record struct SparseVector(uint[] Indices, float[] Values)
{
    /// <summary>空向量。嵌入服务不产出稀疏向量时的占位值。</summary>
    public static SparseVector Empty { get; } = new([], []);

    /// <summary>非零分量个数。</summary>
    public int NnZ => Indices.Length;
}
