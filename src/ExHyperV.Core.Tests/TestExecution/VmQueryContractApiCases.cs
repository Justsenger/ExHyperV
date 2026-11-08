using System.Management;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmQueryContractApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmQueryService.构造()" or "VmQueryService.Dispose()" or "VmQueryService.DisposeAsync()"
        or "VmQueryService.GetVmRuntimeMemoryDataAsync()" or "VmQueryService.GetVmConfigRootAsync()" or "VmQueryService.GetVmStateAsync()" or "VmQueryService.IsVmPoweredOffAsync()"
        || p.Id is "contract.inventory-failure" or "contract.inventory-issues" or "api.query-partial" or "T-2e465d73bfe57502-5" or "T-319692fabf672c00-2";

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api == "VmQueryService.构造()")
        {
            using var first = c.Call(() => new VmQueryService()); using var second = c.Call(() => new VmQueryService()); first.Dispose();
            var result = await second.QueryAsync(new([VmIdentityFields.Name], [])); c.Require(result.HasData && result.Data!.VirtualMachines.Count == 0, "释放第一个实例后第二个仍可查询"); return;
        }
        if (p.Api is "VmQueryService.Dispose()" or "VmQueryService.DisposeAsync()") { await LifetimeAsync(p, c); return; }
        await using var query = new VmQueryService();
        if (p.Id == "contract.inventory-failure")
        {
            using (WmiApi.UseQuerySource((_, _) => new()))
            { var empty = await c.CallAsync(query.GetVmListAsync); c.Require(empty.HasData && empty.Data!.VirtualMachines.Count == 0 && empty.Data.IsComplete, "受控空来源返回成功空清单"); }
            foreach (var component in new[] { "Msvm_SummaryInformation", "Msvm_MemorySettingData", "Msvm_StorageAllocationSettingData" })
            {
                using var fault = WmiApi.UseQuerySource((wql, _) => wql.Contains(component, StringComparison.Ordinal) ? Denied() : new());
                var failure = await c.CallAsync(query.GetVmListAsync); c.Require(!failure.Success && failure.Data is null, "必需来源失败传播：" + component); c.Equal(ApiErrorKind.AccessDenied, failure.ErrorKind, "权限失败分类");
            }
            c.Evidence.Add("本项通过作用域内的只读来源替换注入故障，调用的是公开 GetVmListAsync。"); return;
        }
        if (p.Id == "contract.inventory-issues")
        {
            foreach (var (source, name) in new[] { ("Msvm_GpuPartitionSettingData", "GpuAssignments"), ("Msvm_VirtualEthernetSwitch", "SwitchNames"), ("Msvm_GuestNetworkAdapterConfiguration", "GuestIpAddresses") })
            {
                using var fault = WmiApi.UseQuerySource((wql, _) => wql.Contains(source, StringComparison.Ordinal) ? Denied() : new());
                var partial = await c.CallAsync(query.GetVmListAsync); c.Require(partial.HasData && !partial.Data!.IsComplete, "可选来源失败保留成功快照及缺项");
                var issue = partial.Data!.Issues.Single(i => i.Component == name); c.Equal(ApiErrorKind.AccessDenied, issue.ErrorKind, "缺项类别"); c.Equal((int)ManagementStatus.AccessDenied, issue.Code, "原始 WMI 错误码");
            }
            c.Evidence.Add("受控可选来源失败，逐项调用公开清单接口并核对 Issues。"); return;
        }
        if (p.Id == "T-319692fabf672c00-2")
        {
            var off = new VmInstance(Guid.NewGuid(), "off") { StateCode = 3, HasGpuAssignment = true }; var noGpu = new VmInstance(Guid.NewGuid(), "no-gpu") { StateCode = 2, HasGpuAssignment = false };
            var values = await c.CallAsync(() => query.GetGpuPerformanceAsync([off, noGpu]));
            foreach (var vm in new[] { off, noGpu }) { c.Equal(PerformanceSampleState.NotApplicable, values[vm.Id].Sample!.State, "GPU 采样适用性"); c.Require(values[vm.Id].DriverSample?.IsValid != true, "缺测具备明确状态"); }
            return;
        }
        await using var owned = await TemporaryVm.CreateAsync(c);
        if (p.Id == "api.query-partial")
        {
            var request = new VmQueryRequest([VmIdentityFields.Name, VmMemoryFields.Startup], [owned.Id]);
            using (WmiApi.UseQuerySource((wql, _) => wql.Contains("Msvm_MemorySettingData", StringComparison.Ordinal) ? Denied() : null))
            {
                var partial = await c.CallAsync(() => query.QueryAsync(request)); c.Require(partial.HasData, "单来源失败保留其他字段");
                var row = partial.Data!.VirtualMachines.Single(); c.Equal(owned.Name, row.Get(VmIdentityFields.Name).Value, "可用名称保留"); c.Equal(VmQueryValueState.Failed, row.Get(VmMemoryFields.Startup).State, "内存失败状态");
            }
            using (WmiApi.UseQuerySource((wql, _) => wql.Contains("Msvm_SummaryInformation", StringComparison.Ordinal) ? Denied() : null))
            { var failure = await c.CallAsync(() => query.QueryAsync(request)); c.Require(!failure.Success && failure.Data is null, "必需身份来源失败阻止返回伪清单"); c.Equal(ApiErrorKind.AccessDenied, failure.ErrorKind, "身份枚举错误保留"); }
            return;
        }
        if (p.Api == "VmQueryService.GetVmConfigRootAsync()")
        {
            var result = await c.CallAsync(() => query.GetVmConfigRootAsync(owned.Id)); c.Require(result.HasData, "配置根路径：" + result.Error);
            c.Equal(SystemBaseline.Settings(owned.Id)["ConfigurationDataRoot"]?.ToString(), result.Data, "原生配置根路径"); return;
        }
        if (p.Id == "T-2e465d73bfe57502-5")
        {
            await TemporaryDisk.AttachAsync(owned); await WindowsTestCommand.RunAsync("$v=Get-VM -Id ([guid]$a.id);$v|Set-VMProcessor -Count 2;$v|Set-VMMemory -StartupBytes 768MB -DynamicMemoryEnabled $false;@{ok=$true}|ConvertTo-Json", new { id = owned.Id });
            await VmMetadataService.SetOperatingSystemTypeAsync(owned.Id, "Linux");
            var result = await c.CallAsync(query.GetVmListAsync); c.Require(result.HasData, "扩展清单：" + result.Error);
            var vm = result.Data!.VirtualMachines.Single(v => v.Id == owned.Id); var raw = SystemBaseline.Settings(owned.Id);
            c.Equal(2, vm.Generation, "代数"); c.Equal(raw["Version"]?.ToString(), vm.Version, "配置版本"); c.Equal(2, vm.CpuCount, "处理器数"); c.Equal(0.75, vm.MemoryGiB, "启动内存 GiB");
            c.Equal("Linux", vm.OsType, "系统分类元数据"); c.Equal(1, vm.Disks.Count, "磁盘数量"); c.Equal(0.5, vm.TotalDiskSizeGiB, "磁盘容量 GiB"); return;
        }
        foreach (var (action, native, state) in new[] { ("", (ushort)3, VmState.PoweredOff), ("Start-VM", (ushort)2, VmState.Running), ("Save-VM", (ushort)6, VmState.Saved) })
        {
            if (action != "") await WindowsTestCommand.RunAsync("$v=Get-VM -Id ([guid]$a.id);switch($a.action){'Start-VM'{$v|Start-VM};'Save-VM'{$v|Save-VM};default{throw '未知准备动作'}};@{ok=$true}|ConvertTo-Json", new { id = owned.Id, action }, 90);
            await SystemBaseline.WaitStateAsync(owned.Id, native);
            if (p.Api == "VmQueryService.GetVmStateAsync()")
            { var result = await c.CallAsync(() => query.GetVmStateAsync(owned.Id)); c.Require(result.HasData, "状态查询：" + result.Error); c.Equal(state, result.Data, "系统状态映射"); }
            else if (p.Api == "VmQueryService.IsVmPoweredOffAsync()")
            { var result = await c.CallAsync(() => query.IsVmPoweredOffAsync(owned.Id)); c.Require(result.HasData, "关机判定：" + result.Error); c.Equal(native == 3, result.Data, "仅完全关机为 true"); }
            else
            {
                var before = SystemBaseline.Query($"SELECT * FROM Msvm_SummaryInformation WHERE Name='{owned.Id:D}'").Single();
                var result = await c.CallAsync(query.GetVmRuntimeMemoryDataAsync); c.Require(result.HasData, "内存采样：" + result.Error); var sample = result.Data![owned.Id];
                var after = SystemBaseline.Query($"SELECT * FROM Msvm_SummaryInformation WHERE Name='{owned.Id:D}'").Single();
                if (native == 2)
                {
                    if (!Equals(before["MemoryUsage"], after["MemoryUsage"])) throw new ApiCheckSkippedException("内存采样窗口内分配量变化，需重采样。");
                    c.Require(sample.AssignedSample!.IsValid, "运行时分配量有效"); c.Equal(Convert.ToInt64(before["MemoryUsage"]), sample.AssignedMiB, "分配内存 MiB");
                    var counters = SystemBaseline.Query("SELECT Name,CurrentPressure,GuestVisiblePhysicalMemory FROM Win32_PerfFormattedData_BalancerStats_HyperVDynamicMemoryVM", "root/cimv2")
                        .Where(row => string.Equals(Convert.ToString(row["Name"]), owned.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    c.Require(counters.Length <= 1, "本项专属虚拟机具有唯一计数器身份");
                    c.Require(counters.Length == 0 || Convert.ToDecimal(counters[0]["CurrentPressure"] ?? 0) == 0
                        || Convert.ToInt64(counters[0]["GuestVisiblePhysicalMemory"] ?? 0) == 0,
                        "空白固件虚拟机尚未报告操作系统内存需求");
                    c.Equal(PerformanceSampleState.Unavailable, sample.AvailableSample!.State, "固定内存支持需求采样，缺少系统报告时标为缺测");
                    c.Require(!sample.AvailableSample.IsValid, "缺测占位值作为无效样本处理");
                }
                else c.Equal(PerformanceSampleState.NotApplicable, sample.AssignedSample!.State, "关机或保存状态标记");
            }
        }
    }
    private static WmiApi.QuerySubstitution Denied() => new(new(ApiErrorKind.AccessDenied, (int)ManagementStatus.AccessDenied, ApiErrorSource.Wmi, "注入的查询权限失败"));

    private static async Task LifetimeAsync(CatalogTest p, ApiCaseContext c)
    {
        bool asyncDispose = p.Api == "VmQueryService.DisposeAsync()";
        bool active = p.Id.EndsWith("-2", StringComparison.Ordinal);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(!active);
        Guid adapterId = Guid.NewGuid(); var vm = new VmInstance(Guid.NewGuid(), "fixture") { StateCode = 2 };
        vm.NetworkAdapters.Add(new() { Id = vm.Id + "\\" + adapterId });
        var counters = new List<Counter>();
        using var query = new VmQueryService(_ => true, _ => ["fixture-" + adapterId], (_, kind, instance) =>
        { var counter = new Counter(kind, instance, () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("计数器释放等待超时。"); }); counters.Add(counter); return counter; });
        async Task DisposeTarget()
        { if (asyncDispose) await c.CallAsync(async () => { await query.DisposeAsync(); return true; }); else c.Call(query.Dispose); }
        Task? sampling = null, closing = null;
        try
        {
            await query.UpdateNetworkPerformanceAsync([vm]);
            if (active)
            {
                sampling = Task.Run(() => query.UpdateNetworkPerformanceAsync([vm])); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                closing = Task.Run(DisposeTarget); await Task.Delay(100); c.Require(!closing.IsCompleted && counters.All(x => x.Releases == 0), "释放等待已接纳采样结束");
                release.Set(); await sampling.WaitAsync(TimeSpan.FromSeconds(10)); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else await DisposeTarget();
            await DisposeTarget(); c.Require(counters.Count > 0 && counters.All(counter => counter.Releases == 1), "重复释放保持每个计数器恰好释放一次");
            try { await query.UpdateNetworkPerformanceAsync([]); c.Require(false, "释放后应拒绝采样"); } catch (ObjectDisposedException) { c.Evidence.Add("释放后公开采样入口明确拒绝。"); }
        }
        finally { release.Set(); if (sampling is not null) await sampling; if (closing is not null) await closing; }
        c.Evidence.Add("本项使用受控计数器提供者核对真实公开释放入口的生命周期。");
    }
    private sealed class Counter(string kind, string instance, Action wait) : VmQueryService.IUsageCounter
    {
        public string InstanceName => instance; private int reads; internal int Releases;
        public float NextValue() { if (Interlocked.Increment(ref reads) == 2) wait(); return kind == "Bytes Sent/sec" ? 123 : 456; }
        public void Dispose() => Interlocked.Increment(ref Releases);
    }
}
