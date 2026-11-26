using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class HostDeviceTelemetryTests
{
    [TestMethod]
    public void EngineInstancesResolveBothHalvesOfTheAdapterLuid()
    {
        Assert.IsTrue(HostDeviceTelemetryService.TryParseLuid(
            "pid_1_luid_0x80000000_0x00ABCDEF_phys_0_eng_2_engtype_3D", out var luid));
        Assert.AreEqual((0x80000000u, 0x00ABCDEFu), luid);
        Assert.IsFalse(HostDeviceTelemetryService.TryParseLuid("pid_1_luid_0x01_phys_0_eng_2_engtype_3D", out _));
    }

    [TestMethod]
    public void GpuLoadsStayWithTheirDeviceAndDistinguishIdleFromMissing()
    {
        var readings = HostDeviceTelemetryService.GroupGpuReadings([
            ("gpu-a", 0, 0, 10), ("GPU-A", 0, 0, 25), ("gpu-a", 1, 0, 30),
            ("gpu-a", 3, 1, 5), ("gpu-a", 3, 1, null), ("gpu-b", 0, 0, 0),
            ("warming", 0, 0, null)
        ]);
        Assert.AreEqual(35d, readings["gpu-a"].Pct3D);
        Assert.IsNull(readings["gpu-a"].PctCopy);
        Assert.IsNull(readings["gpu-b"].PctEnc);
        Assert.AreEqual(0d, readings["gpu-b"].Pct3D);
        Assert.IsFalse(readings.ContainsKey("warming"));
    }
}
