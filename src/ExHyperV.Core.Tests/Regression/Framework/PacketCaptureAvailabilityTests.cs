using ExHyperV.Core.Tests.ResourceInspection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class PacketCaptureAvailabilityTests
{
    [TestMethod]
    public void AvailableApiOnlyLoadsAndReleasesBindings()
    {
        var binding = new Binding();
        Assert.AreEqual(InspectionStatus.Ready, PacketCaptureAvailability.Inspect(() => binding).Status);
        Assert.AreEqual(1, binding.Disposals);
    }

    [TestMethod]
    public void MissingPlatformApisAndFailedChecksHaveDifferentStates()
    {
        foreach (Exception error in new Exception[] { new DllNotFoundException(), new EntryPointNotFoundException(), new PlatformNotSupportedException(), new BadImageFormatException() })
            Assert.AreEqual(InspectionStatus.Missing, PacketCaptureAvailability.Inspect(() => throw error).Status);
        Assert.AreEqual(InspectionStatus.Unavailable, PacketCaptureAvailability.Inspect(() => throw new IOException("fixture")).Status);
    }

    private sealed class Binding : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }
}
