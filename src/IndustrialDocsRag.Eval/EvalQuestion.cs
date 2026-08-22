namespace IndustrialDocsRag.Eval;

/// <summary>
/// 一处正确答案在源 Markdown 中的位置。
/// <para>
/// 绑「页码 + 关键串」而不是 chunk id，是整套评测集能活过切分规则变更的原因：
/// Week3 的规范化实验要重新入库，chunk id 全部改变，绑 id 的 qrels 当场作废。
/// 见 docs/plan-week1.md §5.1 第 9 项。
/// </para>
/// </summary>
/// <param name="Page">
/// 物理页码，与 <see cref="IndustrialDocsRag.Core.Documents.DocumentChunk.PageNumber"/> 同一口径
/// （.md 里 &lt;!-- page:N --&gt; 的 N）。
/// </param>
/// <param name="Needle">该页内<b>唯一</b>的关键串。唯一性由 AnchorResolver 硬校验。</param>
public sealed record Anchor(int Page, string Needle);

/// <summary>
/// 评测题的内容类型分层。
/// <para>
/// 做成枚举而不是字符串，理由与 <c>ChunkKind</c> 一样：它是 <c>EvalMetrics</c> 分层聚合唯一的
/// 分组键，而分层召回率表是整个 Week3 的核心产出。手写评测集时把 <c>FaultCode</c> 打错一个字母，
/// 字符串版不会报错，只会让那道题<b>从主表里悄悄消失</b>（渲染时按已知分层查表，对不上的键永不渲染），
/// 同时它所属分层的分母少 1、而总计里它还在——三个数字同时错且没有任何提示。
/// 枚举 + <c>JsonStringEnumConverter</c> 让这种手滑在反序列化阶段就硬失败。
/// </para>
/// </summary>
public enum Stratum
{
    /// <summary>故障码行。稀疏侧应强、稠密侧应弱。</summary>
    FaultCode,

    /// <summary>参数行。两侧都应强。</summary>
    Param,

    /// <summary>对象字典行。稠密侧应强。</summary>
    ObjectDictionary,

    /// <summary>散文段落。稠密侧应强、稀疏侧应弱。</summary>
    Prose,

    /// <summary>记法差异（用户写 0x6041、手册写 6041）。规范化实验的靶子，唯一预期能被修好的分层。</summary>
    Notation,
}

/// <summary>
/// 一道评测题。
/// <para>
/// <see cref="Anchors"/> 是数组而不是单值：一道题可以有多个同样正确的出处，
/// pooling 播种出来的新锚点直接追加进去。这就是本项目的 qrels，只是坐标系换成了 .md 偏移。
/// </para>
/// </summary>
public sealed record EvalQuestion
{
    /// <summary>题号，形如 <c>q01</c>。人工写死，不从文本派生 —— 改了措辞不该换 id。</summary>
    public required string Qid { get; init; }

    /// <summary>
    /// 见 <see cref="IndustrialDocsRag.Eval.Stratum"/>。
    /// n=25 时总分说明不了任何事，分层结果才是硬的。见 docs/plan-week3.md §2.2。
    /// </summary>
    /// <remarks>
    /// 属性名和类型名撞在一起是刻意的（跟 <c>DocumentChunk.Kind</c> / <c>ChunkKind</c> 不同名不一样），
    /// 上面 cref 特意写全限定名 —— 裸写 <c>Stratum</c> 在这个上下文里会先解析到本属性自己。
    /// </remarks>
    public required Stratum Stratum { get; init; }

    public required string Query { get; init; }

    /// <remarks>
    /// 集合属性让本 record 自动生成的 <c>Equals</c> 退化成引用比较
    /// （<c>EqualityComparer&lt;IReadOnlyList&lt;Anchor&gt;&gt;.Default</c> 对反序列化出的
    /// <c>List&lt;T&gt;</c> 比引用），存盘再读回来内容一样却判不相等。
    /// <para>
    /// <b>不要为此手写 <c>Equals</c>/<c>GetHashCode</c> 补救。</b>生产侧没有任何地方比较这些
    /// record，为一句测试断言扩大生产接口不划算；更要命的是以后加字段忘了同步 <c>Equals</c>，
    /// 会得到静默错误的相等判定 —— 正是本项目从 Week1 起一直在防的那类「不报错、只是判错」的失效。
    /// </para>
    /// <para>
    /// 约定：<b>带集合属性的 record，测试一律断言成员，不断言整个 record。</b>
    /// 同样适用于 <c>ConfigSummary</c> 与 <c>PairwiseDiff</c>。
    /// </para>
    /// </remarks>
    public required IReadOnlyList<Anchor> Anchors { get; init; }
}

/// <summary>解析到具体字符偏移之后的锚点。</summary>
public sealed record ResolvedAnchor(string Qid, int Page, string Needle, int CharOffset);
