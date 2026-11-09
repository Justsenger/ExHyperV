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
        => Assert.AreEqual(0u, FreeRdpNative.RunSelfTest(), "Native frame/input/clipboard self-test failed; no VM is connected.");

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
}
