using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class HostCpuThreadMonitorTests
{
    [TestMethod]
    [TestCategory("OperationQuery")]
    public void EtwNativeStructs_MatchWindowsSdkBinaryLayouts()
    {
        int expectedTraceSize = IntPtr.Size == 8 ? 88 : 56;
        int expectedLogfileHeaderSize = IntPtr.Size == 8 ? 280 : 272;
        int expectedRecordSize = IntPtr.Size == 8 ? 112 : 100;

        Assert.AreEqual(48, Unsafe.SizeOf<HostCpuEtwNative.WNODE_HEADER>());
        Assert.AreEqual(120, Unsafe.SizeOf<HostCpuEtwNative.EVENT_TRACE_PROPERTIES>());
        Assert.AreEqual(48, Unsafe.SizeOf<HostCpuEtwNative.EVENT_TRACE_HEADER>());
        Assert.AreEqual(expectedTraceSize, Unsafe.SizeOf<HostCpuEtwNative.EVENT_TRACE>());
        Assert.AreEqual(172, Unsafe.SizeOf<HostCpuEtwNative.TIME_ZONE_INFORMATION>());
        Assert.AreEqual(expectedLogfileHeaderSize, Unsafe.SizeOf<HostCpuEtwNative.TRACE_LOGFILE_HEADER>());
        Assert.AreEqual(80, Unsafe.SizeOf<HostCpuEtwNative.EVENT_HEADER>());
        Assert.AreEqual(4, Unsafe.SizeOf<HostCpuEtwNative.ETW_BUFFER_CONTEXT>());
        Assert.AreEqual(expectedRecordSize, Unsafe.SizeOf<HostCpuEtwNative.EVENT_RECORD>());
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public void ResolveVmProcessMap_LinksWorkerVmmemAndTokenOwnerWhileExcludingNonHyperV()
    {
        var vm1 = Guid.NewGuid();
        var vm2 = Guid.NewGuid();
        var vms = new[]
        {
            new VmProcessDescriptor(vm1, "vm-one", true, 1000, null, null),
            new VmProcessDescriptor(vm2, "vm-two", true, 2000, null, null)
        };
        var processes = new Dictionary<int, ProcessSnapshotItem>
        {
            [1000] = new(1000, 500, "vmwp.exe"),
            [1001] = new(1001, 1000, "vmmem"),
            [2000] = new(2000, 500, "vmwp.exe"),
            [2001] = new(2001, 1, "vmmemCm"), // Reparented / decoupled PPID -> resolved via token SID
            [3001] = new(3001, 1, "vmmemWSL"), // Not a WMI VM -> stays Host
            [4000] = new(4000, 500, "chrome.exe")
        };

        var map = HostCpuThreadAggregator.ResolveVmProcessMap(
            vms,
            processes,
            pid => pid switch
            {
                2001 => vm2,
                3001 => Guid.NewGuid(),
                _ => null
            });

        Assert.AreEqual(vm1, map[1000]);
        Assert.AreEqual(vm1, map[1001]);
        Assert.AreEqual(vm2, map[2000]);
        Assert.AreEqual(vm2, map[2001]);
        Assert.IsFalse(map.ContainsKey(3001));
        Assert.IsFalse(map.ContainsKey(4000));
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public void RootKernelContextSwitch_AttributesConcurrentVmsAndNormalizesOverflow()
    {
        var vmA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var vmB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var topology = new LogicalProcessorTopologyItem[]
        {
            new(0, 0, 0, CpuCoreType.Efficient),
            new(1, 0, 1, CpuCoreType.Performance)
        };
        var vms = new VmProcessDescriptor[]
        {
            new(vmA, "alpha-vm", true, 100, null, null),
            new(vmB, "beta-vm", true, 200, null, null)
        };
        var processes = new Dictionary<int, ProcessSnapshotItem>
        {
            [100] = new(100, 10, "vmwp.exe"),
            [101] = new(101, 100, "vmmem"),
            [200] = new(200, 10, "vmwp.exe"),
            [201] = new(201, 200, "vmmem"),
            [0] = new(0, 0, "System Idle Process"),
            [500] = new(500, 1, "System")
        };
        var toolhelpThreads = new Dictionary<int, int>
        {
            [9001] = 201 // Late-resolved vmmem thread for vmB
        };

        // 10_000_000 ticks = 1.0 second window
        var window = new EtwHarvestWindow(
            StartQpc: 1_000_000,
            EndQpc: 11_000_000,
            QpcFrequency: 10_000_000,
            EventsLost: 0,
            RealTimeBuffersLost: 0,
            IdleTicksByLp: new Dictionary<int, long> { [0] = 2_000_000 },
            PidTicksByLp: new Dictionary<(int Lp, uint Pid), long>
            {
                [(0, 101)] = 5_000_000, // 50% vmA
                [(0, 500)] = 2_000_000, // 20% Host (unresolved + resolved = 30% vmB?) -> add via unresolved TID
                [(1, 101)] = 8_000_000, // Overflow test: 8M + 4M = 12M in 10M window!
                [(1, 201)] = 4_000_000
            },
            UnresolvedTidTicksByLp: new Dictionary<(int Lp, uint Tid), long>
            {
                [(0, 9001)] = 1_000_000, // Resolves via Toolhelp32 to PID 201 -> vmB (10%)
                [(0, 9999)] = 500_000
            },
            ObservedThreadToPid: new Dictionary<uint, uint>
            {
                [8001] = 101
            });

        var now = DateTimeOffset.UtcNow;
        var report = HostCpuThreadAggregator.BuildFromKernelContextSwitch(
            HyperVSchedulerType.Root,
            topology,
            vms,
            processes,
            toolhelpThreads,
            window,
            now);

        Assert.IsTrue(report.Sample.IsValid);
        Assert.AreEqual(HostCpuAttributionSource.KernelContextSwitch, report.AttributionSource);
        Assert.AreEqual(HyperVSchedulerType.Root, report.SchedulerType);
        Assert.AreEqual(TimeSpan.FromSeconds(1), report.WindowDuration);
        Assert.HasCount(2, report.Threads);

        var lost = HostCpuThreadAggregator.BuildFromKernelContextSwitch(HyperVSchedulerType.Root,
            topology, vms, processes, toolhelpThreads, window with { EventsLost = 1 }, now);
        Assert.AreEqual(PerformanceSampleState.Unavailable, lost.Sample.State);
        Assert.IsTrue(lost.Threads.All(lp => !lp.Sample.IsValid));

        var lp0 = report.Threads[0];
        Assert.AreEqual(5f, lp0.UnattributedUsagePercent, 0.05f);
        Assert.AreEqual(85f, lp0.TotalUsagePercent, 0.05f);
        Assert.AreEqual(20f, lp0.HostUsagePercent!.Value, 0.05f);
        Assert.AreEqual(0f, lp0.UnattributedGuestUsagePercent);
        Assert.AreEqual(50f, lp0.Consumers.Single(c => c.VmId == vmA).UsagePercent, 0.05f);
        Assert.AreEqual(10f, lp0.Consumers.Single(c => c.VmId == vmB).UsagePercent, 0.05f);
        Assert.AreEqual(20f, lp0.Consumers.Single(c => c.IsHost).UsagePercent, 0.05f);

        // Overflow on LP1 must be normalized strictly <= 100% maintaining proportions (2/3 vmA, 1/3 vmB)
        var lp1 = report.Threads[1];
        Assert.IsLessThanOrEqualTo(100f, lp1.TotalUsagePercent);
        Assert.AreEqual(100f, lp1.TotalUsagePercent, 0.05f);
        Assert.AreEqual(66.67f, lp1.Consumers.Single(c => c.VmId == vmA).UsagePercent, 0.2f);
        Assert.AreEqual(33.33f, lp1.Consumers.Single(c => c.VmId == vmB).UsagePercent, 0.2f);
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    [DataRow(HyperVSchedulerType.Classic)]
    [DataRow(HyperVSchedulerType.Core)]
    public void ClassicAndCoreScheduler_HandlesIdleSingleDisjointAndSharedVmsHonestly(HyperVSchedulerType scheduler)
    {
        var now = DateTimeOffset.UtcNow;
        var topology = Enumerable.Range(0, 4)
            .Select(i => new LogicalProcessorTopologyItem(i, 0, i, CpuCoreType.Unknown))
            .ToArray();
        var lpSamples = new LogicalProcessorCounterSample[]
        {
            new(0, 60f, 15f, new(PerformanceSampleState.Valid, now)),
            new(1, 30f, 30f, new(PerformanceSampleState.Valid, now)),
            new(2, 85f, 25f, new(PerformanceSampleState.Valid, now)),
            new(3, 50f, 10f, new(PerformanceSampleState.Valid, now))
        };

        // 1) Idle host: 0 VMs -> all total belongs to Host
        var idleReport = HostCpuThreadAggregator.BuildFromCounters(
            scheduler, topology, lpSamples, [], now, TimeSpan.FromSeconds(1),
            new(PerformanceSampleState.Valid, now), attributionNote: null);
        Assert.AreEqual(HostCpuAttributionSource.PerformanceCounter, idleReport.AttributionSource);
        Assert.IsTrue(idleReport.Threads.All(t => Math.Abs(t.HostUsagePercent.GetValueOrDefault() - t.TotalUsagePercent) < 0.01f && t.UnattributedGuestUsagePercent == 0f));

        // 2) Single active VM: all (HvLP - RootVP) on every LP attributed to that single VM
        var vm1 = Guid.NewGuid();
        var singleVm = new VmProcessDescriptor(vm1, "classic-single", true, null, null, null);
        var singleReport = HostCpuThreadAggregator.BuildFromCounters(
            scheduler, topology, lpSamples, [singleVm], now, TimeSpan.FromSeconds(1),
            new(PerformanceSampleState.Valid, now), attributionNote: null);
        Assert.AreEqual(45f, singleReport.Threads[0].Consumers.Single(c => c.VmId == vm1).UsagePercent, 0.05f);
        Assert.AreEqual(15f, singleReport.Threads[0].HostUsagePercent!.Value, 0.05f);
        Assert.AreEqual(0f, singleReport.Threads[0].UnattributedGuestUsagePercent);
        Assert.IsFalse(singleReport.Threads[1].Consumers.Any(c => !c.IsHost));

        // 3) Two VMs in disjoint CPU groups: VM1 on {0,1}, VM2 on {2,3}
        var vm2 = Guid.NewGuid();
        var groupVm1 = new VmProcessDescriptor(vm1, "group-a", true, null, new HashSet<int> { 0, 1 }, null);
        var groupVm2 = new VmProcessDescriptor(vm2, "group-b", true, null, new HashSet<int> { 2, 3 }, null);
        var disjointReport = HostCpuThreadAggregator.BuildFromCounters(
            scheduler, topology, lpSamples, [groupVm1, groupVm2], now, TimeSpan.FromSeconds(1),
            new(PerformanceSampleState.Valid, now), attributionNote: null);
        Assert.AreEqual(45f, disjointReport.Threads[0].Consumers.Single(c => c.VmId == vm1).UsagePercent, 0.05f);
        Assert.AreEqual(60f, disjointReport.Threads[2].Consumers.Single(c => c.VmId == vm2).UsagePercent, 0.05f);
        Assert.IsTrue(disjointReport.Threads.All(t => t.UnattributedGuestUsagePercent == 0f));

        // 4) Two VMs sharing all LPs (unconstrained): zero fabricated per-VM LP shares, guest residual goes to UnattributedGuest
        var sharedVm1 = new VmProcessDescriptor(vm1, "shared-a", true, null, null, null);
        var sharedVm2 = new VmProcessDescriptor(vm2, "shared-b", true, null, null, null);
        var sharedReport = HostCpuThreadAggregator.BuildFromCounters(
            scheduler, topology, lpSamples, [sharedVm1, sharedVm2], now, TimeSpan.FromSeconds(1),
            new(PerformanceSampleState.Valid, now), attributionNote: null);
        Assert.IsFalse(sharedReport.Threads.SelectMany(t => t.Consumers).Any(c => !c.IsHost));
        Assert.AreEqual(45f, sharedReport.Threads[0].UnattributedGuestUsagePercent, 0.05f);
        Assert.AreEqual(15f, sharedReport.Threads[0].HostUsagePercent!.Value, 0.05f);
        Assert.AreEqual(60f, sharedReport.Threads[0].TotalUsagePercent, 0.05f);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sharedReport.AttributionNote));
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public void ClassicScheduler_MissingRootVpWithActiveVms_PreservesUnattributedGuestSafely()
    {
        var now = DateTimeOffset.UtcNow;
        var topology = new LogicalProcessorTopologyItem[] { new(0, 0, 0, CpuCoreType.Unknown) };
        var samples = new LogicalProcessorCounterSample[]
        {
            new(0, 42f, null, new(PerformanceSampleState.Valid, now))
        };
        var vm = new VmProcessDescriptor(Guid.NewGuid(), "vm-classic", true, null, null, null);

        var report = HostCpuThreadAggregator.BuildFromCounters(
            HyperVSchedulerType.Classic, topology, samples, [vm], now, TimeSpan.FromSeconds(1),
            new(PerformanceSampleState.Valid, now), attributionNote: null);

        Assert.AreEqual(42f, report.Threads[0].TotalUsagePercent, 0.05f);
        Assert.IsNull(report.Threads[0].HostUsagePercent);
        Assert.AreEqual(0f, report.Threads[0].UnattributedGuestUsagePercent);
        Assert.AreEqual(42f, report.Threads[0].UnattributedUsagePercent, 0.05f);
        StringAssert.Contains(report.AttributionNote, "Root Virtual Processor");
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public void MultiGroupTopology_SupportsMoreThan64LogicalProcessorsAcrossGroups()
    {
        // 72 LPs: 48 in Group 0, 24 in Group 1
        var topology = new List<LogicalProcessorTopologyItem>(72);
        for (int i = 0; i < 48; i++) topology.Add(new(i, 0, i, CpuCoreType.Performance));
        for (int i = 0; i < 24; i++) topology.Add(new(48 + i, 1, i, CpuCoreType.Efficient));

        var vmId = Guid.NewGuid();
        var vms = new[] { new VmProcessDescriptor(vmId, "big-vm", true, 5000, null, null) };
        var procs = new Dictionary<int, ProcessSnapshotItem>
        {
            [5000] = new(5000, 1, "vmwp.exe"),
            [5001] = new(5001, 5000, "vmmem")
        };
        var window = new EtwHarvestWindow(
            0, 10_000_000, 10_000_000, 0, 0,
            new Dictionary<int, long>(),
            new Dictionary<(int Lp, uint Pid), long> { [(71, 5001)] = 7_500_000 },
            new Dictionary<(int Lp, uint Tid), long>(),
            new Dictionary<uint, uint>());

        var report = HostCpuThreadAggregator.BuildFromKernelContextSwitch(
            HyperVSchedulerType.Root, topology, vms, procs, new Dictionary<int, int>(), window, DateTimeOffset.UtcNow);

        Assert.HasCount(72, report.Threads);
        var last = report.Threads[71];
        Assert.AreEqual(71, last.LogicalProcessorIndex);
        Assert.AreEqual(1, last.ProcessorGroup);
        Assert.AreEqual(23, last.GroupProcessorIndex);
        Assert.AreEqual(CpuCoreType.Efficient, last.CoreType);
        Assert.AreEqual(75f, last.TotalUsagePercent, 0.05f);
        Assert.AreEqual(75f, last.Consumers.Single(c => c.VmId == vmId).UsagePercent, 0.05f);
    }

    [TestMethod]
    [TestCategory("OperationResource")]
    public async Task RootAccessDeniedFallback_AndLifecycleDisposalWorkCleanly()
    {
        var released = new ConcurrentBag<string>();
        var vmId = Guid.NewGuid();
        int step = 0;
        var baseTime = DateTimeOffset.UtcNow;

        var backend = new HostCpuThreadMonitorService.Backend(
            ReadSchedulerType: () => HyperVSchedulerType.Root,
            ReadTopology: () => [new(0, 0, 0, CpuCoreType.Performance)],
            StartKernelTrace: _ => (null, 5, "Access is denied."),
            SnapshotProcesses: () => new ProcessThreadSnapshot(new Dictionary<int, ProcessSnapshotItem>(), new Dictionary<int, int>()),
            TryReadVmTokenOwner: _ => null,
            ReadVirtualMachines: _ => Task.FromResult<IReadOnlyList<VmProcessDescriptor>?>([new(vmId, "root-vm", true, null, null, null)]),
            EnumerateCounterInstances: cat => cat.EndsWith("Logical Processor", StringComparison.Ordinal) ? ["Hv LP 0"] : ["Root VP 0"],
            CreateCounter: (cat, _, inst) =>
            {
                float val = inst == "Hv LP 0" ? 55f : 15f;
                return new HostCpuThreadMonitorService.CounterHandle(() => val, () => released.Add(cat));
            },
            Delay: async (_, ct) => await Task.Delay(25, ct),
            UtcNow: () => baseTime.AddSeconds(Interlocked.Increment(ref step)));

        await using var monitor = new HostCpuThreadMonitorService(backend);
        await monitor.WaitForInitialSnapshotAsync(CancellationToken.None);

        var warming = monitor.GetThreadUsage();
        Assert.AreEqual(HostCpuAttributionSource.PerformanceCounter, warming.AttributionSource);
        StringAssert.Contains(warming.AttributionNote, "错误码 5");
        Assert.AreEqual(PerformanceSampleState.WarmingUp, warming.Threads[0].Sample.State);

        var valid = monitor.GetThreadUsage();
        Assert.IsTrue(valid.Sample.IsValid);
        Assert.AreEqual(55f, valid.Threads[0].TotalUsagePercent, 0.05f);
        Assert.AreEqual(15f, valid.Threads[0].HostUsagePercent!.Value, 0.05f);
        Assert.AreEqual(40f, valid.Threads[0].Consumers.Single(c => c.VmId == vmId).UsagePercent, 0.05f);

        await monitor.DisposeAsync();
        monitor.Dispose();
        Assert.HasCount(2, released);
        Assert.ThrowsExactly<ObjectDisposedException>(() => monitor.GetThreadUsage());
    }

    [TestMethod]
    [TestCategory("OperationQuery")]
    public async Task SampleAsync_RejectsOutOfRangeDuration()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            HostCpuThreadMonitorService.SampleAsync(TimeSpan.Zero));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            HostCpuThreadMonitorService.SampleAsync(TimeSpan.FromMilliseconds(-50)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            HostCpuThreadMonitorService.SampleAsync(TimeSpan.FromMinutes(2)));
    }
}
