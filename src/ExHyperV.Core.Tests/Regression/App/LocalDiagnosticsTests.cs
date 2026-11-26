using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExHyperV.App.Diagnostics;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
[DoNotParallelize]
public sealed class LocalDiagnosticsTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"ExHyperV-Diagnostics-{Guid.NewGuid():N}");

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(root);

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, recursive: true);

    [TestMethod]
    public void CorePublishesBothFailureOverloadsAndIsolatesObservers()
    {
        var received = new List<CoreDiagnosticEvent>();
        var vm = Guid.NewGuid();
        static void BrokenObserver(CoreDiagnosticEvent _) => throw new InvalidOperationException();
        CoreDiagnostics.Reported += BrokenObserver;
        CoreDiagnostics.Reported += received.Add;
        try
        {
            ApiResponse.Fail("private text", code: 5, kind: ApiErrorKind.AccessDenied);
            ApiResponse<int>.Fail("another private value", exception: new IOException("secret"));
            CoreDiagnostics.Report("Probe", TraceEventType.Verbose, vmId: vm);
        }
        finally
        {
            CoreDiagnostics.Reported -= BrokenObserver;
            CoreDiagnostics.Reported -= received.Add;
        }
        Assert.HasCount(3, received);
        Assert.AreEqual("ApiResponse.Fail", received[0].EventName);
        Assert.AreEqual(TraceEventType.Error, received[0].Level);
        Assert.AreEqual(ApiErrorKind.AccessDenied, received[0].ErrorKind);
        Assert.AreEqual(5, received[0].ErrorCode);
        StringAssert.Contains(received[0].Stack!, nameof(CorePublishesBothFailureOverloadsAndIsolatesObservers));
        Assert.AreEqual(typeof(IOException).FullName, received[1].ExceptionType);
        Assert.IsNotNull(received[1].HResult);
        Assert.AreEqual(vm, received[2].VmId);
        Assert.AreEqual("Probe", received[2].EventName);
        Assert.AreEqual(nameof(CorePublishesBothFailureOverloadsAndIsolatesObservers), received[2].Caller);
    }

    [TestMethod]
    public void RollsByEncodedByteLengthAndRetainsNewestCompleteRecords()
    {
        using var log = new RollingLog(root, true, true, maxBytes: 384, fileCount: 3);
        for (int n = 0; n < 12; n++)
            log.Write(TraceEventType.Information, "Unit", $"Row{n:D2}", new string('界', 75));
        log.Write(TraceEventType.Error, "Unit", "Oversized", new string('界', 9999));
        // Returning from an error write guarantees the bytes are already visible.
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "runtime-0.log")), "Oversized");
        var files = log.Snapshot(out _);
        Assert.HasCount(3, files);
        Assert.AreEqual(3, Directory.GetFiles(root, "*.log").Length);
        var utf8 = new UTF8Encoding(false, true);
        foreach (var (_, bytes) in files)
        {
            Assert.IsTrue(bytes.Length <= 384);
            StringAssert.EndsWith(utf8.GetString(bytes), "\n");
        }
        StringAssert.Contains(utf8.GetString(files[0].Bytes), "Row10");
        StringAssert.Contains(utf8.GetString(files[1].Bytes), "Row11");
        StringAssert.EndsWith(utf8.GetString(files[2].Bytes), " [truncated]\n");
    }

    [TestMethod]
    public void ToggleGatesAllLevelsAndClearIsOrderedWithConcurrentWriters()
    {
        using var log = new RollingLog(root, true, false);
        log.Write(TraceEventType.Verbose, "Unit", "RoutineBefore");
        log.Write(TraceEventType.Information, "Unit", "Before");
        log.Configure(false, true);
        log.Write(TraceEventType.Error, "Unit", "Disabled");
        log.Configure(true, true);
        log.Write(TraceEventType.Verbose, "Unit", "After");
        Parallel.For(0, 200, i => log.Write(TraceEventType.Information, "Unit", $"Parallel{i}"));
        string data = Encoding.UTF8.GetString(log.Snapshot(out _).Single().Bytes);
        Assert.AreEqual(202, data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        StringAssert.Contains(data, "event=Before");
        StringAssert.Contains(data, "event=After");
        Assert.IsFalse(data.Contains("RoutineBefore") || data.Contains("Disabled"));
        File.WriteAllText(Path.Combine(root, "unrelated.log"), "preserve");
        log.Clear();
        Assert.IsEmpty(log.Snapshot(out _));
        log.Write(TraceEventType.Information, "Unit", "Resumed");
        StringAssert.Contains(Encoding.UTF8.GetString(log.Snapshot(out _).Single().Bytes), "Resumed");
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(root, "unrelated.log")));
    }

    [TestMethod]
    public void InstancesShareRotationAndClearWhilePreservingUnrelatedFiles()
    {
        using var first = new RollingLog(root, true, false, 1024, 2);
        using var second = new RollingLog(root, true, false, 1024, 2);
        Parallel.For(0, 400, n =>
            (n % 2 == 0 ? first : second).Write(TraceEventType.Information, "Concurrent", $"Record{n}"));
        first.Flush();
        second.Flush();
        var snapshot = first.Snapshot(out var error);
        Assert.IsNull(error);
        Assert.HasCount(2, snapshot);
        Assert.IsTrue(snapshot.All(file => file.Bytes.Length <= 1024));
        first.Clear();
        Assert.IsEmpty(second.Snapshot(out _));
        second.Write(TraceEventType.Critical, "Crash", "LastRecord");
        StringAssert.Contains(Encoding.UTF8.GetString(first.Snapshot(out _).Single().Bytes), "LastRecord");
    }

    [TestMethod]
    public void UnreadableConfigurationCanBeExportedWithoutFollowingExternalReferences()
    {
        string config = Path.Combine(root, "Config.xml"), zip = Path.Combine(root, "export.zip");
        string external = Path.Combine(root, "private.txt");
        File.WriteAllText(external, "private credentials");
        File.WriteAllText(config, $"<!DOCTYPE Config [<!ENTITY external SYSTEM '{new Uri(external).AbsoluteUri}'>]><Config><MmioCeilingMb>&external;</MmioCeilingMb></Config>");
        using var log = new RollingLog(root, false, false);
        DiagnosticBundle.Export(log, config, zip, "hypervisor=Unknown");
        using var archive = ZipFile.OpenRead(zip);
        Assert.HasCount(2, archive.Entries);
        using var xml = archive.GetEntry("Config.xml")!.Open();
        Assert.IsTrue(XNode.DeepEquals(new XElement("Config", new XAttribute("status", "unavailable")), XElement.Load(xml)));
    }

    [TestMethod]
    public void ExportUsesOnlyReviewedLogFieldsAndReconstructsSafeConfiguration()
    {
        const string secret = "credential-剪贴板-4f2d";
        string config = Path.Combine(root, "Config.xml");
        File.WriteAllText(config, $"<Config password='{secret}'><MmioCeilingMb secret='{secret}'>1234</MmioCeilingMb>" +
            $"<VtpmCertificatePassword>{secret}</VtpmCertificatePassword><ArchivePassword>{secret}</ArchivePassword>" +
            $"<!-- {secret} --><FutureSetting>{secret}</FutureSetting></Config>");
        using var log = new RollingLog(root, true, true);
        using var listener = new LocalTraceListener(log);
        CoreDiagnostics.Reported += listener.OnCoreEvent;
        try
        {
            ApiResponse.Fail(secret, exception: new InvalidOperationException(secret));
            listener.TraceEvent(null, secret, TraceEventType.Error, 7, "UI event failed: {0}", new IOException(secret));
            listener.TraceEvent(null, secret, TraceEventType.Warning, 8, secret);
            listener.WriteLine(secret);
        }
        finally { CoreDiagnostics.Reported -= listener.OnCoreEvent; }

        string destination = Path.Combine(root, "diagnostics.zip");
        DiagnosticBundle.Export(log, config, destination, "scheduler=Unknown");
        using var archive = ZipFile.OpenRead(destination);
        CollectionAssert.AreEquivalent(new[] { "Logs/runtime-0.log", "Config.xml", "environment.txt" },
            archive.Entries.Select(e => e.FullName).ToArray());
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert.IsFalse(text.Contains(secret), entry.FullName);
            if (entry.FullName == "Config.xml")
                Assert.IsTrue(XNode.DeepEquals(XElement.Parse("<Config><MmioCeilingMb>1234</MmioCeilingMb></Config>"), XElement.Parse(text)));
            if (entry.FullName == "environment.txt") StringAssert.Contains(text, "scheduler=Unknown");
            if (entry.FullName.EndsWith(".log"))
            {
                StringAssert.Contains(text, "event=ApiResponse.Fail");
                StringAssert.Contains(text, "event=UiEventFailed");
                StringAssert.Contains(text, "System.IO.IOException");
            }
        }
        StringAssert.Contains(File.ReadAllText(config), secret);
    }
}
