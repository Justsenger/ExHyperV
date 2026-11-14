namespace ExHyperV.Services;

internal static class VmConfigurationPaths
{
    internal static string Resolve(string file, string? root)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) is "" or "." or "..")
            throw new InvalidDataException("提供者未返回有效的文件路径。");

        if (Path.IsPathFullyQualified(file)) return Path.GetFullPath(file);
        // Never resolve provider paths against the caller's working directory.
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || Path.IsPathRooted(file))
            throw new InvalidDataException("提供者返回的路径无法解析为完整文件路径。");

        return Path.GetFullPath(file, root);
    }
}
