using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

try
{
    if (args.Length is < 1 or > 2 || args[0] is not ("--audit-tests" or "--sync-categories"))
        throw new ArgumentException("Usage: dotnet run --project src/ExHyperV.Core.Tests/Tools/Catalog -- --audit-tests|--sync-categories [repository]");
    var root = Path.GetFullPath(args.Length == 2 ? args[1] : Directory.GetCurrentDirectory());
    var core = Path.Combine(root, "src", "ExHyperV.Core");
    if (!File.Exists(Path.Combine(core, "ExHyperV.Core.csproj")))
        throw new InvalidOperationException("Run from the repository root or pass its path.");

    var files = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
        .Where(file => IsOwnSource(Path.GetRelativePath(root, file))).Order(StringComparer.Ordinal).ToArray();
    var methods = new List<Method>();
    var ownersByKey = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in files)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var owners = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().ToArray();
            if (owners.Length == 0 || (!method.Modifiers.Any(SyntaxKind.PublicKeyword) && method.Parent is not InterfaceDeclarationSyntax) ||
                method.Modifiers.Any(SyntaxKind.PrivateKeyword) || method.Modifiers.Any(SyntaxKind.InternalKeyword) ||
                owners.Any(owner => !owner.Modifiers.Any(SyntaxKind.PublicKeyword))) continue;
            var type = string.Join('.', owners.Reverse().Select(owner => owner.Identifier.ValueText));
            var key = type + "." + method.Identifier.ValueText;
            var ns = string.Join('.', method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse().Select(declaration => declaration.Name.ToString()));
            var qualifiedOwner = ns + "." + type;
            if (ownersByKey.TryGetValue(key, out var existing) && existing != qualifiedOwner)
                throw new InvalidOperationException($"Ambiguous method key across namespaces: {key}");
            ownersByKey[key] = qualifiedOwner;
            methods.Add(new(key,
                Path.GetRelativePath(root, file).Replace('\\', '/'),
                tree.GetLineSpan(method.Span).StartLinePosition.Line + 1));
        }
    }
    if (methods.Count == 0) throw new InvalidOperationException("No explicit public methods found.");
    var byKey = methods.GroupBy(method => method.Key).ToDictionary(group => group.Key, group => group.ToArray());
    if (args[0] is "--audit-tests" or "--sync-categories")
    {
        string catalogPath = Path.Combine(root, "src", "ExHyperV.Core.Tests", "TestPlanning", "TestCatalog.json");
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsArray();
        var categories = new CategoryCatalog(core);
        if (args[0] == "--sync-categories")
        {
            categories.Synchronize(catalog, catalogPath);
            Console.WriteLine($"已同步 {catalog.Count} 个检查点的 Core 分类与顺序。");
            return 0;
        }
        var planned = catalog.Select(item => item!["api"]!.GetValue<string>()).ToArray();
        var unplanned = byKey.Keys.Where(key => !planned.Any(api => api.Replace("<T>", "").StartsWith(key + "(", StringComparison.Ordinal))).Order().ToArray();
        var categoryProblems = categories.Audit(catalog);
        Console.WriteLine(JsonSerializer.Serialize(new { PublicMethods = byKey.Count, Missing = unplanned, CategoryProblems = categoryProblems }, new JsonSerializerOptions { WriteIndented = true }));
        return unplanned.Length == 0 && categoryProblems.Length == 0 ? 0 : 1;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static bool IsOwnSource(string path) => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    .Intersect(new[] { "bin", "obj", "external", ".artifacts", "Documentation" }, StringComparer.OrdinalIgnoreCase).Any()
    && !path.EndsWith("Resources.Designer.cs", StringComparison.OrdinalIgnoreCase);

sealed record Method(string Key, string Path, int Line);
