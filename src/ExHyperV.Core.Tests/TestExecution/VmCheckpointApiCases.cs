using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmCheckpointApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmCheckpointService.GetCheckpointNodesAsync()" or "VmCheckpointService.CreateSnapshotAsync()"
        or "VmCheckpointService.RenameSnapshotAsync()" or "VmCheckpointService.DeleteSnapshotAsync()" or "VmCheckpointService.MergeSnapshotAsync()"
        or "VmCheckpointService.MountHistoricalDiskAsync()" or "VmCheckpointService.DismountHistoricalDiskAsync()"
        || p.Api.StartsWith("VmCheckpointService.ApplySnapshotAsync(", StringComparison.Ordinal);

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        await TemporaryDisk.AttachAsync(vm, "before");
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VM -CheckpointType Standard -AutomaticCheckpointsEnabled $false; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        if (p.Api == "VmCheckpointService.CreateSnapshotAsync()")
        {
            int before = (await NativeAsync(vm.Id)).GetArrayLength();
            Require(c, await c.CallAsync(() => VmCheckpointService.CreateSnapshotAsync(vm.Id)), "创建快照");
            c.Equal(before + 1, (await NativeAsync(vm.Id)).GetArrayLength(), "系统新增一个快照");
            c.Equal("before", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "快照后磁盘内容保留");
            await ProductionAsync(c); return;
        }
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Checkpoint-VM -SnapshotName 'A'; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single(), "after-A");
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Checkpoint-VM -SnapshotName 'B'; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        var nodes = await NodesAsync(vm.Id); var a = nodes.Single(n => n.NodeType == CheckpointNodeType.Snapshot && n.Name == "A"); var b = nodes.Single(n => n.NodeType == CheckpointNodeType.Snapshot && n.Name == "B");
        if (p.Api == "VmCheckpointService.GetCheckpointNodesAsync()")
        {
            var result = await c.CallAsync(() => VmCheckpointService.GetCheckpointNodesAsync(vm.Id));
            c.Require(result.HasData, "查询快照树：" + result.Error);
            var native = await NativeAsync(vm.Id);
            c.Sequence(native.EnumerateArray().Select(x => x.GetProperty("id").GetString()).Order(), result.Data!.Where(n => n.NodeType == CheckpointNodeType.Snapshot).Select(n => n.Id).Order(), "真实快照身份集合");
            foreach (var row in native.EnumerateArray())
            {
                var node = result.Data!.Single(n => n.Id == row.GetProperty("id").GetString());
                c.Equal(row.GetProperty("name").GetString(), node.Name, "快照名称");
                string? parent = row.GetProperty("parent").GetString();
                if (Guid.TryParse(parent, out var parentId) && parentId != Guid.Empty) c.Equal(parent, node.ParentId, "快照父节点");
                c.Require(!string.IsNullOrWhiteSpace(node.VhdPath) && File.Exists(node.VhdPath), "历史磁盘存在");
            }
            c.Equal(1, result.Data!.Count(n => n.NodeType == CheckpointNodeType.Current), "当前状态节点唯一");
            c.Equal(1, result.Data!.Count(n => n.NodeType == CheckpointNodeType.Origin), "起点唯一");
        }
        else if (p.Api == "VmCheckpointService.RenameSnapshotAsync()")
        {
            if (p.Id.EndsWith("-3", StringComparison.Ordinal))
            {
                await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|Where-Object Id -eq ([guid]$a.snapshot)|Remove-VMSnapshot -Confirm:$false; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, snapshot = b.Id });
                var gone = await c.CallAsync(() => VmCheckpointService.RenameSnapshotAsync(vm.Id, b, "消失"));
                c.Require(!gone.Success, "已删除快照拒绝重命名");
                c.Equal("A", (await NativeAsync(vm.Id)).EnumerateArray().Single().GetProperty("name").GetString(), "其他快照名称保留"); return;
            }
            for (int i = 0; i < (p.Id.EndsWith("-2", StringComparison.Ordinal) ? 2 : 1); i++) Require(c, await c.CallAsync(() => VmCheckpointService.RenameSnapshotAsync(vm.Id, b, "中文 Checkpoint B")), "重命名快照");
            var updated = (await NativeAsync(vm.Id)).EnumerateArray().Single(n => n.GetProperty("id").GetString() == b.Id);
            c.Equal("中文 Checkpoint B", updated.GetProperty("name").GetString(), "系统快照名称");
            c.Equal(a.Id, updated.GetProperty("parent").GetString(), "原父关系保留");
        }
        else if (p.Api == "VmCheckpointService.DeleteSnapshotAsync()")
        {
            Require(c, await c.CallAsync(() => VmCheckpointService.DeleteSnapshotAsync(vm.Id, a)), "删除历史节点");
            var ids = (await NativeAsync(vm.Id)).EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToArray();
            c.Require(!ids.Contains(a.Id), "目标节点消失"); c.Equal(false, ids.Contains(b.Id), "后继节点一并删除");
            c.Equal("after-A", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "删除后当前磁盘内容保持");
        }
        else if (p.Api == "VmCheckpointService.MergeSnapshotAsync()")
        {
            Require(c, await c.CallAsync(() => VmCheckpointService.MergeSnapshotAsync(vm.Id, b)), "向左合并历史节点");
            var native = (await NativeAsync(vm.Id)).EnumerateArray().ToArray();
            var ids = native.Select(n => n.GetProperty("id").GetString()).ToArray();
            c.Require(!ids.Contains(a.Id) && ids.Contains(b.Id), "旧父快照由子节点接替");
            c.Equal("A", native.Single(n => n.GetProperty("id").GetString() == b.Id).GetProperty("name").GetString(), "保留父快照名称");
            c.Equal("after-A", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "合并后文件内容保持");
        }
        else if (p.Api.StartsWith("VmCheckpointService.ApplySnapshotAsync(", StringComparison.Ordinal))
        {
            await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single(), "after-B");
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMProcessor -Count 2; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
            var result = await c.CallAsync(() => VmCheckpointService.ApplySnapshotAsync(a, vm.Id, nodes)); Require(c, result, "恢复 A");
            c.Equal("before", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "恢复快照中的文件标记");
            var cpu = await WindowsTestCommand.RunAsync("@{count=(Get-VM -Id ([guid]$a.id)|Get-VMProcessor).Count}|ConvertTo-Json", new { id = vm.Id });
            c.Equal(1, cpu.GetProperty("count").GetInt32(), "恢复快照时的处理器数量");
            c.Sequence(new[] { a.Id, b.Id }.Order(), (await NativeAsync(vm.Id)).EnumerateArray().Select(n => n.GetProperty("id").GetString()!).Order(), "恢复后历史节点保留");
        }
        else
        {
            // Apply A with the system tool so B is outside the active disk ancestry.
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|Where-Object Id -eq ([guid]$a.snapshot)|Restore-VMSnapshot -Confirm:$false; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, snapshot = a.Id });
            b = (await NodesAsync(vm.Id)).Single(n => n.Id == b.Id);
            var pathsBefore = await TemporaryDisk.PathsAsync(vm.Id);
            var opened = await c.CallAsync(() => VmCheckpointService.MountHistoricalDiskAsync(vm.Id, b));
            Require(c, opened, "挂接历史磁盘");
            var extra = (await TemporaryDisk.PathsAsync(vm.Id)).Except(pathsBefore, StringComparer.OrdinalIgnoreCase).Single();
            c.Equal("after-A", await TemporaryDisk.MarkerAsync(extra), "挂接指向 B 的历史文件内容");
            string temporaryPath = b.HistoricalDiskTempPath;
            var closed = await c.CallAsync(() => VmCheckpointService.DismountHistoricalDiskAsync(vm.Id, b));
            Require(c, closed, "解除历史挂接");
            c.Sequence(pathsBefore.Order(StringComparer.OrdinalIgnoreCase), (await TemporaryDisk.PathsAsync(vm.Id)).Order(StringComparer.OrdinalIgnoreCase), "原磁盘连接恢复");
            c.Require(!File.Exists(temporaryPath) && File.Exists(b.VhdPath), "临时盘删除，历史盘保留");
            c.Equal("after-A", await TemporaryDisk.MarkerAsync(b.VhdPath), "历史盘内容保持");
        }
    }

    private static async Task ProductionAsync(ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CopyTemplateAsync(c);
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VM -CheckpointType Production; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        await vm.StartAsync();
        var preparation = await WindowsTestCommand.RunAsync("""
            $v=Get-VM -Id ([guid]$a.id)
            $end=(Get-Date).AddSeconds(180)
            do {$service=@($v|Get-VMIntegrationService|Where-Object {$_.Id.ToString() -match '5ced1297-4598-4915-a5fc-ad21bb4d02a4'}); if($service.Count -eq 1 -and $service[0].Enabled -and $service[0].PrimaryOperationalStatus -eq 'Ok'){break}; Start-Sleep -Milliseconds 500}while((Get-Date) -lt $end)
            @{ready=($service.Count -eq 1 -and $service[0].Enabled -and $service[0].PrimaryOperationalStatus -eq 'Ok')}|ConvertTo-Json
            """, new { id = vm.Id }, 200, mayMutate: false);
        if (!preparation.GetProperty("ready").GetBoolean()) throw new ApiCheckSkippedException("模板副本的备份集成服务尚未就绪，生产快照条件待满足。");
        var production = await c.CallAsync(() => VmCheckpointService.CreateSnapshotAsync(vm.Id));
        c.RequireSettled(production.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, production.Error);
        Require(c, production, "生产快照");
        var actual = await NativeAsync(vm.Id); c.Equal(1, actual.GetArrayLength(), "生产快照数");
    }
    private static async Task<List<CheckpointNode>> NodesAsync(Guid id)
    {
        var response = await VmCheckpointService.GetCheckpointNodesAsync(id);
        if (!response.HasData) throw new ApiCheckFailureException("快照对象准备失败：" + response.Error); return response.Data;
    }
    private static Task<JsonElement> NativeAsync(Guid id) => WindowsTestCommand.RunAsync("ConvertTo-Json -InputObject @(Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|ForEach-Object {@{id=$_.Id.ToString();name=$_.Name;parent=[string]$_.ParentSnapshotId;type=$_.SnapshotType.ToString()}})", new { id });
    private static void Require(ApiCaseContext c, ApiResponse r, string label) => c.Require(r.Success, label + "：" + r.Error);
}
