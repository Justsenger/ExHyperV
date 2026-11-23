using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.IO.Compression;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void Core_DoesNotReferenceDesktopUiAssemblies()
    {
        string[] forbidden = ["ExHyperV", "ExHyperV.App", "Wpf.Ui", "Wpf.Ui.Tray", "Wpf.Ui.Abstractions",
            "ExHyperV.WinUI3", "Microsoft.WinUI", "Microsoft.UI.Xaml", "Microsoft.InteractiveExperiences.Projection", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms"];
        var references = typeof(VmIpService).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.IsFalse(references.Any(name => name != null && (name.StartsWith("Microsoft.WindowsApp", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase))));
        var friends = typeof(VmIpService).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(friend => new AssemblyName(friend.AssemblyName).Name);
        CollectionAssert.AreEqual(Array.Empty<string>(), friends.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Core must not grant desktop UI assemblies access to its internals.");
        CollectionAssert.AreEqual(Array.Empty<string>(), references.Intersect(forbidden, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Core must remain consumable without a desktop UI.");
    }

    [TestMethod]
    public void Core_DoesNotDependOnWindowsAppSdkPackages()
    {
        string deps = Path.ChangeExtension(typeof(ArchitectureTests).Assembly.Location, ".deps.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(deps));
        string targetName = document.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        var target = document.RootElement.GetProperty("targets").GetProperty(targetName);
        var core = target.EnumerateObject().Single(package => package.Name.StartsWith("ExHyperV.Core/", StringComparison.Ordinal));
        var pending = new Stack<string>([core.Name]);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out string? packageId))
        {
            if (!visited.Add(packageId)) continue;
            Assert.IsFalse(packageId.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase), packageId);
            if (target.TryGetProperty(packageId, out var package) && package.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateObject())
                    pending.Push($"{dependency.Name}/{dependency.Value.GetString()}");
        }
    }

    [TestMethod]
    public void Core_DoesNotEmbedPowerShellScriptsOrReferenceAutomation()
    {
        var assembly = typeof(VmIpService).Assembly;
        using var file = File.OpenRead(assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = metadata.GetNextHandle(handle))
        {
            string literal = metadata.GetUserString(handle);
            Assert.IsFalse(literal.Contains("$ErrorActionPreference", StringComparison.OrdinalIgnoreCase)
                || literal.Contains("powershell.exe -", StringComparison.OrdinalIgnoreCase),
                "Core contains a PowerShell installer or launcher literal.");
        }
        var embeddedPs = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), embeddedPs,
            "Core must not embed PowerShell scripts.");

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name);
        CollectionAssert.AreEqual(Array.Empty<string>(),
            references.Where(name => string.Equals(name, "System.Management.Automation", StringComparison.OrdinalIgnoreCase)).ToArray(),
            "Core must not reference System.Management.Automation.");
    }

    [TestMethod]
    public void PciDatabase_IsAvailableAsAnEmbeddedResource()
    {
        using var stream = typeof(VmIpService).Assembly.GetManifestResourceStream("ExHyperV.Assets.pci.ids");
        Assert.IsNotNull(stream);
        using var reader = new StreamReader(stream);
        StringAssert.Contains(reader.ReadToEnd(), "8086  Intel Corporation");
    }

    [TestMethod]
    [DataRow("win-x64")]
    [DataRow("win-arm64")]
    public void ConsolePackage_ContainsNativeLibrariesForEachSupportedArchitecture(string runtime)
    {
        using var stream = typeof(VmIpService).Assembly.GetManifestResourceStream($"ExHyperV.ConsoleNative.{runtime}.zip");
        Assert.IsNotNull(stream, $"Missing embedded console package for {runtime}.");
        using var archive = new ZipArchive(stream);
        Assert.IsTrue(archive.Entries.Any(e => e.Name == "ExHyperV.FreeRdp.dll"),
            "A console package must contain the Core native entry library.");
        Assert.IsTrue(archive.Entries.All(e => !Path.IsPathRooted(e.FullName) && !e.FullName.Split('/', '\\').Contains("..")),
            "Package paths must stay within their extraction directory.");
    }
}
