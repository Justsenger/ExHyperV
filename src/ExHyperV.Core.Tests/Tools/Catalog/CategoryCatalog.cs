using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Consume the same metadata as the API website, including explicit member assignments.
internal sealed class CategoryCatalog
{
    private const string Separator = " / ";
    private readonly Dictionary<string, List<string[]>> types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string[]>> members = new(StringComparer.Ordinal);
    private readonly List<string> categoryOrder = [];
    private static readonly Dictionary<string, string[]> ContractAliases = new(StringComparer.Ordinal)
    {
        ["控制台事件"] = ["IConsoleConnection", "IConsoleVideoSource", "IConsoleClipboard"]
    };

    public CategoryCatalog(string core)
    {
        using var data = ReadDocumentation(core);
        foreach (var category in data.RootElement.GetProperty("categories").EnumerateArray())
        {
            var path = PathParts(category.GetProperty("path"));
            for (int length = 1; length <= path.Length; length++)
            {
                string key = string.Join(Separator, path.Take(length));
                if (!categoryOrder.Contains(key)) categoryOrder.Add(key);
            }
        }
        foreach (var type in data.RootElement.GetProperty("types").EnumerateArray())
        {
            string name = Normalize(type.GetProperty("shortName").GetString()!);
            foreach (var location in type.GetProperty("navigationPaths").EnumerateArray())
                Add(types, name, PathParts(location));
            foreach (var member in type.GetProperty("members").EnumerateArray())
                foreach (var location in member.GetProperty("navigationPaths").EnumerateArray())
                    Add(members, name + "." + Normalize(member.GetProperty("name").GetString()!), PathParts(location));
        }
        if (types.Count == 0 || categoryOrder.Count < 2) throw new InvalidDataException("Core API metadata is empty.");
    }

    private static JsonDocument ReadDocumentation(string core)
    {
        string output = Path.Combine(Path.GetTempPath(), "ExHyperV-api-catalog-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (string argument in new[] { "run", "--project", Path.Combine(core, "Documentation", "Generator"),
                "--no-launch-profile", "--", "--html", output, "--root", core }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("API generator failed to start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("API generator timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException("Core API metadata generation failed:\n" + stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            var match = Regex.Match(File.ReadAllText(output), "<script type=\"application/json\" id=\"api-data\">(?<data>.*?)</script>", RegexOptions.Singleline);
            if (!match.Success) throw new InvalidDataException("Core API website metadata was not found.");
            return JsonDocument.Parse(match.Groups["data"].Value);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    public string Category(string api)
    {
        var method = Regex.Match(api, @"^(?<type>[A-Za-z]\w*(?:<T>)?(?:\.[A-Za-z]\w*)?)\.(?<member>[A-Za-z]\w*(?:<T>)?|构造)\(");
        if (method.Success)
        {
            string type = Normalize(method.Groups["type"].Value), member = Normalize(method.Groups["member"].Value);
            if (member == "构造") return CommonCategory(FindType(type));
            if (members.TryGetValue(type + "." + member, out var locations)) return CommonCategory(locations);
            if (member is "Dispose" or "DisposeAsync") return CommonCategory(FindType(type));
            throw new InvalidOperationException($"API method has no source category: {api}");
        }
        string[] names = ContractAliases.TryGetValue(api, out var alias) ? alias :
            Regex.Matches(api, @"\b[A-Z][A-Za-z0-9]+(?:\.[A-Z][A-Za-z0-9]+)?").Select(match => match.Value).ToArray();
        if (names.Length == 0) throw new InvalidOperationException($"Test requires an explicit contract or framework assignment: {api}");
        return CommonCategory(names.SelectMany(FindType));
    }

    public string[] Audit(JsonArray catalog)
    {
        var problems = new List<string>();
        foreach (var item in catalog)
        {
            try
            {
                string expected = Category(item!["api"]!.GetValue<string>());
                if (item["group"]!.GetValue<string>() != expected) problems.Add($"{item["id"]}: {item["group"]} → {expected}");
            }
            catch (InvalidOperationException error) { problems.Add($"{item!["id"]}: {error.Message}"); }
        }
        if (problems.Count == 0 && !catalog.Select(item => item!["id"]!.GetValue<string>())
            .SequenceEqual(Sorted(catalog).Select(item => item!["id"]!.GetValue<string>())))
            problems.Add("测试项目顺序需按 Core 分类同步。");
        return problems.ToArray();
    }

    public void Synchronize(JsonArray catalog, string path)
    {
        foreach (var item in catalog) item!["group"] = Category(item["api"]!.GetValue<string>());
        var sorted = new JsonArray(Sorted(catalog).Select(item => item!.DeepClone()).ToArray());
        File.WriteAllText(path, sorted.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n");
    }
    private IEnumerable<JsonNode?> Sorted(JsonArray catalog) => catalog
        .OrderBy(item => categoryOrder.IndexOf(Category(item!["api"]!.GetValue<string>())))
        .ThenBy(item => item!["order"]!.GetValue<int>());
    private List<string[]> FindType(string name) => types.TryGetValue(Normalize(name), out var paths) ? paths :
        throw new InvalidOperationException($"Core public type has no category: {name}");
    private static string CommonCategory(IEnumerable<string[]> locations)
    {
        var paths = locations.ToArray();
        var shared = paths[0].TakeWhile((part, index) => paths.All(path => path.Length > index && path[index] == part)).ToArray();
        if (shared.Length == 0) throw new InvalidOperationException("Test spans Core category roots; split its contract explicitly.");
        return string.Join(Separator, shared);
    }
    private static string Normalize(string name) => name.Replace("<T>", "");
    private static string[] PathParts(JsonElement path) => path.EnumerateArray().Select(part => part.GetString()!).ToArray();
    private static void Add(Dictionary<string, List<string[]>> index, string key, string[] path)
    {
        if (!index.TryGetValue(key, out var paths)) index[key] = paths = [];
        paths.Add(path);
    }
}
