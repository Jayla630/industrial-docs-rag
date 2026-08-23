using IndustrialDocsRag.Core.Documents;
using IndustrialDocsRag.Core.Retrieval;

namespace IndustrialDocsRag.Tests;

public class RetrievalSignalsTests
{
    private static RetrievedChunk Hit(string id, string tag = "dense")
        => new(
            new DocumentChunk
            {
                Id = id,
                SourceFile = "sv660n-manual.pdf",
                SectionPath = "第5章 故障处理",
                Text = id,
            },
            1.0,
            tag);

    /// <summary>
    /// 某一路为空时另外两条判据必然成立（空集跟谁交都是空）。
    /// 三条一起报等于同一件事说三遍，是纯噪音 —— 只报根因。
    /// </summary>
    [Fact]
    public void Detect_某路为空时只报SingleSideEmpty()
    {
        var signals = RetrievalSignals.Detect(
            dense: [Hit("a"), Hit("b")],
            sparse: [],
            fused: [Hit("a"), Hit("b")]);

        Assert.Single(signals);
        Assert.Equal(RetrievalSignalKind.SingleSideEmpty, signals[0].Kind);
        Assert.Contains("sparse", signals[0].Message);
    }

    [Fact]
    public void Detect_两路都为空时也只报一条()
    {
        var signals = RetrievalSignals.Detect(dense: [], sparse: [], fused: []);

        Assert.Single(signals);
        Assert.Equal(RetrievalSignalKind.SingleSideEmpty, signals[0].Kind);
    }

    [Fact]
    public void Detect_某路有候选却无一进TopK时报FusedFromOneSideOnly()
    {
        // 两路都非空、且有交集（避免混入 NoCandidateOverlap），但 fused 全来自 dense
        var signals = RetrievalSignals.Detect(
            dense: [Hit("a"), Hit("shared")],
            sparse: [Hit("shared", "sparse"), Hit("z", "sparse")],
            fused: [Hit("a")]);

        var kinds = signals.Select(s => s.Kind).ToList();
        Assert.Contains(RetrievalSignalKind.FusedFromOneSideOnly, kinds);
        Assert.DoesNotContain(RetrievalSignalKind.NoCandidateOverlap, kinds);
    }

    [Fact]
    public void Detect_两路零交集时报NoCandidateOverlap()
    {
        var signals = RetrievalSignals.Detect(
            dense: [Hit("a"), Hit("b")],
            sparse: [Hit("y", "sparse"), Hit("z", "sparse")],
            fused: [Hit("a"), Hit("y", "sparse")]);

        Assert.Contains(RetrievalSignalKind.NoCandidateOverlap, signals.Select(s => s.Kind));
    }

    [Fact]
    public void Detect_两路有交集且各有条目进TopK时无判据()
    {
        var signals = RetrievalSignals.Detect(
            dense: [Hit("a"), Hit("shared")],
            sparse: [Hit("shared", "sparse"), Hit("z", "sparse")],
            fused: [Hit("a"), Hit("z", "sparse")]);

        Assert.Empty(signals);
    }

    /// <summary>
    /// 判据只认 chunk id。RetrieverTag 的规则（两路都有打融合名、单路独有留原 tag）
    /// 是 FusionMerge 的实现细节，判据依赖它的话，改 tag 规则会静默改掉判据语义。
    /// </summary>
    [Fact]
    public void Detect_结果不随RetrieverTag改变()
    {
        var withTags = RetrievalSignals.Detect(
            dense: [Hit("a", "dense"), Hit("shared", "dense")],
            sparse: [Hit("shared", "sparse"), Hit("z", "sparse")],
            fused: [Hit("a", "rrf"), Hit("z", "rrf")]);

        var allSameTag = RetrievalSignals.Detect(
            dense: [Hit("a", "x"), Hit("shared", "x")],
            sparse: [Hit("shared", "x"), Hit("z", "x")],
            fused: [Hit("a", "x"), Hit("z", "x")]);

        Assert.Equal(
            withTags.Select(s => s.Kind),
            allSameTag.Select(s => s.Kind));
    }
}
