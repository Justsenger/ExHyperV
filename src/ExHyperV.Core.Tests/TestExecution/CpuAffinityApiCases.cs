using System.Diagnostics;
using System.Runtime.InteropServices;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class CpuAffinityApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "CpuAffinityService.ApplySavedAffinityAfterStartAsync()" or "CpuAffinityService.GetCpuAffinityAsync()"
        or "CpuAffinityService.ReadStateAsync()" or "CpuAffinityService.SaveAsync()" or "HostCpuGroupService.GetAllAsync()" or "HostCpuGroupService.GetAsync()"
        or "HostCpuGroupService.SetCpuCapAsync()" or "HostCpuGroupService.SetIdleProcessorReserveAsync()" or "HostCpuGroupService.SetPriorityAsync()"
        or "HostCpuGroupService.SetRelativeWeightAsync()" or "HostProcessorService.GetAffinityProcessorsAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        var mode = HyperVSchedulerService.GetSchedulerType();
        if (p.Api == "HostProcessorService.GetAffinityProcessorsAsync()")
        {
            var actual = await c.CallAsync(HostProcessorService.GetAffinityProcessorsAsync);
            if (mode == HyperVSchedulerType.Unknown || mode == HyperVSchedulerType.Root && GetActiveProcessorGroupCount() != 1)
            { c.Require(!actual.Success && actual.ErrorKind == ApiErrorKind.Unsupported, "未知后端或 Root 多组返回 Unsupported"); return; }
            c.Require(actual.HasData, actual.Error);
            if (mode == HyperVSchedulerType.Root) c.Sequence(Enumerable.Range(0, checked((int)GetActiveProcessorCount(0))), actual.Data!.Select(v => v.Index), "Windows 活动处理器编号");
            else { var raw = await HcsApi.GetProcessorTopologyAsync(); c.Require(raw.HasData, raw.Error); c.Sequence(raw.Data!.OrderBy(v => v.LpIndex).Select(v => (v.LpIndex, v.CoreId, v.PackageId, (uint)v.NodeNumber)), actual.Data!.Select(v => ((uint)v.Index, v.CoreId!.Value, v.PackageId!.Value, v.NumaNode!.Value)), "HCS 编号及拓扑逐项对照"); }
            return;
        }
        if (p.Api.StartsWith("HostCpuGroupService.")) { await GroupsAsync(p, c, mode); return; }
        if (mode == HyperVSchedulerType.Unknown) throw new ApiCheckSkippedException("当前运行期调度器未知，亲和性后端尚无可用依据。");
        var processors = await HostProcessorService.GetAffinityProcessorsAsync();
        if (!processors.HasData) throw new ApiCheckSkippedException("亲和性拓扑暂不可用：" + processors.Error);
        int[] selected = processors.Data!.Select(v => v.Index).Take(Math.Max(1, processors.Data!.Count / 2)).ToArray();
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (p.Id == "T-e1b65193766f3eec-2")
        {
            var saved = await VmMetadataService.SetAsync(vm.Id, VmMetadataService.CpuAffinityKey, "{invalid-json"); c.Require(saved.Success, saved.Error);
            var state = await c.CallAsync(() => CpuAffinityService.ReadStateAsync(vm.Id)); c.Require(state.HasData, state.Error);
            c.Equal(CpuAffinityService.PreferenceStatus.InvalidConfiguration, state.Data!.SavedPreferenceStatus, "坏元数据的单独诊断");
            if (mode == HyperVSchedulerType.Root)
            { await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Start-VM; @{ok=$true}|ConvertTo-Json", new { id = vm.Id }); await SystemBaseline.WaitStateAsync(vm.Id, 2); }
            var actual = await c.CallAsync(() => CpuAffinityService.GetCpuAffinityAsync(vm.Id));
            c.Require(mode == HyperVSchedulerType.Root ? actual.Count > 0 : actual.Count == 0, "实际亲和性独立于损坏的保存偏好"); return;
        }
        var result = await c.CallAsync(() => CpuAffinityService.SaveAsync(vm.Id, selected.ToList()));
        Track(c, result); c.Require(result.Status != CpuAffinityService.SaveStatus.Failed && result.MetadataSaved, result.Error ?? "亲和性选择已保存");
        var read = await c.CallAsync(() => CpuAffinityService.ReadStateAsync(vm.Id)); c.Require(read.HasData, read.Error);
        c.Sequence(selected, read.Data!.SavedProcessors ?? [], "停机保存的选择");
        if (mode == HyperVSchedulerType.Root) { c.Equal(CpuAffinityService.SaveStatus.Queued, result.Status, "停机排队状态"); c.Require(read.Data!.AppliedProcessors is null && read.Data!.ProcessId is null, "停机阶段只有保存偏好"); }
        await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Start-VM; @{ok=$true}|ConvertTo-Json", new { id = vm.Id }); await SystemBaseline.WaitStateAsync(vm.Id, 2);
        var applied = await c.CallAsync(() => CpuAffinityService.ApplySavedAffinityAfterStartAsync(vm.Id, c.Token));
        c.Require(applied is CpuAffinityService.StartupApplyStatus.Applied or CpuAffinityService.StartupApplyStatus.NotRequired, "启动恢复结果：" + applied);
        var runtime = await c.CallAsync(() => CpuAffinityService.GetCpuAffinityAsync(vm.Id)); c.Sequence(selected, runtime.Order(), "实际应用处理器集合");
        read = await c.CallAsync(() => CpuAffinityService.ReadStateAsync(vm.Id)); c.Require(read.HasData, read.Error); c.Sequence(selected, read.Data!.AppliedProcessors ?? [], "快照区分实际生效与保存偏好");
        if (mode == HyperVSchedulerType.Root)
        {
            c.Require(read.Data!.ProcessId.HasValue && read.Data!.ProcessCreationTime.HasValue, "已核验运行进程实例");
            using var process = Process.GetProcessById(read.Data!.ProcessId!.Value); ulong mask = unchecked((ulong)process.ProcessorAffinity.ToInt64());
            c.Sequence(selected, Enumerable.Range(0, 64).Where(i => (mask & (1ul << i)) != 0), "系统进程亲和性掩码");
        }
        else
        {
            var raw = await HcsApi.GetAllCpuGroupsAsync(); c.Require(raw.HasData, raw.Error); var g = raw.Data!.Single(g => g.GroupId == result.CurrentGroupId);
            c.Sequence(selected.Select(v => (uint)v), g.Affinity!.LogicalProcessors ?? [], "底层 HCS 组处理器集合");
        }
        if (p.Api.Contains("ApplySaved") || p.Api.Contains("SaveAsync"))
        {
            var repeated = await c.CallAsync(() => CpuAffinityService.ApplySavedAffinityAfterStartAsync(vm.Id, c.Token)); c.Equal(CpuAffinityService.StartupApplyStatus.NotRequired, repeated, "同一进程实例恢复去重");
            var clear = await c.CallAsync(() => CpuAffinityService.SaveAsync(vm.Id, [])); Track(c, clear); c.Require(clear.Status == CpuAffinityService.SaveStatus.Applied && clear.MetadataSaved, "清空选择解除限制");
            var empty = await c.CallAsync(() => CpuAffinityService.ReadStateAsync(vm.Id)); c.Require(empty.HasData && empty.Data!.SavedProcessors?.Count == 0, "空选择明确保存");
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                var bad = await VmMetadataService.SetAsync(vm.Id, VmMetadataService.CpuAffinityKey, "[999999]"); c.Require(bad.Success, bad.Error);
                var rejected = await c.CallAsync(() => CpuAffinityService.ApplySavedAffinityAfterStartAsync(vm.Id, c.Token)); c.Equal(CpuAffinityService.StartupApplyStatus.ReselectionRequired, rejected, "旧环境编号保留并请求重新选择");
                var invalid = await VmMetadataService.SetAsync(vm.Id, VmMetadataService.CpuAffinityKey, "{bad"); c.Require(invalid.Success, invalid.Error);
                c.Equal(CpuAffinityService.StartupApplyStatus.InvalidConfiguration, await c.CallAsync(() => CpuAffinityService.ApplySavedAffinityAfterStartAsync(vm.Id, c.Token)), "损坏配置明确分类");
            }
        }
    }
    private static void Track(ApiCaseContext c, CpuAffinityService.SaveResult r)
    {
        if (r.CurrentGroupId is Guid current) TemporaryCpuGroups.Record(c, current);
        if (r.RetainedGroupId is Guid retained) TemporaryCpuGroups.Record(c, retained);
        c.RequireSettled(r.ErrorKind == ApiErrorKind.OutcomeUnknown || r.CleanupErrorKind == ApiErrorKind.OutcomeUnknown, r.Error ?? r.CleanupError ?? "CPU 组操作结果未知");
    }
    private static async Task GroupsAsync(CatalogTest p, ApiCaseContext c, HyperVSchedulerType mode)
    {
        var baseline = await HcsApi.GetAllCpuGroupsAsync();
        if (!baseline.HasData) throw new ApiCheckSkippedException("HCS CPU 组查询不可用：" + baseline.Error);
        if (p.Api.Contains("GetAll"))
        {
            var result = await c.CallAsync(() => HostCpuGroupService.GetAllAsync(c.Token)); c.Require(result.HasData, result.Error);
            c.Sequence(baseline.Data!.Select(v => v.GroupId).Order(), result.Data!.Select(v => v.GroupId).Order(), "CPU 组 GUID 清单");
            foreach (var group in result.Data) Compare(c, baseline.Data!.Single(v => v.GroupId == group.GroupId), group); return;
        }
        if (mode is not (HyperVSchedulerType.Classic or HyperVSchedulerType.Core)) throw new ApiCheckSkippedException("当前为 " + mode + " 调度器；CPU 组策略写入适用于 Classic/Core。");
        var topology = await HcsApi.GetProcessorTopologyAsync(); c.Require(topology.HasData && topology.Data!.Count > 0, topology.Error);
        var id = Guid.NewGuid(); TemporaryCpuGroups.Record(c, id);
        var create = await HcsApi.CreateCpuGroupAsync(id, topology.Data!.Select(v => v.LpIndex).Take(1).ToArray()); c.RequireSettled(create.ErrorKind == ApiErrorKind.OutcomeUnknown, create.Error); c.Require(create.Success, create.Error);
        var before = await c.CallAsync(() => HostCpuGroupService.GetAsync(id, c.Token)); c.Require(before.HasData, before.Error);
        var native = await HcsApi.GetAllCpuGroupsAsync(); c.Require(native.HasData, native.Error); Compare(c, native.Data!.Single(v => v.GroupId == id), before.Data!);
        if (p.Api.Contains("GetAsync"))
        { var missing = await c.CallAsync(() => HostCpuGroupService.GetAsync(Guid.NewGuid(), c.Token)); c.Equal(ApiErrorKind.NotFound, missing.ErrorKind, "缺失组身份"); return; }
        c.Require(before.Data!.MembersComplete && !string.IsNullOrEmpty(before.Data!.Revision), "完整成员版本可用于修改");
        uint code = p.Api.Contains("CpuCap") ? 0x10000u : p.Api.Contains("Priority") ? 0x20000u : p.Api.Contains("IdleProcessor") ? 0x30000u : 0x40000u;
        var raw = native.Data!.Single(v => v.GroupId == id); if (raw.GroupProperties?.All(v => v.PropertyCode != code) != false) throw new ApiCheckSkippedException("本机 HCS 未提供待测组属性 0x" + code.ToString("X"));
        Task<ApiResponse<HostCpuGroupInfo>> Set(uint value, string revision) => code switch
        { 0x10000 => HostCpuGroupService.SetCpuCapAsync(id, value, revision, c.Token), 0x20000 => HostCpuGroupService.SetPriorityAsync(id, value, revision, c.Token), 0x30000 => HostCpuGroupService.SetIdleProcessorReserveAsync(id, value, revision, c.Token), _ => HostCpuGroupService.SetRelativeWeightAsync(id, value, revision, c.Token) };
        string oldRevision = before.Data!.Revision!;
        uint[] values = code switch { 0x10000 => [32768, 65536], 0x20000 => [0, 7], 0x30000 => [0, 1], _ => [1, 65535] };
        foreach (uint value in values)
        {
            var response = await c.CallAsync(() => Set(value, before.Data!.Revision!)); c.RequireSettled(response.ErrorKind == ApiErrorKind.OutcomeUnknown, response.Error); c.Require(response.HasData, response.Error);
            native = await HcsApi.GetAllCpuGroupsAsync(); c.Require(native.HasData, native.Error); raw = native.Data!.Single(v => v.GroupId == id);
            c.Equal((ulong)value, raw.GroupProperties!.Single(v => v.PropertyCode == code).PropertyValue, "底层属性读回"); Compare(c, raw, response.Data!); before = response;
        }
        if (oldRevision != before.Data!.Revision) { var stale = await c.CallAsync(() => Set(values[0], oldRevision)); c.Equal(ApiErrorKind.Conflict, stale.ErrorKind, "过期成员版本拒绝写入"); }
        var bad = await c.CallAsync(() => Set(uint.MaxValue, before.Data!.Revision!)); c.Equal(ApiErrorKind.InvalidArgument, bad.ErrorKind, "越界属性拒绝写入");
    }
    private static void Compare(ApiCaseContext c, HcsCpuGroupDetail raw, HostCpuGroupInfo actual)
    {
        c.Equal(raw.GroupId, actual.GroupId, "CPU 组身份"); c.Sequence(raw.Affinity?.LogicalProcessors ?? [], actual.LogicalProcessors ?? [], "逻辑处理器");
        uint? Value(uint code) => raw.GroupProperties?.FirstOrDefault(v => v.PropertyCode == code) is { } value ? checked((uint)value.PropertyValue) : null;
        c.Equal(Value(0x10000), actual.CpuCap, "组配额"); c.Equal(Value(0x20000), actual.Priority, "组优先级"); c.Equal(Value(0x30000), actual.IdleProcessorReserve, "保留处理器"); c.Equal(Value(0x40000), actual.RelativeWeight, "相对权重");
        c.Require(actual.MembersComplete ? !string.IsNullOrEmpty(actual.Revision) : !string.IsNullOrEmpty(actual.MembersError), "成员查询完整性具有对应版本或错误");
    }
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
}
