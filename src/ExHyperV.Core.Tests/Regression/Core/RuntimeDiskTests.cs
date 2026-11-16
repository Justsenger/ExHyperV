using System;
using System.Collections.Generic;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class RuntimeDiskTests
{
    [TestMethod]
    public void ApplyDiskPerformance_PopulatesThroughputIopsQueueAndErrorCount()
    {
        var diskPath = @"C:\VMs\system.vhdx";
        var wmiName = "C:-VMs-system.vhdx";
        var disk = new VmDiskStatus
        {
            Name = "system.vhdx",
            Path = diskPath,
            DiskType = "Virtual"
        };
        var vm = new VmInstance
        {
            Id = Guid.NewGuid(),
            Name = "vm-disk-test",
            StateCode = 2
        };
        vm.Disks.Add(disk);

        var observedAt = new DateTimeOffset(2026, 11, 15, 4, 0, 0, TimeSpan.Zero);
        VmQueryService.PerfItem[] samples =
        [
            new(wmiName, 1_048_576UL, 524_288UL, 142UL, 38UL, 3UL, 2UL)
        ];

        VmQueryService.ApplyDiskPerformance([vm], samples, [diskPath], observedAt);

        Assert.AreEqual(PerformanceSampleState.Valid, disk.PerformanceSample.State);
        Assert.AreEqual(observedAt, disk.PerformanceSample.ObservedAtUtc);
        Assert.AreEqual(1_048_576L, disk.ReadBytesPerSecond);
        Assert.AreEqual(524_288L, disk.WriteBytesPerSecond);
        Assert.AreEqual(142L, disk.ReadOperationsPerSecond);
        Assert.AreEqual(38L, disk.WriteOperationsPerSecond);
        Assert.AreEqual(3L, disk.QueueLength);
        Assert.AreEqual(2L, disk.ErrorCount);
    }

    [TestMethod]
    public void ApplyDiskPerformance_HandlesThreeArgSampleAndRejectsOverflowedThroughput()
    {
        var diskPath = @"D:\Hyper-V\data.vhdx";
        var wmiName = "D:-Hyper-V-data.vhdx";
        var disk = new VmDiskStatus
        {
            Name = "data.vhdx",
            Path = diskPath,
            DiskType = "Virtual",
            ReadOperationsPerSecond = 99,
            QueueLength = 4
        };
        var vm = new VmInstance
        {
            Id = Guid.NewGuid(),
            Name = "vm-disk-fallback",
            StateCode = 2
        };
        vm.Disks.Add(disk);

        var observedAt = new DateTimeOffset(2026, 11, 15, 4, 1, 0, TimeSpan.Zero);
        VmQueryService.ApplyDiskPerformance([vm], [new VmQueryService.PerfItem(wmiName, 2048UL, 4096UL)], [diskPath], observedAt);

        Assert.AreEqual(PerformanceSampleState.Valid, disk.PerformanceSample.State);
        Assert.AreEqual(2048L, disk.ReadBytesPerSecond);
        Assert.AreEqual(4096L, disk.WriteBytesPerSecond);
        Assert.AreEqual(0L, disk.ReadOperationsPerSecond);
        Assert.AreEqual(0L, disk.WriteOperationsPerSecond);
        Assert.AreEqual(0L, disk.QueueLength);
        Assert.AreEqual(0L, disk.ErrorCount);

        VmQueryService.ApplyDiskPerformance([vm], [new VmQueryService.PerfItem(wmiName, (ulong)long.MaxValue + 1, 0UL)], [diskPath], observedAt);
        Assert.AreEqual(PerformanceSampleState.Unavailable, disk.PerformanceSample.State);
    }

    [TestMethod]
    public void VmStorageFields_ExposesNewDiskPerfFields()
    {
        IReadOnlyList<VmQueryField> items = VmStorageFields.Items;
        CollectionAssert.Contains((System.Collections.ICollection)items, VmStorageFields.ReadOperationsPerSecond);
        CollectionAssert.Contains((System.Collections.ICollection)items, VmStorageFields.WriteOperationsPerSecond);
        CollectionAssert.Contains((System.Collections.ICollection)items, VmStorageFields.QueueLength);
        CollectionAssert.Contains((System.Collections.ICollection)items, VmStorageFields.ErrorCount);
    }
}
