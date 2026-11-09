using System.Reflection;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ImportSourceResolutionTests
{
    [TestMethod]
    [DataRow(VmImportSourceKind.Folder)]
    [DataRow(VmImportSourceKind.Zip)]
    public void RemovedPreviewDiskKeepsItsIdentityWhenOriginalStillExists(VmImportSourceKind kind)
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.Original, "original-host-data");
        File.WriteAllText(fixture.Exported, "exported-data");
        var session = fixture.Session(kind);
        Assert.AreEqual(fixture.Exported, Resolve(session, fixture.Original));
        File.Delete(fixture.Exported);
        Assert.AreEqual(fixture.Exported, Resolve(session, fixture.Original));
        Assert.AreEqual("original-host-data", File.ReadAllText(fixture.Original));
        Assert.IsFalse(File.Exists(Resolve(session, fixture.Original)));
    }

    [TestMethod]
    public void ExternalDiskSelectedInitiallyRemainsValid()
    {
        using var fixture = new Files();
        File.WriteAllText(fixture.Original, "external-source");
        var session = fixture.Session();
        Assert.AreEqual(fixture.Original, Resolve(session, fixture.Original));
        // A later same-name file must not replace the source already shown in the preview.
        File.WriteAllText(fixture.Exported, "late-copy");
        Assert.AreEqual(fixture.Original, Resolve(session, fixture.Original));
    }

    private static string Resolve(VmImportSession session, string configured)
        => (string)typeof(VmImportService).GetMethod("ResolveSourceDiskPath", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [session, configured])!;

    private sealed class Files : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ExHyperV-ImportSource-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(root, "export");
        internal string Original => Path.Combine(root, "original", "disk.vhdx");
        internal string Exported => Path.Combine(Source, "Virtual Hard Disks", "disk.vhdx");
        internal Files()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Original)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Exported)!);
        }
        internal VmImportSession Session(VmImportSourceKind kind = VmImportSourceKind.Folder) => new()
        {
            SourcePath = Source, SourceRoot = Source, SourceKind = kind,
            PlacementMode = VmImportPlacementMode.HostDirectories,
            ArchiveFullyExtracted = kind == VmImportSourceKind.Zip,
            ArchiveEntries = new Dictionary<string, long> { ["Virtual Hard Disks/disk.vhdx"] = 13 },
            ExtractedArchivePaths = new Dictionary<string, string> { ["Virtual Hard Disks/disk.vhdx"] = Exported }
        };
        public void Dispose()
        {
            File.Delete(Original); File.Delete(Exported);
            Directory.Delete(Path.GetDirectoryName(Original)!);
            Directory.Delete(Path.GetDirectoryName(Exported)!);
            Directory.Delete(Source); Directory.Delete(root);
        }
    }
}
