using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class AppStructureTests
{
    private static string FindAppDirectory()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
            {
                string app = Path.Combine(folder.FullName, "src", "ExHyperV.App");
                if (File.Exists(Path.Combine(app, "ExHyperV.App.csproj"))) return app;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the ExHyperV.App source directory.");
    }

    private static IEnumerable<string> SourceFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));

    [TestMethod]
    public void NamespacesAndDependenciesFollowAppDirectories()
    {
        string root = FindAppDirectory();
        foreach (string file in SourceFiles(root, "*.cs"))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string directory = relative[..(relative.LastIndexOf('/') + 1)].TrimEnd('/');
            string expected = directory.Length == 0 ? "ExHyperV.App" : "ExHyperV.App." + directory.Replace('/', '.');
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
            var declarations = syntax.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToArray();
            Assert.HasCount(1, declarations, file);
            Assert.IsInstanceOfType<FileScopedNamespaceDeclarationSyntax>(declarations[0], file);
            Assert.AreEqual(expected, declarations[0].Name.ToString(), file);

            foreach (var directive in syntax.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                string name = directive.Name?.ToString().Replace("global::", "") ?? "";
                AssertAllowed(relative, name);
            }
        }
        foreach (string file in SourceFiles(root, "*.xaml"))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var attribute in XDocument.Load(file).Descendants().Attributes()
                .Where(attribute => attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("using:", StringComparison.Ordinal)))
                AssertAllowed(relative, attribute.Value["using:".Length..]);
        }
    }

    private static void AssertAllowed(string path, string name)
    {
        string[] forbidden = path switch
        {
            _ when path.StartsWith("UI/Pages/Host/", StringComparison.Ordinal) => ["UI.Pages.VirtualMachines"],
            _ when path.StartsWith("UI/Shared/", StringComparison.Ordinal) => ["UI.Pages", "UI.Shell"],
            _ when path.StartsWith("Localization/", StringComparison.Ordinal) => ["UI"],
            _ => []
        };
        foreach (string branch in forbidden)
        {
            string prefix = "ExHyperV.App." + branch;
            Assert.IsFalse(name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal), $"{path} imports {name}");
        }
    }

    [TestMethod]
    public void StringTablesHaveMatchingUniqueKeysAndValidPunctuation()
    {
        string directory = Path.Combine(FindAppDirectory(), "Localization");
        Dictionary<string, XElement> ReadTable(string file)
        {
            var entries = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var entry in XDocument.Load(file).Root!.Elements("data"))
            {
                string key = entry.Attribute("name")!.Value;
                Assert.IsTrue(entries.TryAdd(key, entry), $"Duplicate {key} in {file}");
                string value = entry.Element("value")!.Value;
                // XML entities have already been decoded. File-picker patterns can contain semicolons.
                string copy = Regex.Replace(value, @"\*\.[\w]+(?:;\*\.[\w]+)+", "");
                Assert.IsFalse(copy.IndexOfAny(['；', ';', '·']) >= 0, $"{file} {key}: {value}");
            }
            return entries;
        }

        var neutral = ReadTable(Path.Combine(directory, "Strings.resx"));
        var chinese = ReadTable(Path.Combine(directory, "Strings.zh-CN.resx"));
        CollectionAssert.AreEquivalent(neutral.Keys.ToArray(), chinese.Keys.ToArray());
        foreach (var (key, entry) in neutral)
        {
            if (entry.Element("comment")?.Value == "TODO: en")
                Assert.AreEqual(chinese[key].Element("value")!.Value, entry.Element("value")!.Value, key);
        }

        foreach (string file in Directory.EnumerateFiles(directory, "Strings.*.resx")
            .Where(file => Path.GetFileName(file) != "Strings.zh-CN.resx"))
            CollectionAssert.AreEquivalent(neutral.Keys.ToArray(), ReadTable(file).Keys.ToArray(), file);
    }

    [TestMethod]
    public void ChineseUiCopyComesFromResources()
    {
        string root = FindAppDirectory();
        static bool ContainsChinese(string text) => text.Any(ch => ch is >= '\u3400' and <= '\u9fff');
        foreach (string file in SourceFiles(Path.Combine(root, "UI"), "*.cs")
            .Append(Path.Combine(root, "Localization", "ApiFieldHelp.cs")))
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
            foreach (var token in syntax.DescendantTokens().Where(token =>
                token.Parent is LiteralExpressionSyntax or InterpolatedStringTextSyntax))
                Assert.IsFalse(ContainsChinese(token.ValueText), $"{file}: {token.Text}");
        }
        foreach (string file in SourceFiles(Path.Combine(root, "UI"), "*.xaml"))
        {
            var elements = XDocument.Load(file).Descendants().ToArray();
            foreach (string text in elements.Attributes().Select(a => a.Value)
                .Concat(elements.Nodes().OfType<XText>().Select(t => t.Value)))
                Assert.IsFalse(ContainsChinese(text), $"{file}: {text}");
        }
    }
}
