namespace CodexQuotaWidget.Executor;

internal static class ExecutorPaths
{
    public static string OnD(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("必须使用 D 盘绝对路径");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase) || full.Length <= 3) throw new InvalidDataException("必须使用 D 盘工作目录");
        NoReparse(full);
        return full;
    }

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static void NoReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("拒绝符号链接或目录联接");
            current = Path.GetDirectoryName(current);
        }
    }

    public static string Source(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("检查点源文件路径无效");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(full, root)) throw new InvalidDataException("检查点源文件超出项目");
        NoReparse(full);
        return full;
    }
}
