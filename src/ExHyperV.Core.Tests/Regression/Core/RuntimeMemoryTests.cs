using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class RuntimeMemoryTests
{
    [TestMethod]
    [DataRow(50, 1024, 1024, 50)]
    [DataRow(120, 1024, 1024, -20)]
    [DataRow(50, 512, 1024, 75)]
    public void FixedMemoryUsesDemandAndPreservesOvercommit(int pressure, long visible, long physical, int expected)
    {
        var observed = DateTimeOffset.UtcNow;
        var assigned = new PerformanceSampleInfo(PerformanceSampleState.Valid, observed.AddSeconds(-1));
        var original = new VmQueryService.VmDynamicMemoryData { AssignedMiB = 1024, AssignedSample = assigned };
        var result = VmQueryService.MapFixedMemoryDemand(original, pressure, visible, physical, observed);
        Assert.AreEqual(expected, result.AvailablePercent);
        Assert.AreEqual(PerformanceSampleState.Valid, result.AvailableSample!.State);
        Assert.AreEqual(observed, result.AvailableSample.ObservedAtUtc);
        Assert.AreSame(assigned, result.AssignedSample);
        Assert.AreEqual(1024L, result.AssignedMiB);
    }

    [TestMethod]
    [DataRow(0, 1024, 1024)]
    [DataRow(50, 0, 1024)]
    [DataRow(50, 1024, 512)]
    public void MissingOrMismatchedDemandPreservesAssignedMemory(int pressure, long visible, long physical)
    {
        var observed = DateTimeOffset.UtcNow;
        var assigned = new PerformanceSampleInfo(PerformanceSampleState.Valid, observed);
        var original = new VmQueryService.VmDynamicMemoryData { AssignedMiB = 1024, AssignedSample = assigned };
        var result = VmQueryService.MapFixedMemoryDemand(original, pressure, visible, physical, observed);
        Assert.AreEqual(PerformanceSampleState.Unavailable, result.AvailableSample!.State);
        Assert.IsFalse(result.AvailableSample.IsValid);
        Assert.AreSame(assigned, result.AssignedSample);
        Assert.AreEqual(1024L, result.AssignedMiB);
    }
}
