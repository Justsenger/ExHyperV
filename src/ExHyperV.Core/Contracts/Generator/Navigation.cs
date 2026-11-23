internal static partial class ApiDocument
{
    private static readonly (string? Directory, string[] Path)[] NavigationCatalog =
        Catalog.Categories.Select(path => ((string?)null, path)).ToArray();
    private static string[][] NavigationPaths(TypeEntry type)
    {
        var paths = DeclaredNavigationPaths(type).Concat(type.Members.SelectMany(member => MemberNavigationPaths(type, member)))
            .DistinctBy(NavigationKey, StringComparer.Ordinal).ToArray();
        var catalog = NavigationCatalog.Select(entry => entry.Path).DistinctBy(NavigationKey, StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
            if (!catalog.Any(item => NavigationKey(item) == NavigationKey(path)))
                throw new InvalidOperationException($"{type.Name} 的功能归属未列入目录：{NavigationKey(path)}。");
        return catalog
            .Where(path => paths.Any(item => NavigationKey(item) == NavigationKey(path))).ToArray();
    }

    private static string NavigationKey(string[] path) => string.Join(" / ", path);

    // Reading and writing share a documentation page; their source assignments retain their roles.
    private static string[] DocumentationPath(string[] path) => path.Length >= 3
        && path[0] == "管理层面" && path[1] is "虚拟机" or "宿主" && path[2] is "查询" or "修改"
        ? [.. path.Take(2), .. path.Skip(3)] : path;

    private static IEnumerable<string[]> DocumentationCategoriesFor(ScanResult scan) =>
        CategoriesFor(scan).Select(DocumentationPath).DistinctBy(NavigationKey, StringComparer.Ordinal);

    private static string[][] CategoriesFor(ScanResult scan)
    {
        var keys = scan.Types.SelectMany(NavigationPaths).Select(NavigationKey).ToHashSet(StringComparer.Ordinal);
        return NavigationCatalog.Where(entry => entry.Directory == null || keys.Contains(NavigationKey(entry.Path)))
            .Select(entry => entry.Path).DistinctBy(NavigationKey, StringComparer.Ordinal).ToArray();
    }
}
