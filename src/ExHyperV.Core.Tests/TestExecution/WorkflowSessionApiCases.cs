using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class WorkflowSessionApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "GpuDeploymentSession.构造()" or "GpuDeploymentSession.StartAsync()" or "GpuDeploymentSession.SubmitDecisionAsync()"
        or "GpuDeploymentSession.CancelAsync()" or "GpuDeploymentSession.RequestCancellation()" or "GpuDeploymentSession.GetStatus()"
        or "GpuDeploymentSession.RetryCleanupAsync()" or "GpuDeploymentSession.ReconcileAsync()" or "VmPowerOperationSession.构造()"
        or "VmPowerOperationSession.AdvanceAsync()" or "VmPowerOperationSession.SubmitDecisionAsync()" or "VmPowerOperationSession.RunAsync()"
        or "VmPcieChangeSession.构造()" or "VmPcieChangeSession.ApplyAsync()" or "PcieAssignmentSession.构造()" or "PcieAssignmentSession.AdvanceAsync()";
    internal static Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        c.Evidence.Add("状态机契约：使用受控底层响应，逐次调用真实 Core 会话 API；设备效果由对应实机 API 项核验。");
        return p.Api.StartsWith("GpuDeploymentSession.") ? GpuAsync(p, c) : p.Api.StartsWith("VmPowerOperationSession.") ? PowerAsync(p, c) : PcieAsync(p, c);
    }
    private static async Task GpuAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api.Contains("构造"))
        {
            using var query = new VmQueryService(); var service = new VmGpuService(query);
            foreach (var mode in Enum.GetValues<GpuDeploymentMode>())
            { var s = c.Call(() => new GpuDeploymentSession(Guid.NewGuid(), "fixture", new HostGpuInfo(), service, query, mode)); c.Equal(GpuDeploymentState.Created, s.GetStatus().State, "构造只产生初始状态"); }
            c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => new GpuDeploymentSession(Guid.NewGuid(), "fixture", new HostGpuInfo(), service, query, (GpuDeploymentMode)999)), "无效模式拒绝"); return;
        }
        var backend = new GpuWorkflowChecks.Backend(); var session = new GpuDeploymentSession(backend);
        if (p.Api.Contains("GetStatus"))
        {
            var initial = c.Call(session.GetStatus); c.Equal(GpuDeploymentState.Created, initial.State, "初始状态");
            var snapshots = new List<GpuDeploymentSnapshot>(); await session.StartAsync(s => snapshots.Add(c.Call(session.GetStatus)));
            c.Equal(GpuDeploymentState.Created, initial.State, "旧快照隔离"); c.Require(snapshots.Count > 1, "逐步骤发布快照");
            var waiting = c.Call(session.GetStatus); c.Equal(GpuDeploymentState.WaitingForDecision, waiting.State, "确认前暂停"); await session.CancelAsync(); c.Equal(GpuDeploymentState.Cancelled, c.Call(session.GetStatus).State, "取消终态"); return;
        }
        if (p.Api.Contains("StartAsync"))
        {
            foreach (var mode in Enum.GetValues<GpuDeploymentMode>())
            {
                backend = new(); session = new(backend, mode); var state = await c.CallAsync(() => session.StartAsync());
                c.Equal(mode == GpuDeploymentMode.AddWithoutDrivers ? GpuDeploymentState.Completed : GpuDeploymentState.WaitingForDecision, state.State, "模式对应执行边界：" + mode);
                if (p.Id.EndsWith("-2")) await RejectAsync(c, () => session.StartAsync(), "同一会话二次启动");
                c.Equal(mode == GpuDeploymentMode.UpdateDrivers ? 0 : 1, backend.Calls.Count(v => v == "Assign"), "分配执行次数"); await session.CancelAsync();
            }
            return;
        }
        if (p.Api.Contains("RequestCancellation"))
        {
            c.Call(session.RequestCancellation); var state = await session.StartAsync(); c.Equal(GpuDeploymentState.Cancelled, state.State, "启动前取消"); c.Equal(0, backend.Calls.Count, "底层尚无调用");
            backend = new(); session = new(backend); await session.StartAsync(); c.Call(session.RequestCancellation); await session.CancelAsync(); c.Equal(GpuDeploymentState.Cancelled, session.GetStatus().State, "等待决定时取消"); return;
        }
        if (p.Api.Contains("CancelAsync"))
        {
            var before = await c.CallAsync(session.CancelAsync); c.Equal(GpuDeploymentState.Cancelled, before.State, "准备前取消");
            backend = new(); session = new(backend); await session.StartAsync(); var cancelled = await c.CallAsync(session.CancelAsync); c.Equal(GpuDeploymentState.Cancelled, cancelled.State, "等待时取消"); c.Sequence(new[] { "new-adapter" }, backend.Removed, "精确清理本次适配器");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend = new() { AssignPause = async () => { entered.SetResult(); await release.Task; } }; session = new(backend);
            var running = session.StartAsync(); Task<GpuDeploymentSnapshot>? stopping = null;
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping = c.CallAsync(session.CancelAsync); c.Require(!stopping.IsCompleted, "取消等待在途分配完成"); }
            finally { release.TrySetResult(); await running; if (stopping is not null) await stopping; }
            c.Equal(GpuDeploymentState.Cancelled, session.GetStatus().State, "执行中取消终态"); c.Sequence(new[] { "new-adapter" }, backend.Removed, "在途调用收尾归属"); return;
        }
        if (p.Api.Contains("RetryCleanup"))
        {
            backend.FailRemoval = true; await session.StartAsync(); await session.CancelAsync(); c.Equal(GpuDeploymentState.CleanupRequired, session.GetStatus().State, "固定清理失败");
            var still = await c.CallAsync(session.RetryCleanupAsync); c.Equal(GpuDeploymentState.CleanupRequired, still.State, "重试失败保留目标"); backend.FailRemoval = false;
            var clean = await c.CallAsync(session.RetryCleanupAsync); c.Equal(GpuDeploymentState.Cancelled, clean.State, "恢复后重试清理"); c.Equal(1, backend.Calls.Count(v => v == "Assign"), "清理重试保持原分配次数"); return;
        }
        if (p.Api.Contains("Reconcile"))
        {
            backend.UnidentifiedAssignment = true; await session.StartAsync(); var pending = await c.CallAsync(session.ReconcileAsync); c.Equal(GpuDeploymentState.CleanupRequired, pending.State, "原调用尚未结束时保留"); c.Equal(0, backend.Removed.Count, "核实过程保持只读");
            backend.ReconciliationComplete = true; var known = await c.CallAsync(session.ReconcileAsync); c.Equal("new-adapter", known.PendingCleanupAdapterId, "终态回执恢复资源身份"); c.Equal(0, backend.Removed.Count, "核实后仍等待单独清理"); await session.RetryCleanupAsync(); c.Sequence(new[] { "new-adapter" }, backend.Removed, "随后精确清理"); return;
        }
        backend.Off = false; var request = await session.StartAsync();
        if (p.Id.EndsWith("-2"))
        {
            Guid current = request.PendingDecision!.Id;
            await RejectAsync(c, () => session.SubmitDecisionAsync(new GpuConfirmation(Guid.NewGuid(), true)), "旧决定身份");
            await RejectAsync(c, () => session.SubmitDecisionAsync(new GpuPartitionSelection(current, "forged")), "越序选择");
            c.Equal(current, session.GetStatus().PendingDecision!.Id, "合法请求保留");
            backend.MultiplePartitions = true; await session.SubmitDecisionAsync(new GpuConfirmation(current, true)); var partitions = session.GetStatus().PendingDecision!;
            await RejectAsync(c, () => session.SubmitDecisionAsync(new GpuPartitionSelection(partitions.Id, "forged")), "伪造分区"); await session.CancelAsync(); return;
        }
        backend.MultiplePartitions = true;
        var choice = await c.CallAsync(() => session.SubmitDecisionAsync(new GpuConfirmation(request.PendingDecision!.Id, true)));
        c.Equal(GpuDecisionKind.SelectPartition, choice.PendingDecision!.Kind, "关机确认后分区选择");
        var install = await c.CallAsync(() => session.SubmitDecisionAsync(new GpuPartitionSelection(choice.PendingDecision.Id, choice.PendingDecision.Partitions[0].Id)));
        c.Equal(GpuDecisionKind.InstallWindows, install.PendingDecision!.Kind, "安装确认");
        var complete = await c.CallAsync(() => session.SubmitDecisionAsync(new GpuConfirmation(install.PendingDecision.Id, true))); c.Equal(GpuDeploymentState.Completed, complete.State, "Windows 选择链完成");
        backend = new() { Os = OperatingSystemType.Linux }; session = new(backend); var linux = await session.StartAsync();
        if (linux.PendingDecision!.Kind == GpuDecisionKind.SelectPartition) linux = await c.CallAsync(() => session.SubmitDecisionAsync(new GpuPartitionSelection(linux.PendingDecision.Id, linux.PendingDecision.Partitions[0].Id)));
        c.Equal(GpuDecisionKind.StartLinux, linux.PendingDecision!.Kind, "Linux 启动确认");
        linux = await c.CallAsync(() => session.SubmitDecisionAsync(new GpuConfirmation(linux.PendingDecision.Id, true)));
        c.Equal(GpuDecisionInput.LinuxCredentials, linux.PendingDecision!.Input, "启动后接收 SSH 输入");
        var done = await c.CallAsync(() => session.SubmitDecisionAsync(new GpuLinuxInput(linux.PendingDecision!.Id, new() { Name = "fixture", FileName = "fixture.sh", SourceUrl = "https://example.invalid/fixture.sh" }, new() { Host = "192.0.2.1", Port = 22, Username = "root", Password = "fixture-password" })));
        c.Equal(GpuDeploymentState.Completed, done.State, "Linux 输入链完成"); c.Require(!System.Text.Json.JsonSerializer.Serialize(done).Contains("fixture-password"), "公开快照屏蔽凭据");
    }

    private static async Task PowerAsync(CatalogTest p, ApiCaseContext c)
    {
        Guid id = Guid.NewGuid();
        if (p.Api.Contains("构造")) { foreach (var action in Enum.GetValues<VmPowerAction>()) { var s = c.Call(() => new VmPowerOperationSession(id, action, "fixture")); c.Equal(id, s.VmId, "固定目标"); c.Equal(action, s.Action, "固定动作"); c.Equal(VmPowerOperationStage.Created, s.Stage, "初始阶段"); } return; }
        var backend = new PowerWorkflowChecks.Backend("GPU Partition"); backend.Gpus.Add(new(id, 1, Guid.NewGuid().ToString("D"), "stale", null));
        var session = new VmPowerOperationSession(id, VmPowerAction.Start, backend);
        if (p.Id == "contract.power-query-failure")
        {
            backend.ThrowGpuQuery = true; await c.CallAsync(async () => { await session.AdvanceAsync(); return true; }); c.Equal(VmPowerOperationStage.Failed, session.Stage, "诊断读取失败终止"); c.Equal(0, backend.Repaired.Count, "失败后未执行修复");
            backend = new("0x80070103"); backend.Disks.AddRange([new(), new()]); backend.Removals.Enqueue(true); backend.Removals.Enqueue(false); session = new(id, VmPowerAction.Start, backend);
            await c.CallAsync(async () => { await session.AdvanceAsync(); return true; }); await session.SubmitDecisionAsync(session.Confirmation!.RequestId, true); c.Equal(VmPowerOperationStage.Failed, session.Stage, "部分修复失败终止"); c.Equal(1, backend.Executions, "失败后保持初次电源调用次数"); return;
        }
        if (p.Api.Contains("RunAsync"))
        {
            await c.CallAsync(async () => { await session.RunAsync(_ => Task.FromResult(true)); return true; }); c.Equal(VmPowerOperationStage.Completed, session.Stage, "GPU 修复后完成"); c.Equal(2, backend.Executions, "修复后一次重试");
            backend = new("0x80070103"); backend.Disks.Add(new()); backend.Removals.Enqueue(true); session = new(id, VmPowerAction.Start, backend);
            await c.CallAsync(async () => { await session.RunAsync(_ => Task.FromResult(true)); return true; }); c.Equal(VmPowerOperationStage.Completed, session.Stage, "磁盘修复后完成");
            backend = new(); session = new(id, VmPowerAction.Start, backend); await c.CallAsync(async () => { await session.RunAsync(); return true; }); c.Equal(1, backend.Executions, "普通电源动作一次调用"); return;
        }
        if (p.Api.Contains("AdvanceAsync"))
        {
            await c.CallAsync(async () => { await session.AdvanceAsync(); return true; }); c.Equal(VmPowerOperationStage.AwaitingConfirmation, session.Stage, "初始动作失败后等待确认");
            await RejectAsync(c, async () => { await session.AdvanceAsync(); }, "等待确认阶段直接推进"); await session.SubmitDecisionAsync(session.Confirmation!.RequestId, true);
            await c.CallAsync(async () => { await session.AdvanceAsync(); return true; }); c.Equal(VmPowerOperationStage.Completed, session.Stage, "可重试阶段推进"); await RejectAsync(c, async () => { await session.AdvanceAsync(); }, "终态重复推进"); return;
        }
        await session.AdvanceAsync(); var current = session.Confirmation!.RequestId;
        await c.CallAsync(async () => { await session.SubmitDecisionAsync(Guid.NewGuid(), true); return true; }); c.Equal(current, session.Confirmation!.RequestId, "过期决定保留当前请求"); c.Equal(ApiErrorKind.Conflict, session.Failure!.ErrorKind, "过期决定分类");
        await c.CallAsync(async () => { await session.SubmitDecisionAsync(current, false); return true; }); c.Equal(VmPowerOperationStage.Cancelled, session.Stage, "拒绝终态"); c.Equal(0, backend.Repaired.Count, "拒绝后的修复数");
        await RejectAsync(c, async () => { await session.SubmitDecisionAsync(current, true); }, "终态重复决定");
        backend = new("GPU Partition"); backend.Gpus.Add(new(id, 1, Guid.NewGuid().ToString("D"), "stale", null)); session = new(id, VmPowerAction.Start, backend); await session.AdvanceAsync();
        await c.CallAsync(async () => { await session.SubmitDecisionAsync(session.Confirmation!.RequestId, true); return true; }); c.Equal(VmPowerOperationStage.ReadyToRetry, session.Stage, "同意修复后等待重试");
    }

    private static async Task PcieAsync(CatalogTest p, ApiCaseContext c)
    {
        Guid id = Guid.NewGuid(); var device = new PcieDeviceInfo { InstanceId = "fixture", FriendlyName = "fixture", ClassType = "Display", Status = HostPcieService.HostKey, Path = "PCIROOT(0)#PCI(0600)#PCI(0000)" };
        if (p.Api.StartsWith("PcieAssignmentSession."))
        {
            if (p.Api.Contains("构造")) { var created = c.Call(() => new PcieAssignmentSession(device, [device], PcieAssignmentTarget.ForVirtualMachine(id, "fixture"))); c.Equal(PcieAssignmentStep.Ready, created.State.Step, "分配初始阶段"); return; }
            var backend = new DeviceWorkflowChecks.PcieBackend { Mmio = MmioCheckResultType.NeedsExpansion };
            var session = new PcieAssignmentSession(device, [device], id.ToString(), backend.Create());
            var state = await c.CallAsync(() => session.AdvanceAsync()); c.Equal(PcieAssignmentStep.ConfirmMmio, state.Step, "MMIO 确认边界");
            bool approve = !p.Id.EndsWith("-2");
            for (int i = 0; state.RequiresDecision && i < 8; i++) state = await c.CallAsync(() => session.AdvanceAsync(new(state.RequestId!.Value, approve)));
            c.Equal(approve ? PcieAssignmentStep.Completed : PcieAssignmentStep.Cancelled, state.Step, "分配终态"); int calls = backend.Calls.Count; await c.CallAsync(() => session.AdvanceAsync()); c.Equal(calls, backend.Calls.Count, "终态再次推进保持调用数");
            if (approve)
            {
                device = new PcieDeviceInfo { InstanceId = "fixture", FriendlyName = "fixture", ClassType = "HDC", Status = HostPcieService.HostKey, Path = "PCIROOT(0)#PCI(0600)#PCI(0000)" }; backend = new() { Disks = [new(3, "fixture-disk", false, false, false)] }; session = new(device, [device], id.ToString(), backend.Create());
                state = await c.CallAsync(() => session.AdvanceAsync()); c.Equal(PcieAssignmentStep.ConfirmOfflineDisks, state.Step, "关联磁盘确认"); state = await c.CallAsync(() => session.AdvanceAsync(new(state.RequestId!.Value, true))); c.Equal(PcieAssignmentStep.Completed, state.Step, "磁盘分配完成"); c.Require(backend.Calls.IndexOf("disk:3:True") < backend.Calls.IndexOf("execute:fixture"), "先脱机后分配");
            }
            return;
        }
        var setting = new VmPcieDeviceSetting { WmiInstanceId = "fixture", GuestModeAvailable = true }; var writes = new List<string>();
        var stateNow = new VmPcieSettings { SystemSettingsAvailable = true, Devices = [setting] };
        var backendChange = new VmPcieChangeBackend(_ => Task.FromResult(ApiResponse<VmPcieSettings>.Ok(stateNow)), (_, enabled, topology) => { writes.Add("system"); return Task.FromResult(ApiResponse.Ok()); }, (_, _, mode) => { writes.Add("device"); return Task.FromResult(ApiResponse.Ok()); });
        foreach (var kind in Enum.GetValues<VmPcieChangeKind>())
        {
            if (p.Api.Contains("构造")) { var initial = c.Call(() => new VmPcieChangeSession(kind, id, false, 1, setting, VmPcieGuestMode.Emulated)); c.Require(initial.RequiresConfirmation && !initial.EmulationEnabled, "永久模拟确认要求：" + kind); continue; }
            writes.Clear(); var session = new VmPcieChangeSession(kind, id, false, 1, setting, VmPcieGuestMode.Emulated, backendChange);
            if (p.Id.EndsWith("-2")) stateNow.Devices.Clear();
            var result = await c.CallAsync(() => session.ApplyAsync(true));
            if (p.Id.EndsWith("-2") && kind == VmPcieChangeKind.DeviceMode) { c.Equal(ApiErrorKind.Conflict, result.Response.ErrorKind, "设备在确认后移除"); c.Equal(0, writes.Count, "归属变化时保持原配置"); }
            else { c.Require(result.Success, result.Error); c.Sequence(kind == VmPcieChangeKind.DeviceMode ? new[] { "system", "device" } : new[] { "system" }, writes, "修改顺序"); }
            int count = writes.Count; await c.CallAsync(() => session.ApplyAsync(true)); c.Equal(count, writes.Count, "同一对象仅执行一次");
        }
    }
    private static async Task RejectAsync(ApiCaseContext c, Func<Task> action, string label)
    { try { await c.CallAsync(async () => { await action(); return true; }); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { c.Evidence.Add(label + "：" + e.GetType().Name); return; } throw new ApiCheckFailureException(label + "应拒绝"); }
}
