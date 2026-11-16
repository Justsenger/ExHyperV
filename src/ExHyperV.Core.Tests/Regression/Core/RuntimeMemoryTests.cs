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

    [TestMethod]
    public void DynamicMemoryExtractsAvailableBufferDemandAndRejectsMaxValueSentinel()
    {
        var observed = DateTimeOffset.UtcNow;
        var ready = VmQueryService.MapRuntimeMemory(2048L, 30, 2, true, observed, 125);
        Assert.AreEqual(2048L, ready.AssignedMiB);
        Assert.AreEqual(30, ready.AvailablePercent);
        Assert.AreEqual(125, ready.AvailableBufferPercent);
        Assert.IsTrue(ready.AvailableBufferSample!.IsValid);
        Assert.AreEqual(1434L, ready.DemandMiB);
        Assert.IsTrue(ready.DemandSample!.IsValid);

        var notReady = VmQueryService.MapRuntimeMemory(2048L, 30, 2, true, observed, int.MaxValue);
        Assert.AreEqual(PerformanceSampleState.Unavailable, notReady.AvailableBufferSample!.State);
        Assert.AreEqual(0, notReady.AvailableBufferPercent);

        var fixedMem = VmQueryService.MapRuntimeMemory(2048L, null, 2, false, observed, 20);
        Assert.AreEqual(PerformanceSampleState.NotApplicable, fixedMem.AvailableBufferSample!.State);
    }

    [TestMethod]
    public void BalancerCountersPopulatePressureGuestSizesAndSmartPagingWithoutOverwritingDynamicSummary()
    {
        var observed = DateTimeOffset.UtcNow;
        var initial = VmQueryService.MapRuntimeMemory(2048L, 25, 2, true, observed, 40);
        var updated = VmQueryService.MapBalancerMemoryCounters(initial, 80, 2560L, 2048L, 720L, 64L, true, observed);
        Assert.AreEqual(80, updated.PressurePercent);
        Assert.IsTrue(updated.PressureSample!.IsValid);
        Assert.AreEqual(2560L, updated.GuestVisibleMiB);
        Assert.AreEqual(720L, updated.GuestAvailableMiB);
        Assert.AreEqual(64L, updated.SmartPagingMiB);
        Assert.AreEqual(25, updated.AvailablePercent);
        Assert.AreEqual(1536L, updated.DemandMiB);

        var fixedInitial = VmQueryService.MapRuntimeMemory(2048L, null, 2, false, observed, null);
        var fixedUpdated = VmQueryService.MapBalancerMemoryCounters(fixedInitial, 70, 2048L, 2048L, 614L, 0L, false, observed);
        Assert.AreEqual(70, fixedUpdated.PressurePercent);
        Assert.AreEqual(1434L, fixedUpdated.DemandMiB);
        Assert.AreEqual(30, fixedUpdated.AvailablePercent);
        Assert.AreEqual(614L, fixedUpdated.GuestAvailableMiB);
        Assert.AreEqual(0L, fixedUpdated.SmartPagingMiB);
        Assert.IsTrue(fixedUpdated.SmartPagingSample!.IsValid);
    }
}
