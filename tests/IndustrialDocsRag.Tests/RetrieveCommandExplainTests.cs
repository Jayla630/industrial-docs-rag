using IndustrialDocsRag.App.Commands;
using IndustrialDocsRag.Core.Retrieval;
using IndustrialDocsRag.Retrieval.Fusion;

namespace IndustrialDocsRag.Tests;

public class RetrieveCommandExplainTests
{
    private sealed class StubRetriever(string name) : IRetriever
    {
        public string Name => name;

        public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
            RetrievalQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
    }

    /// <summary>
    /// 静默打一张空轨迹表，看起来像「这次检索确实没有轨迹」，
    /// 又是一个「看起来正常」—— 必须明确报错。
    /// </summary>
    [Fact]
    public void ExplainNotSupported_单路检索器返回错误信息()
    {
        var message = RetrieveCommand.ExplainNotSupported(new StubRetriever("dense:bge-m3"));

        Assert.NotNull(message);
        Assert.Contains("dense:bge-m3", message);
        Assert.Contains("hybrid", message);
    }

    [Fact]
    public void ExplainNotSupported_混合检索器返回null()
    {
        var hybrid = new HybridRetriever(
            new StubRetriever("dense"),
            new StubRetriever("sparse"),
            new RrfFusion());

        Assert.Null(RetrieveCommand.ExplainNotSupported(hybrid));
    }

    /// <summary>
    /// 候选数大于 TopK：常规情况，融合前候选池比融合后的 Top-K 宽。
    /// </summary>
    [Fact]
    public void PoolLabel_候选数大于TopK_文案不自相矛盾()
    {
        var label = RetrieveCommand.PoolLabel("稠密侧候选", count: 15, topK: 5);

        Assert.Contains("15", label);
        Assert.Contains("Top-5", label);
    }

    [Fact]
    public void PoolLabel_候选数等于TopK_文案不自相矛盾()
    {
        var label = RetrieveCommand.PoolLabel("稠密侧候选", count: 5, topK: 5);

        Assert.Contains("5", label);
        Assert.Contains("Top-5", label);
    }

    /// <summary>
    /// 候选数小于 TopK：BM25 只在词有交集时才有分，稀疏侧命中数常年不够 TopK × 倍数。
    /// 这次审查实测就撞上了 —— "3 条截不成 5 条"，旧文案在这里会自相矛盾。
    /// </summary>
    [Fact]
    public void PoolLabel_候选数小于TopK_文案不自相矛盾()
    {
        var label = RetrieveCommand.PoolLabel("稀疏侧候选", count: 3, topK: 5);

        Assert.Contains("3", label);
        Assert.Contains("Top-5", label);
        Assert.DoesNotContain("×", label);
    }
}
