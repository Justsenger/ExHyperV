namespace ExHyperV.Mcp.Tests;

internal static class TestStorage
{
    internal static string CreatePath() => Path.Combine(Path.GetTempPath(), "ExHyperV.Mcp.Tests", Guid.NewGuid().ToString("N"));
    internal static void Delete(string directory)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExHyperV.Mcp.Tests")) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("测试清理路径超出本轮临时目录。");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
