namespace IndustrialDocsRag.Retrieval;

/// <summary>
/// FNV-1a 32 位哈希。
/// <para>
/// 不用 <see cref="string.GetHashCode()"/>：.NET 的字符串哈希每个进程都会随机化种子，
/// 同一段文本在两次运行里得到的哈希不同。落盘的索引文件必须跨进程可复现，否则今天灌的库
/// 明天查出来就是另一回事，而且这种失效是静默的。
/// </para>
/// </summary>
internal static class StableHash
{
    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    public static uint Of(ReadOnlySpan<char> text)
    {
        var hash = OffsetBasis;

        foreach (var ch in text)
        {
            hash ^= (byte)(ch & 0xFF);
            hash *= Prime;
            hash ^= (byte)(ch >> 8);
            hash *= Prime;
        }

        return hash;
    }
}
