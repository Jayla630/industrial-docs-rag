namespace IndustrialDocsRag.App.Configuration;

/// <summary>
/// 把配置里的相对路径解析成绝对路径。
/// <para>
/// 基准是仓库根目录（含 <c>IndustrialDocsRag.slnx</c> 的那一级），不是当前工作目录 ——
/// <c>dotnet run --project ...</c> 会把工作目录设成项目目录，此时 <c>data/chunks.jsonl</c>
/// 会指到 <c>src/IndustrialDocsRag.App/data/</c> 去。这是个每次都要重新踩一遍的坑，
/// 一次解决掉。
/// </para>
/// </summary>
public static class RepoPaths
{
    private const string RootMarker = "IndustrialDocsRag.slnx";

    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    public static string Root => LazyRoot.Value;

    public static string Resolve(string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));

    /// <summary>把绝对路径显示成相对仓库根的短路径，纯粹为了控制台好读。</summary>
    public static string ToDisplay(string absolutePath)
    {
        var relative = Path.GetRelativePath(Root, absolutePath);
        return relative.StartsWith("..", StringComparison.Ordinal) ? absolutePath : relative.Replace('\\', '/');
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        // 发布成独立目录后找不到 slnx 是正常的，退回当前工作目录
        return Directory.GetCurrentDirectory();
    }
}
