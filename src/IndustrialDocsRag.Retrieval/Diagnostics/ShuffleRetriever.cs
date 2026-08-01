using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.InMemory;

namespace IndustrialDocsRag.Retrieval.Diagnostics;

/// <summary>
/// 故意坏掉的检索器：随机返回 K 个块。这是对照组，不是玩具。
/// <para>
/// 它要解决的是本项目最隐蔽的自欺风险：像 <c>0x6041</c>（CiA402 状态字）这类标准协议内容，
/// 生成模型本来就知道答案。即使检索完全失效、召回的全是无关块，控制台照样会打出一个漂亮
/// 且正确的答案，让人误以为链路通了。
/// </para>
/// <para>
/// 用法：<c>idr ask "..." --retriever shuffle</c>。如果答案质量没有明显下降，
/// 说明 RAG 链路根本没起作用，模型是在背知识。
/// </para>
/// <para>
/// 它同时也是"检索层可替换"这个核心主张的第一次真实验证 —— 换一行 DI 注册，其余代码零改动。
/// </para>
/// <para>
/// <b>统计功效的限制</b>：随机抽 K 块时，正确的那块被误打误撞抽中的概率是 K/N。
/// Week1 只有 15 块、TopK=5，概率高达 1/3 —— 单跑一次说明不了问题，必须换几个 seed 多跑几轮。
/// Week2 语料上千块之后，这个概率降到 0.5% 以下，对照才真正有说服力。
/// </para>
/// </summary>
public sealed class ShuffleRetriever(InMemoryChunkIndex index, int seed = 20260801) : IRetriever
{
    public string Name => $"shuffle(seed={seed})";

    public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        RetrievalQuery query,
        CancellationToken ct = default)
    {
        // 种子里混入查询文本的稳定哈希：不同问题拿到不同的随机集合，
        // 但同一个问题每次跑的结果一致 —— 对照实验必须可复现，否则没法比较。
        var rng = new Random(unchecked(seed + (int)StableHash.Of(query.Text)));

        var picked = index.Items
            .OrderBy(_ => rng.Next())
            .Take(query.TopK)
            .Select(item => new RetrievedChunk(item.Chunk, 0d, "shuffle"))
            .ToList();

        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(picked);
    }
}
