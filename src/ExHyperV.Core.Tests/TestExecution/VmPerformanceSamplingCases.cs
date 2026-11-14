using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmPerformanceSamplingCases
{
    internal static async Task ReadExistingAsync(CatalogTest p, ApiCaseContext c)
    {
        bool network = p.Api.Contains("Network", StringComparison.Ordinal);
        await using var query = new VmQueryService();
        var inventory = await query.GetVmListAsync();
        c.Require(inventory.HasData, inventory.Error);
        var machines = inventory.Data!.VirtualMachines.ToArray();
        int count = machines.Sum(vm => network ? vm.NetworkAdapters.Count : vm.Disks.Count);
        if (count == 0) throw new ApiCheckSkippedException(network ? "现有虚拟机没有网卡" : "现有虚拟机没有磁盘");
        await SampleAsync();
        await Task.Delay(1100, c.Token);
        var started = DateTimeOffset.UtcNow;
        await SampleAsync();
        var samples = machines.SelectMany(vm => network
            ? vm.NetworkAdapters.Select(a => (a.PerformanceSample, (double)a.SendBytesPerSecond, (double)a.ReceiveBytesPerSecond))
            : vm.Disks.Select(d => (d.PerformanceSample, (double)d.ReadBytesPerSecond, (double)d.WriteBytesPerSecond))).ToArray();
        foreach (var (sample, first, second) in samples)
        {
            c.Require(sample.ObservedAtUtc >= started, "每个设备记录本轮采样时间");
            c.Require(sample.State != PerformanceSampleState.Failed, "系统采样成功：" + sample.Error);
            c.Require(sample.State != PerformanceSampleState.NotSampled, "每个设备具有明确的采样状态");
            if (sample.IsValid) c.Require(double.IsFinite(first) && double.IsFinite(second) && first >= 0 && second >= 0, "有效速率为有限非负数，单位为字节/秒");
        }
        c.Evidence.Add(string.Join("；", samples.GroupBy(s => s.PerformanceSample.State).Select(g => $"{g.Key}：{g.Count()}")));
        if (samples.All(s => !s.PerformanceSample.IsValid))
            throw new ApiCheckSkippedException("本轮缺少有效速率样本：" + string.Join("、", samples.Select(s => s.PerformanceSample.State).Distinct()));

        async Task SampleAsync() => await c.CallAsync(async () =>
        {
            if (network) await query.UpdateNetworkPerformanceAsync(machines);
            else await query.UpdateDiskPerformanceAsync(machines);
            return true;
        });
    }

    internal static Task BoundaryAsync(CatalogTest p, ApiCaseContext c) => p.Api.Contains("Network", StringComparison.Ordinal)
        ? NetworkBoundaryAsync(c) : DiskBoundaryAsync(c);

    private static async Task DiskBoundaryAsync(ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        string path = Path.Combine(vm.Root, "performance.vhdx");
        await WindowsTestCommand.RunAsync("New-VHD -Path $a.path -SizeBytes 64MB -Dynamic|Out-Null;Get-VM -Id $a.id|Add-VMHardDiskDrive -Path $a.path;@{ok=$true}|ConvertTo-Json", new { id = vm.Id, path });
        await using var query = new VmQueryService();
        var inventory = await query.GetVmListAsync();
        c.Require(inventory.HasData, inventory.Error);
        var model = inventory.Data!.VirtualMachines.Single(v => v.Id == vm.Id);
        var disk = model.Disks.Single(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        await SampleAsync();
        c.Equal(PerformanceSampleState.NotApplicable, disk.PerformanceSample.State, "关机磁盘标记为非活动");
        await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Get-VMHardDiskDrive|Remove-VMHardDiskDrive;@{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        // Retain an old caller snapshot to verify that removed identities cannot regain a valid sample.
        model.StateCode = 2;
        await SampleAsync();
        if (disk.PerformanceSample.State == PerformanceSampleState.Unsupported)
            throw new ApiCheckSkippedException("此系统缺少虚拟磁盘性能计数器：" + disk.PerformanceSample.Error);
        c.Equal(PerformanceSampleState.Unavailable, disk.PerformanceSample.State, "磁盘移除后，旧模型获得缺测状态");
        async Task SampleAsync() => await c.CallAsync(async () => { await query.UpdateDiskPerformanceAsync([model]); return true; });
    }

    private static async Task NetworkBoundaryAsync(ApiCaseContext c)
    {
        var model = new VmInstance { Id = Guid.NewGuid(), StateCode = 2 };
        Guid adapterId = Guid.NewGuid();
        var adapter = new VmNetworkAdapter { Name = "采样网卡", Id = $"{model.Id}\\{adapterId}" };
        model.NetworkAdapters.Add(adapter);
        string[] instances = [$"fixture-{adapterId}"];
        float sent = 128, received = 256;
        var counters = new List<Counter>();
        await using var query = new VmQueryService(_ => true, _ => instances, (_, kind, instance) =>
        {
            var counter = new Counter(instance, () => kind == "Bytes Sent/sec" ? sent : received);
            counters.Add(counter); return counter;
        });
        c.Evidence.Add("注入固定网卡计数器，实际调用 Core 公开采样 API；宿主设备保持原状态。");
        await SampleAsync();
        c.Equal(PerformanceSampleState.WarmingUp, adapter.PerformanceSample.State, "首次采样预热");
        await SampleAsync();
        c.Equal(PerformanceSampleState.Valid, adapter.PerformanceSample.State, "第二轮具有有效样本");
        c.Equal(128d, adapter.SendBytesPerSecond, "发送字节/秒按计数器原值返回");
        c.Equal(256d, adapter.ReceiveBytesPerSecond, "接收字节/秒按计数器原值返回");
        sent = received = 0;
        await SampleAsync();
        c.Equal(PerformanceSampleState.Valid, adapter.PerformanceSample.State, "空闲零速率仍是有效样本");
        c.Equal(0d, adapter.SendBytesPerSecond + adapter.ReceiveBytesPerSecond, "空闲速率为零");
        instances = [];
        await SampleAsync();
        c.Equal(PerformanceSampleState.Unavailable, adapter.PerformanceSample.State, "计数器移除后明确缺测");
        var oldCounters = counters.ToArray();
        Guid replacementId = Guid.NewGuid();
        var replacement = new VmNetworkAdapter { Name = adapter.Name, Id = $"{model.Id}\\{replacementId}" };
        model.NetworkAdapters.Clear(); model.NetworkAdapters.Add(replacement);
        instances = [$"fixture-{replacementId}"];
        sent = 512; received = 1024;
        await SampleAsync();
        c.Equal(PerformanceSampleState.WarmingUp, replacement.PerformanceSample.State, "同名新身份重新预热");
        c.Require(oldCounters.All(counter => counter.Disposals == 1), "旧身份计数器各释放一次");
        await SampleAsync();
        c.Equal(PerformanceSampleState.Valid, replacement.PerformanceSample.State, "新身份获得自己的样本");
        c.Equal(512d, replacement.SendBytesPerSecond, "新身份发送速率");
        c.Equal(1024d, replacement.ReceiveBytesPerSecond, "新身份接收速率");
        await query.DisposeAsync();
        c.Require(counters.All(counter => counter.Disposals == 1), "所有计数器最终各释放一次");
        async Task SampleAsync() => await c.CallAsync(async () => { await query.UpdateNetworkPerformanceAsync([model]); return true; });
    }

    private sealed class Counter(string instance, Func<float> read) : VmQueryService.IUsageCounter
    {
        public string InstanceName => instance;
        public int Disposals { get; private set; }
        public float NextValue() => Disposals == 0 ? read() : throw new ObjectDisposedException(nameof(Counter));
        public void Dispose() => Disposals++;
    }
}
