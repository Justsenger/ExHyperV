using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostCpuThreadMonitorApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api.StartsWith("HostCpuThreadMonitorService.", StringComparison.Ordinal);

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api.Contains("Dispose"))
        {
            var releasedTrace = false;
            var trace = new StubKernelTrace(() => releasedTrace = true);
            var disposeMonitor = new HostCpuThreadMonitorService(CreateStubBackend(HyperVSchedulerType.Root, trace));
            await disposeMonitor.WaitForInitialSnapshotAsync(c.Token);
            if (p.Api.Contains("DisposeAsync"))
                await c.CallAsync(async () => { await disposeMonitor.DisposeAsync(); return true; });
            else
                c.Call(disposeMonitor.Dispose);
            c.Require(releasedTrace, "释放后底层内核跟踪会话被释放");
            c.Throws<ObjectDisposedException>(() => disposeMonitor.GetThreadUsage(), "已释放实例拒绝再次采样");
            return;
        }

        const string lpCategory = "Hyper-V Hypervisor Logical Processor";
        if (p.Api.Contains("构造"))
        {
            await using var service = c.Call(() => new HostCpuThreadMonitorService());
            await service.WaitForInitialSnapshotAsync(c.Token);
            var report = service.GetThreadUsage();
            c.Require(report.Threads.Count == HostCpuEtwNative.ReadProcessorTopology().Count, "构造后返回与宿主一致的物理逻辑处理器总数");
            return;
        }

        if (!PerformanceCounterCategory.Exists(lpCategory))
            throw new ApiCheckSkippedException("宿主缺少 Hyper-V 逻辑处理器性能计数器类别。");

        if (p.Api.Contains("SampleAsync"))
        {
            await using var vm = await TemporaryVm.CreateAsync(c);
            await vm.StartAsync();
            await Task.Delay(800, c.Token);
            var report = await c.CallAsync(() => HostCpuThreadMonitorService.SampleAsync(TimeSpan.FromMilliseconds(1200), c.Token));
            ValidateReportInvariants(c, report, vm.Id);
            c.Evidence.Add($"SampleAsync 调度器={report.SchedulerType} 来源={report.AttributionSource} 备注={report.AttributionNote}");
            return;
        }

        // GetThreadUsage continuous monitoring across idle -> load / active VM
        await using var monitor = new HostCpuThreadMonitorService();
        await monitor.WaitForInitialSnapshotAsync(c.Token);
        var warmup = c.Call(monitor.GetThreadUsage);
        c.Require(warmup.Threads.Count > 0, "返回宿主活动逻辑处理器列表");
        await Task.Delay(1100, c.Token);
        var idle = c.Call(monitor.GetThreadUsage);
        c.Require(idle.Sample.IsValid, "预热窗口后获得有效样本：" + idle.Sample.Error);
        foreach (var thread in idle.Threads)
        {
            c.Require(thread.TotalUsagePercent is >= 0f and <= 100f, $"LP {thread.LogicalProcessorIndex} 总使用率在 [0,100]");
            c.Require((thread.HostUsagePercent ?? 0f) + thread.UnattributedUsagePercent + thread.Consumers.Where(x => !x.IsHost).Sum(x => x.UsagePercent) + thread.UnattributedGuestUsagePercent <= 100.05f,
                $"LP {thread.LogicalProcessorIndex} 各占用主体严格归一不超过 100%");
        }
        c.Evidence.Add($"GetThreadUsage 调度器={idle.SchedulerType} 来源={idle.AttributionSource} 核心数={idle.Threads.Count}");
    }

    private static void ValidateReportInvariants(ApiCaseContext c, HostCpuThreadReport report, Guid expectedVmId)
    {
        c.Require(report.Sample.IsValid, "采样状态有效：" + report.Sample.Error);
        c.Require(report.Threads.Count == HostCpuEtwNative.ReadProcessorTopology().Count, "报告包含全体宿主物理逻辑处理器");
        foreach (var thread in report.Threads)
        {
            c.Require(thread.TotalUsagePercent is >= 0f and <= 100f, $"LP {thread.LogicalProcessorIndex} 总占用合法");
            float sum = (thread.HostUsagePercent ?? 0f) + thread.UnattributedUsagePercent
                + thread.Consumers.Where(x => !x.IsHost).Sum(x => x.UsagePercent)
                + thread.UnattributedGuestUsagePercent;
            c.Require(sum <= 100.05f, $"LP {thread.LogicalProcessorIndex} 占比和不超过 100%");
        }
        c.Require(report.Threads.Sum(t => t.TotalUsagePercent) > 0f, "运行态虚拟机期间宿主 CPU 总占用非零");
        if (report.AttributionSource == HostCpuAttributionSource.KernelContextSwitch)
        {
            c.Require(report.Threads.All(t => t.UnattributedGuestUsagePercent == 0f), "ETW 精确模式下未细分来宾占比恒为零");
            var vmTotal = report.Threads.SelectMany(t => t.Consumers).Where(x => x.VmId == expectedVmId).Sum(x => x.UsagePercent);
            c.Evidence.Add($"Root ETW 精确捕获临时虚拟机 {expectedVmId} 合计逻辑处理器占比={vmTotal:F2}%");
        }
    }

    private static HostCpuThreadMonitorService.Backend CreateStubBackend(HyperVSchedulerType scheduler, IKernelThreadTrace trace) => new(
        () => scheduler,
        () => [new(0, 0, 0, CpuCoreType.Unknown)],
        _ => (trace, 0, null),
        () => new ProcessThreadSnapshot(new Dictionary<int, ProcessSnapshotItem>(), new Dictionary<int, int>()),
        _ => null,
        _ => Task.FromResult<IReadOnlyList<VmProcessDescriptor>?>([]),
        _ => [],
        (_, _, _) => new HostCpuThreadMonitorService.CounterHandle(() => 0f, () => { }),
        (d, ct) => Task.Delay(20, ct),
        () => DateTimeOffset.UtcNow);

    private sealed class StubKernelTrace(Action onDispose) : IKernelThreadTrace
    {
        public bool ProcessTopologyDirty => false;
        public void ClearProcessTopologyDirty() { }
        public void SeedThreads(IReadOnlyDictionary<int, int> threadToPid) { }
        public EtwHarvestWindow HarvestWindow() => new(0, 10_000_000, 10_000_000, 0, 0,
            new Dictionary<int, long>(), new Dictionary<(int, uint), long>(), new Dictionary<(int, uint), long>(), new Dictionary<uint, uint>());
        public void Dispose() => onDispose();
    }
}
