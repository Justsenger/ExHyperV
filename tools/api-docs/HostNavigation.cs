using Microsoft.CodeAnalysis;

internal static partial class ApiDocument
{
    // Navigation describes existing members. Signatures, types and explanations still
    // come from their declarations; this is not a second host query API.
    private sealed record HostEntry(string Owner, string Member, string Area, string Category,
        string Label, string? Reader = null, string? Related = null);

    // Share the source type's remarks with its catalog section and member pages.
    private sealed record HostUsageSection(string Owner, string Area, string Category, string Title);
    private static readonly HostUsageSection[] HostUsageSections = Catalog.HostUsage;

    private static readonly HostEntry[] HostEntries = Catalog.HostEntries;

    // These are helpers or alternate lifetime forms, reachable from their owning
    // type references. Keep their classification explicit without extra menu rows.
    private static readonly HostEntry[] HostHelpers = Catalog.HostHelpers;

    private static HostEntry? FindHostEntry(TypeEntry type, MemberEntry member) =>
        HostEntries.SingleOrDefault(entry => entry.Owner == type.Symbol.Name && entry.Member == member.Name);

    private static HostEntry? FindHostNavigation(TypeEntry type, MemberEntry member) =>
        FindHostEntry(type, member) ?? HostHelpers.SingleOrDefault(entry => entry.Owner == type.Symbol.Name && entry.Member == member.Name);

    private static string[][]? HostDeclaredPaths(TypeEntry type)
    {
        if (!type.Path.StartsWith("Management/Host/", StringComparison.Ordinal)) return null;
        var paths = HostEntries.Concat(HostHelpers).Where(entry => entry.Owner == type.Symbol.Name)
            .Select(entry => HostArea(entry.Area, entry.Category)).DistinctBy(NavigationKey).ToArray();
        return paths.Length == 0 ? null : paths;
    }

    private static object? HostEntryMetadata(TypeEntry type, MemberEntry member, ScanResult scan)
    {
        var entry = FindHostEntry(type, member);
        if (entry == null) return null;
        var symbol = type.Symbol.GetMembers(member.Name).FirstOrDefault();
        string valueType = symbol switch
        {
            IPropertySymbol property => property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IFieldSymbol { Type: INamedTypeSymbol { Name: "VmQueryField", Arity: 1 } fieldType } =>
                fieldType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IFieldSymbol field => field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => type.Symbol.Name,
            IMethodSymbol method => method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            _ => ""
        };
        object? Reference(string? key)
        {
            if (key == null) return null;
            var parts = key.Split('.');
            var owner = scan.Types.Single(candidate => candidate.Symbol.Name == parts[0]);
            var target = owner.Members.Single(candidate => candidate.Name == parts[1]);
            return new { id = Anchor(target.Id), expression = key };
        }
        return new { area = entry.Area, category = entry.Category, label = entry.Label, order = Array.IndexOf(HostEntries, entry), valueType,
            reader = Reference(entry.Reader), related = Reference(entry.Related) };
    }

    private static void ValidateHostEntries(ScanResult scan)
    {
        foreach (var usage in HostUsageSections)
        {
            var owner = scan.Types.SingleOrDefault(type => type.Symbol.Name == usage.Owner);
            if (owner == null || string.IsNullOrWhiteSpace(owner.Doc.Element("remarks")?.Value)
                || !HostEntries.Any(entry => entry.Area == usage.Area && entry.Category == usage.Category))
                scan.Errors.Add($"宿主调用说明 {usage.Owner} 缺少源说明或对应目录。");
        }
        foreach (var entry in HostEntries.Concat(HostHelpers))
        {
            var candidates = scan.Types.Where(type => type.Symbol.Name == entry.Owner)
                .SelectMany(type => type.Members.Where(member => member.Name == entry.Member)).ToArray();
            if (candidates.Length == 0 || (candidates.Length > 1 && candidates.Any(member => member.Kind != "方法")))
                scan.Errors.Add($"宿主目录 {entry.Owner}.{entry.Member} 必须对应公开成员或同名方法重载。");
            foreach (string reference in new[] { entry.Reader, entry.Related }.OfType<string>())
            {
                var parts = reference.Split('.');
                if (parts.Length != 2 || scan.Types.Where(type => type.Symbol.Name == parts[0])
                    .SelectMany(type => type.Members.Where(member => member.Name == parts[1])).Count() != 1)
                    scan.Errors.Add($"宿主目录引用无效：{reference}。");
            }
        }
        if (HostEntries.Concat(HostHelpers).GroupBy(entry => (entry.Owner, entry.Member)).Any(group => group.Count() > 1))
            scan.Errors.Add("宿主目录存在重复成员。");
        foreach (var type in scan.Types)
        foreach (var member in type.Members.Where(member => member.Kind == "方法"))
            if (MemberNavigationPaths(type, member).Any(path => path[1] == "宿主") && FindHostNavigation(type, member) == null)
                scan.Errors.Add($"宿主成员 {type.Symbol.Name}.{member.Name} 尚未明确归入目录或辅助方法。");
    }
}
