using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmCheckpointApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmSpacetimeService.GetSpacetimeNodesAsync()" or "VmSpacetimeService.CaptureMomentAsync()"
        or "VmSpacetimeService.RenameSnapshotAsync()" or "VmSpacetimeService.AnnihilateAsync()" or "VmSpacetimeService.ConvergeAsync()"
        or "VmSpacetimeService.OpenWormholeAsync()" or "VmSpacetimeService.CloseWormholeAsync()"
        || p.Api.StartsWith("VmSpacetimeService.TeleportAsync(", StringComparison.Ordinal);

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        await TemporaryDisk.AttachAsync(vm, "before");
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VM -CheckpointType Standard -AutomaticCheckpointsEnabled $false; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        if (p.Api == "VmSpacetimeService.CaptureMomentAsync()")
        {
            foreach (var mode in new[] { SpacetimeMode.Continuous, SpacetimeMode.Still })
            {
                int before = (await NativeAsync(vm.Id)).GetArrayLength();
                Require(c, await c.CallAsync(() => VmSpacetimeService.CaptureMomentAsync(vm.Id, mode)), "创建 " + mode);
                c.Equal(before + 1, (await NativeAsync(vm.Id)).GetArrayLength(), "系统新增一个检查点");
                c.Equal("before", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "检查点后磁盘内容保留");
            }
            c.Evidence.Add("关机时 Continuous 与 Still 均核验标准检查点；生产检查点另在可启动模板副本上核验。");
            await ProductionAsync(c); return;
        }
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Checkpoint-VM -SnapshotName 'A'; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single(), "after-A");
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Checkpoint-VM -SnapshotName 'B'; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        var nodes = await NodesAsync(vm.Id); var a = nodes.Single(n => n.NodeType == SpacetimeNodeType.Snapshot && n.Name == "A"); var b = nodes.Single(n => n.NodeType == SpacetimeNodeType.Snapshot && n.Name == "B");
        if (p.Api == "VmSpacetimeService.GetSpacetimeNodesAsync()")
        {
            var result = await c.CallAsync(() => VmSpacetimeService.GetSpacetimeNodesAsync(vm.Id));
            c.Require(result.HasData, "查询检查点树：" + result.Error);
            var native = await NativeAsync(vm.Id);
            c.Sequence(native.EnumerateArray().Select(x => x.GetProperty("id").GetString()).Order(), result.Data!.Where(n => n.NodeType == SpacetimeNodeType.Snapshot).Select(n => n.Id).Order(), "真实检查点身份集合");
            foreach (var row in native.EnumerateArray())
            {
                var node = result.Data!.Single(n => n.Id == row.GetProperty("id").GetString());
                c.Equal(row.GetProperty("name").GetString(), node.Name, "检查点名称");
                string? parent = row.GetProperty("parent").GetString();
                if (Guid.TryParse(parent, out var parentId) && parentId != Guid.Empty) c.Equal(parent, node.ParentId, "检查点父节点");
                c.Require(!string.IsNullOrWhiteSpace(node.VhdPath) && File.Exists(node.VhdPath), "历史磁盘存在");
            }
            c.Equal(1, result.Data!.Count(n => n.NodeType == SpacetimeNodeType.Current), "当前状态节点唯一");
            c.Equal(1, result.Data!.Count(n => n.NodeType == SpacetimeNodeType.Genesis), "起点唯一");
        }
        else if (p.Api == "VmSpacetimeService.RenameSnapshotAsync()")
        {
            if (p.Id.EndsWith("-3", StringComparison.Ordinal))
            {
                await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|Where-Object Id -eq ([guid]$a.snapshot)|Remove-VMSnapshot -Confirm:$false; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, snapshot = b.Id });
                var gone = await c.CallAsync(() => VmSpacetimeService.RenameSnapshotAsync(vm.Id, b, "消失"));
                c.Require(!gone.Success, "已删除检查点拒绝重命名");
                c.Equal("A", (await NativeAsync(vm.Id)).EnumerateArray().Single().GetProperty("name").GetString(), "其他检查点名称保留"); return;
            }
            for (int i = 0; i < (p.Id.EndsWith("-2", StringComparison.Ordinal) ? 2 : 1); i++) Require(c, await c.CallAsync(() => VmSpacetimeService.RenameSnapshotAsync(vm.Id, b, "中文 Checkpoint B")), "重命名检查点");
            var updated = (await NativeAsync(vm.Id)).EnumerateArray().Single(n => n.GetProperty("id").GetString() == b.Id);
            c.Equal("中文 Checkpoint B", updated.GetProperty("name").GetString(), "系统检查点名称");
            c.Equal(a.Id, updated.GetProperty("parent").GetString(), "原父关系保留");
        }
        else if (p.Api is "VmSpacetimeService.AnnihilateAsync()" or "VmSpacetimeService.ConvergeAsync()")
        {
            bool subtree = p.Api == "VmSpacetimeService.AnnihilateAsync()";
            Require(c, await c.CallAsync(() => subtree ? VmSpacetimeService.AnnihilateAsync(vm.Id, a) : VmSpacetimeService.ConvergeAsync(vm.Id, a)), "删除历史节点");
            var ids = (await NativeAsync(vm.Id)).EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToArray();
            c.Require(!ids.Contains(a.Id), "目标节点消失"); c.Equal(!subtree, ids.Contains(b.Id), "后继节点是否保留");
            c.Equal("after-A", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "合并后文件内容保持");
        }
        else if (p.Api.StartsWith("VmSpacetimeService.TeleportAsync(", StringComparison.Ordinal))
        {
            await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single(), "after-B");
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMProcessor -Count 2; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
            var result = await c.CallAsync(() => VmSpacetimeService.TeleportAsync(a, vm.Id, nodes)); Require(c, result, "恢复 A");
            c.Equal("before", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(vm.Id)).Single()), "恢复检查点中的文件标记");
            var cpu = await WindowsTestCommand.RunAsync("@{count=(Get-VM -Id ([guid]$a.id)|Get-VMProcessor).Count}|ConvertTo-Json", new { id = vm.Id });
            c.Equal(1, cpu.GetProperty("count").GetInt32(), "恢复检查点时的处理器数量");
            c.Sequence(new[] { a.Id, b.Id }.Order(), (await NativeAsync(vm.Id)).EnumerateArray().Select(n => n.GetProperty("id").GetString()!).Order(), "恢复后历史节点保留");
        }
        else
        {
            // Apply A with the system tool so B is outside the active disk ancestry.
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|Where-Object Id -eq ([guid]$a.snapshot)|Restore-VMSnapshot -Confirm:$false; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, snapshot = a.Id });
            b = (await NodesAsync(vm.Id)).Single(n => n.Id == b.Id);
            var pathsBefore = await TemporaryDisk.PathsAsync(vm.Id);
            var opened = p.Api == "VmSpacetimeService.OpenWormholeAsync()" ? await c.CallAsync(() => VmSpacetimeService.OpenWormholeAsync(vm.Id, b)) : await VmSpacetimeService.OpenWormholeAsync(vm.Id, b);
            Require(c, opened, "挂接历史磁盘");
            var extra = (await TemporaryDisk.PathsAsync(vm.Id)).Except(pathsBefore, StringComparer.OrdinalIgnoreCase).Single();
            c.Equal("after-A", await TemporaryDisk.MarkerAsync(extra), "挂接指向 B 的历史文件内容");
            string temporaryPath = b.WormholeTmpDiskPath;
            var closed = p.Api == "VmSpacetimeService.CloseWormholeAsync()" ? await c.CallAsync(() => VmSpacetimeService.CloseWormholeAsync(vm.Id, b)) : await VmSpacetimeService.CloseWormholeAsync(vm.Id, b);
            Require(c, closed, "解除历史挂接");
            c.Sequence(pathsBefore.Order(StringComparer.OrdinalIgnoreCase), (await TemporaryDisk.PathsAsync(vm.Id)).Order(StringComparer.OrdinalIgnoreCase), "原磁盘连接恢复");
            c.Require(!File.Exists(temporaryPath) && File.Exists(b.VhdPath), "临时盘删除，历史盘保留");
            c.Equal("after-A", await TemporaryDisk.MarkerAsync(b.VhdPath), "历史盘内容保持");
        }
    }

    private static async Task ProductionAsync(ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CopyTemplateAsync(c);
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VM -CheckpointType Standard; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
        await vm.StartAsync();
        var preparation = await WindowsTestCommand.RunAsync("""
            $v=Get-VM -Id ([guid]$a.id)
            $end=(Get-Date).AddSeconds(180)
            do {$service=@($v|Get-VMIntegrationService|Where-Object {$_.Id.ToString() -match '5ced1297-4598-4915-a5fc-ad21bb4d02a4'}); if($service.Count -eq 1 -and $service[0].Enabled -and $service[0].PrimaryOperationalStatus -eq 'Ok'){break}; Start-Sleep -Milliseconds 500}while((Get-Date) -lt $end)
            @{ready=($service.Count -eq 1 -and $service[0].Enabled -and $service[0].PrimaryOperationalStatus -eq 'Ok')}|ConvertTo-Json
            """, new { id = vm.Id }, 200, mayMutate: false);
        if (!preparation.GetProperty("ready").GetBoolean()) throw new ApiCheckSkippedException("模板副本的备份集成服务尚未就绪，生产检查点条件待满足。");
        var production = await c.CallAsync(() => VmSpacetimeService.CaptureMomentAsync(vm.Id, SpacetimeMode.Still));
        c.RequireSettled(production.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, production.Error);
        c.Equal((ushort)5, Convert.ToUInt16(SystemBaseline.Settings(vm.Id)["UserSnapshotType"]), "创建生产检查点后保留原标准检查点策略");
        if (!production.Success)
        {
            // Compare a terminal Core failure with the independent system command
            // on the same owned VM before assigning the failure to the environment.
            var baseline = await WindowsTestCommand.RunAsync("""
                $v=Get-VM -Id ([guid]$a.id)
                try{
                    $v|Set-VM -CheckpointType ProductionOnly
                    $v|Checkpoint-VM -SnapshotName 'System production baseline' -ErrorAction Stop
                    @{success=$true;count=@(Get-VM -Id $v.Id|Get-VMSnapshot).Count}|ConvertTo-Json
                }catch{@{success=$false;error=$_.Exception.Message}|ConvertTo-Json}
                """, new { id = vm.Id }, 240);
            c.Evidence.Add("独立生产检查点核验：" + baseline.GetRawText());
        }
        Require(c, production, "生产检查点");
        var actual = await NativeAsync(vm.Id); c.Equal(1, actual.GetArrayLength(), "生产检查点数");
        var restored = await WindowsTestCommand.RunAsync("$v=Get-VM -Id ([guid]$a.id); $v|Stop-VM -TurnOff -Force; $v|Get-VMSnapshot|Restore-VMSnapshot -Confirm:$false; @{state=(Get-VM -Id ([guid]$a.id)).State.ToString()}|ConvertTo-Json", new { id = vm.Id }, 120);
        c.Equal("Off", restored.GetProperty("state").GetString(), "生产检查点恢复为关机状态，省去运行内存状态");
        c.Equal((ushort)5, Convert.ToUInt16(SystemBaseline.Settings(vm.Id)["UserSnapshotType"]), "恢复检查点后仍保留原标准检查点策略");
    }
    private static async Task<List<SpacetimeNode>> NodesAsync(Guid id)
    {
        var response = await VmSpacetimeService.GetSpacetimeNodesAsync(id);
        if (!response.HasData) throw new ApiCheckFailureException("检查点对象准备失败：" + response.Error); return response.Data;
    }
    private static Task<JsonElement> NativeAsync(Guid id) => WindowsTestCommand.RunAsync("ConvertTo-Json -InputObject @(Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|ForEach-Object {@{id=$_.Id.ToString();name=$_.Name;parent=[string]$_.ParentSnapshotId;type=$_.SnapshotType.ToString()}})", new { id });
    private static void Require(ApiCaseContext c, ApiResponse r, string label) => c.Require(r.Success, label + "：" + r.Error);
}
