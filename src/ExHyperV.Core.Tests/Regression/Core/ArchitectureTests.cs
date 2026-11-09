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
        string[] forbidden = ["ExHyperV", "Wpf.Ui", "Wpf.Ui.Tray", "Wpf.Ui.Abstractions",
            "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms"];
        var references = typeof(VmIpService).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        CollectionAssert.AreEqual(Array.Empty<string>(), references.Intersect(forbidden).ToArray(),
            "Core must remain consumable without the WPF project.");
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
