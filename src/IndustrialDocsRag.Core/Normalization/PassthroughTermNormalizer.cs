namespace IndustrialDocsRag.Core.Normalization;

/// <summary>
/// 不做任何规范化，原样返回。
/// Week1 的占位实现，同时也是 Week3 对比实验里的对照组 —— 它不是临时代码，会一直留着。
/// </summary>
public sealed class PassthroughTermNormalizer : ITermNormalizer
{
    public static PassthroughTermNormalizer Instance { get; } = new();

    public string Name => "passthrough";

    public string Normalize(string text) => text;
}
