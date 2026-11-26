using System.Text;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("WindowsIntegration")]
public sealed class WindowsIntegrationTests
{
    [TestInitialize]
    public void RequireExplicitOptIn()
    {
        if (Environment.GetEnvironmentVariable("EXHYPERV_TEST_WINDOWS") != "1")
            Assert.Inconclusive("Run src/ExHyperV.Core.Tests/Scripts/Test-Core.ps1 -Suite Windows to enable offline native integration tests.");
    }

    [TestMethod]
    [TestCategory("OperationResource")]
    public void FreeRdp_OfflineNativeSelfTest()
        => Assert.AreEqual(0u, FreeRdpNative.RunSelfTest(), "Offline native frame/input/clipboard self-test failed.");

    [TestMethod]
    [TestCategory("OperationResource")]
    public void FreeRdp_D3D11GfxPipelineSelfTest()
        => Assert.AreEqual(0u, FreeRdpNative.RunGfxSelfTest(), "Offline native D3D11 GFX pipeline self-test failed.");

    [TestMethod]
    [TestCategory("OperationResource")]
    public void FreeRdp_MultiRegionFramesReconstructAndSurviveProducerShutdown() => ConsoleNativeFrameChecks.Run();

    [TestMethod]
    [TestCategory("OperationResource")]
    public async Task ComExecutor_SerializesCallsOnOneStaAndSurvivesFailure()
    {
        var samples = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
            ComApi.RunOnSta(() => (Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState())))));
        Assert.AreEqual(1, samples.Select(s => s.ManagedThreadId).Distinct().Count());
        Assert.IsTrue(samples.All(s => s.Item2 == ApartmentState.STA));
        Assert.ThrowsExactly<InvalidOperationException>(() => ComApi.RunOnSta<int>(() => throw new InvalidOperationException("fixture")));
        Assert.AreEqual(42, ComApi.RunOnSta(() => ComApi.RunOnSta(() => 42)));
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public void Screenshot_EncodesJpegWithOriginalDimensions()
    {
        var pixels = new byte[] { 0, 248, 224, 7, 31, 0, 255, 255 };
        var frame = VmScreenshotImage.FromRgb565(pixels, 2, 2);
        pixels[0] = 255;
        Assert.AreEqual((byte)0, frame.CopyBytes()[0]);
        var decoded = VmScreenshotImage.FromEncoded(frame.EncodeJpeg());
        Assert.AreEqual(2, decoded.Width);
        Assert.AreEqual(2, decoded.Height);
        Assert.IsTrue(decoded.IsEncoded);
    }

    [TestMethod]
    [TestCategory("OperationCommand")]
    public void IsoBuilder_WritesAValidImageUsingWindowsCom()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV-IsoTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source");
        Directory.CreateDirectory(source);
        string input = Path.Combine(source, "fixture.txt");
        string output = Path.Combine(directory, "fixture.iso");
        try
        {
            File.WriteAllText(input, "Core ISO integration fixture");
            ComApi.RunOnSta(() => { IsoBuilderService.BuildUdfIso(source, output, "ExHyperV Tests"); return true; });
            using var stream = File.OpenRead(output);
            stream.Position = 16 * 2048 + 1;
            var signature = new byte[5];
            stream.ReadExactly(signature);
            Assert.AreEqual("CD001", Encoding.ASCII.GetString(signature));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
            Directory.Delete(source);
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    [TestCategory("OperationCommand")]
    public void IsoBuilder_MultiSourceReportsProgressAndCleansPartialFileOnCancel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV-MultiIsoTest-" + Guid.NewGuid().ToString("N"));
        string rootDir = Path.Combine(directory, "root");
        string subDir = Path.Combine(directory, "sub");
        string output = Path.Combine(directory, "driver.iso");
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(subDir);
        try
        {
            File.WriteAllText(Path.Combine(rootDir, "安装显卡驱动.cmd"), "@echo off\r\n");
            File.WriteAllBytes(Path.Combine(subDir, "payload.bin"), new byte[128 * 1024]);

            var percents = new List<int>();
            ComApi.RunOnSta(() =>
            {
                IsoBuilderService.BuildUdfIso(
                    [
                        new IsoDirectorySource(rootDir, string.Empty),
                        new IsoDirectorySource(subDir, Path.Combine("_payload", "HostDriverStore"))
                    ],
                    output,
                    "EXHYPERV_GPU",
                    pct => percents.Add(pct),
                    CancellationToken.None);
                return true;
            });

            Assert.IsTrue(File.Exists(output), "Multi-source ISO should be created.");
            Assert.IsTrue(percents.Contains(0) && percents.Contains(100), "Progress should report 0% and 100%.");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => ComApi.RunOnSta(() =>
            {
                IsoBuilderService.BuildUdfIso(
                    [new IsoDirectorySource(rootDir, string.Empty)],
                    output,
                    "EXHYPERV_GPU",
                    null,
                    cts.Token);
                return true;
            }));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("OperationCommand")]
    public void IsoBuilder_SupportsLargeImagesBeyondCdLimit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV-LargeIsoTest-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(directory, "source");
        string output = Path.Combine(directory, "large.iso");
        Directory.CreateDirectory(source);
        try
        {
            // > 700 MB to exceed IMAPI2FS's default FreeMediaBlocks (~650 MB CD-R limit)
            const long payloadSize = 760L * 1024 * 1024;
            using (var fs = File.Create(Path.Combine(source, "large-driver.bin")))
            {
                fs.SetLength(payloadSize);
            }

            ComApi.RunOnSta(() =>
            {
                IsoBuilderService.BuildUdfIso(
                    [new IsoDirectorySource(source, string.Empty)],
                    output,
                    "EXHYPERV_LARGE",
                    null,
                    CancellationToken.None);
                return true;
            });

            Assert.IsTrue(File.Exists(output), "Large ISO beyond CD-R limit should be created.");
            Assert.IsTrue(new FileInfo(output).Length > payloadSize, "Large ISO should contain full payload.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public void HostCpuThreadMonitor_OfflineTopologyAndProcessSnapshotAreConsistent()
    {
        var topology = HostCpuEtwNative.ReadProcessorTopology();
        Assert.IsTrue(topology.Count > 0, "Should detect at least one active logical processor.");
        Assert.AreEqual(Environment.ProcessorCount, topology.Count, "Flattened multi-group LP count should match OS processor count.");
        for (int i = 0; i < topology.Count; i++)
        {
            Assert.AreEqual(i, topology[i].LogicalProcessorIndex);
            Assert.IsTrue(topology[i].GroupProcessorIndex is >= 0 and < 64);
        }

        var snapshot = HostCpuEtwNative.SnapshotProcessesAndThreads();
        Assert.IsTrue(snapshot.Processes.ContainsKey(Environment.ProcessId), "Toolhelp32 snapshot should capture current test process.");
        Assert.IsTrue(snapshot.ThreadToProcess.Count > 0, "Toolhelp32 snapshot should capture live threads.");
    }
}
