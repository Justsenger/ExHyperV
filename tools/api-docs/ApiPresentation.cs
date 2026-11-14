using System.Reflection;
using System.Text.Json;

internal static partial class ApiDocument
{
    private sealed record ApiPresentation(string Title, string Role, string[] Path, string Group);
    private sealed record CatalogMember(string Title, string[][] Paths, string Group, bool ReadOnly, string? HelpSource = null, string[]? Examples = null);
    private sealed record ApiCatalog(int SchemaVersion, string[][] Categories,
        Dictionary<string, ApiPresentation> Types, Dictionary<string, CatalogMember> Members,
        HostEntry[] HostEntries, HostEntry[] HostHelpers, HostUsageSection[] HostUsage);
    private static readonly ApiCatalog Catalog = ReadCatalog();
    private static ApiCatalog ReadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.catalog.json")
            ?? throw new InvalidOperationException("缺少 doc/api/catalog.json。");
        var catalog = JsonSerializer.Deserialize<ApiCatalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("API 目录材料为空。");
        if (catalog.SchemaVersion != 1) throw new InvalidOperationException("API 目录版本无效。");
        return catalog;
    }
    private static ApiPresentation PresentationFor(TypeEntry type) => Catalog.Types.TryGetValue(type.Name, out var item)
        ? item : throw new InvalidOperationException($"请在 doc/api/catalog.json 中登记公开类型：{type.Name}");
    private static CatalogMember PresentationFor(MemberEntry member) => Catalog.Members.TryGetValue(member.Id, out var item)
        ? item : throw new InvalidOperationException($"请在 doc/api/catalog.json 中登记公开成员：{member.Id}");
    private static string TypeDisplayName(TypeEntry type) => PresentationFor(type).Title;
    private static string MethodDisplayName(TypeEntry type, MemberEntry member) => PresentationFor(member).Title;
    private static void ValidatePresentation(ScanResult scan)
    {
        var typeNames = scan.Types.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var memberIds = scan.Types.SelectMany(type => type.Members).Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        var stale = Catalog.Types.Keys.Where(key => !typeNames.Contains(key))
            .Concat(Catalog.Members.Keys.Where(key => !memberIds.Contains(key))).ToArray();
        if (stale.Length != 0) throw new InvalidOperationException("目录含有已移除的接口：" + string.Join(", ", stale));
        foreach (var type in scan.Types)
        {
            var presentation = PresentationFor(type);
            if (string.IsNullOrWhiteSpace(presentation.Title) || presentation.Role is not ("entry" or "contract"))
                throw new InvalidOperationException($"类型标题或用途无效：{type.Name}");
            var duplicateTitles = type.Members
                .Where(member => member.Kind is "方法" or "构造函数" or "构造器" or "主构造器" or "运算符")
                .GroupBy(member => PresentationFor(member).Title, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateTitles.Length != 0)
                throw new InvalidOperationException($"方法标题须区分操作和重载：{type.Name}：{string.Join("、", duplicateTitles)}");
            foreach (var member in type.Members)
            {
                var entry = PresentationFor(member);
                if (entry.HelpSource is { } source && !scan.Types.SelectMany(t => t.Members).Any(m => m.Id == source))
                    throw new InvalidOperationException($"字段说明来源不存在：{member.Id} → {source}");
                if (string.IsNullOrWhiteSpace(entry.Title) || entry.Paths.Length == 0
                    || entry.Group is not ("core" or "vm" or "host" or "devices" or "console" or "utilities"))
                    throw new InvalidOperationException($"成员目录无效：{member.Id}");
            }
        }
    }
}
