using System.IO.Compression;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmTransferApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmImportService.GetPreviewError()" or "VmImportSession.DisposeAsync()" or "VmImportBatchSession.DisposeAsync()"
        or "VmImportService.PrepareBatchImportAsync()" or "VmImportService.ImportAsync()" or "VmImportService.PreparePreviewsAsync()"
        or "VmExportService.GetCheckpointsAsync()" or "VmExportService.GetVirtualHardDisksAsync()" or "VmExportService.ExportAsync()";

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api == "VmImportService.GetPreviewError()")
        {
            c.Require(c.Call(() => VmImportService.GetPreviewError([])) is not null, "空预览给出原因");
            var good = new VmImportPreview(); c.Equal<string?>(null, c.Call(() => VmImportService.GetPreviewError([good])), "零兼容性问题");
            var bad = new VmImportPreview(); bad.AddIssue("fixture-a"); bad.AddIssue("fixture-b");
            c.Equal("fixture-a" + Environment.NewLine + "fixture-b", c.Call(() => VmImportService.GetPreviewError([good, bad])), "保留完整兼容性原因"); return;
        }
        await using var network = await TemporarySwitch.CreateAsync(c);
        await using var source = await TemporaryVm.CreateAsync(c);
        string disk = await TemporaryDisk.AttachAsync(source, "transfer-marker");
        await WindowsTestCommand.RunAsync("$v=Get-VM -Id $a.id;$v|Get-VMNetworkAdapter|Connect-VMNetworkAdapter -SwitchName $a.network;@{ok=$true}|ConvertTo-Json", new { id = source.Id, network = network.Name });
        var metadata = await VmMetadataService.SetAsync(source.Id, "ApiTransferMarker", "导入导出·原值"); c.Require(metadata.Success, metadata.Error);
        if (p.Api == "VmExportService.GetVirtualHardDisksAsync()")
        {
            var actual = await c.CallAsync(() => VmExportService.GetVirtualHardDisksAsync(source.Id)); c.Require(actual.HasData, actual.Error);
            c.Sequence(await TemporaryDisk.PathsAsync(source.Id), actual.Data!.Select(v => v.Path).OrderBy(v => v).ToArray(), "磁盘路径清单"); return;
        }
        if (p.Api.StartsWith("VmExportService.", StringComparison.Ordinal)) { await ExportAsync(p, c, source, disk); return; }
        string export = Path.Combine(c.DirectoryPath, "src"); Directory.CreateDirectory(export);
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Export-VM -Path $a.path; $true|ConvertTo-Json", new { id = source.Id, path = export }, 180);
        string zip = Path.Combine(c.DirectoryPath, "source.zip"); ZipFile.CreateFromDirectory(export, zip, CompressionLevel.Fastest, false);
        if (p.Api == "VmImportService.PreparePreviewsAsync()")
        {
            foreach (var input in new[] { export, zip })
            {
                var prepared = await c.CallAsync(() => VmImportService.PreparePreviewsAsync(input, new VmImportArchiveOptions { TemporaryDirectory = c.DirectoryPath }, VmImportPlacementMode.HostDirectories));
                await using var batch = prepared.Batch; c.Require(prepared.Success && batch is not null, prepared.Result.Error);
                var view = batch!.VirtualMachines.Single().Preview; c.Equal(source.Id, view.OriginalGuid, "预览源 GUID"); c.Equal(source.Name, view.Name, "预览名称"); c.Equal(2, view.Generation, "预览代数"); c.Equal(512L, Convert.ToInt64(view.StartupMemoryMiB), "预览内存 MiB"); c.Require(view.Disks.Count == 1 && view.Disks[0].Exists && view.Disks[0].VirtualSize == 512UL * 1024 * 1024, "磁盘存在性与容量");
            }
            await using var extra = await TemporaryVm.CreateAsync(c);
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Export-VM -Path $a.path; $true|ConvertTo-Json", new { id = extra.Id, path = export }, 180);
            var multi = await c.CallAsync(() => VmImportService.PreparePreviewsAsync(export, VmImportPlacementMode.HostDirectories));
            await using (var batch = multi.Batch) { c.Require(multi.Success, multi.Result.Error); c.Sequence(new[] { source.Id, extra.Id }.OrderBy(v => v), batch!.VirtualMachines.Select(v => v.Preview.OriginalGuid).OrderBy(v => v), "多虚拟机预览"); }
            string broken = Path.Combine(c.DirectoryPath, "broken.zip"); File.WriteAllText(broken, "invalid archive");
            var invalid = await c.CallAsync(() => VmImportService.PreparePreviewsAsync(broken, VmImportPlacementMode.HostDirectories)); await using (var batch = invalid.Batch) c.Require(!invalid.Success, "损坏包返回失败"); return;
        }
        if (p.Api is "VmImportSession.DisposeAsync()" or "VmImportBatchSession.DisposeAsync()")
        {
            var prepared = await VmImportService.PreparePreviewsAsync(zip, new VmImportArchiveOptions { TemporaryDirectory = c.DirectoryPath }, VmImportPlacementMode.HostDirectories);
            await using var batch = prepared.Batch; c.Require(prepared.Success && batch is not null, prepared.Result.Error);
            var session = batch!.VirtualMachines.Single(); string? temporary = batch.TemporaryRoot; string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip)));
            await using var retained = TemporaryVm.Reserve(c);
            if (p.Id.EndsWith("-1", StringComparison.Ordinal))
            {
                var imported = await VmImportService.ImportAsync(session, new(VmImportGuidMode.GenerateNew)
                { Name = retained.Name, ConfigurationDirectory = Path.Combine(retained.Root, "config"), DiskDirectory = Path.Combine(retained.Root, "disks") });
                c.RequireSettled(imported.OperationStateUnknown, imported.Error ?? "导入终态");
                if (imported.IsRealized && imported.VmId is Guid id) retained.RecordIdentity(id);
                c.Require(imported.Success && imported.IsRealized, imported.Error ?? "准备已导入虚拟机");
            }
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                var failed = await VmImportService.ImportAsync(session, new(VmImportGuidMode.GenerateNew) { ConfigurationDirectory = Path.Combine(c.DirectoryPath, "blocked"), DiskDirectory = Path.Combine(c.DirectoryPath, "blocked") + "\0" });
                c.RequireSettled(failed.OperationStateUnknown, failed.Error ?? "导入失败终态未知"); c.Require(!failed.Success, "部分准备失败固定输入");
            }
            for (int i = 0; i < (p.Id.EndsWith("-2", StringComparison.Ordinal) ? 2 : 1); i++)
            {
                if (p.Api == "VmImportSession.DisposeAsync()")
                {
                    await c.CallAsync(async () => { await session.DisposeAsync(); return true; });
                    c.Equal(VmImportCleanupState.Completed, session.CleanupState, "单机会话释放完成");
                    c.Require(session.RetainedPaths.All(path => string.Equals(path, temporary, StringComparison.OrdinalIgnoreCase)), "仅批次持有的共享解包目录可保留");
                    c.Require(temporary is not null && Directory.Exists(temporary), "释放单机会话后批次仍持有解包目录");
                }
                else { await c.CallAsync(async () => { await batch.DisposeAsync(); return true; }); c.Equal(VmImportCleanupState.Completed, batch.CleanupState, "批次释放完成"); c.Equal(0, batch.RetainedPaths.Count, "批次暂存已释放"); }
            }
            await batch.DisposeAsync(); c.Require(temporary is null || !Directory.Exists(temporary), "共享解包目录已释放"); c.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))), "来源归档保持原内容");
            if (p.Id.EndsWith("-1", StringComparison.Ordinal))
            {
                c.Equal(retained.Name, Convert.ToString(SystemBaseline.Vm(retained.Id)["ElementName"]), "会话释放保留已导入虚拟机");
                c.Equal("transfer-marker", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(retained.Id)).Single()), "已导入磁盘文件保留");
            }
            c.Require(SystemBaseline.Vm(source.Id).Count > 0, "源虚拟机保留"); return;
        }
        if (p.Api == "VmImportService.PrepareBatchImportAsync()")
        {
            var prepared = await VmImportService.PreparePreviewsAsync(export, VmImportPlacementMode.HostDirectories); await using var batch = prepared.Batch;
            c.Require(prepared.Success && batch is not null, prepared.Result.Error);
            var result = await c.CallAsync(() => VmImportService.PrepareBatchImportAsync(batch!)); c.Require(result.Success, result.Error); c.Require(batch!.VirtualMachines.All(v => !v.IsRealized), "预检查保留预览，尚无新增虚拟机");
            using var fault = WmiApi.UseQuerySource((wql, _) => wql.Contains("Msvm_VirtualEthernetSwitch", StringComparison.Ordinal) ? new(new(ApiErrorKind.AccessDenied, 5, ApiErrorSource.Win32, "controlled-denial")) : null);
            var denied = await c.CallAsync(() => VmImportService.PrepareBatchImportAsync(batch)); c.Require(!denied.Success, "交换机来源读取失败传递"); return;
        }
        // Each import owns a reserved target root. Existing-directory registration uses
        // a separate exported copy; its source VM is removed before preserving its GUID.
        var modes = p.Id.EndsWith("-2", StringComparison.Ordinal)
            ? new[] { (VmImportPlacementMode.HostDirectories, VmImportGuidMode.GenerateNew) }
            : new[] { (VmImportPlacementMode.HostDirectories, VmImportGuidMode.GenerateNew), (VmImportPlacementMode.HostDirectories, VmImportGuidMode.Preserve), (VmImportPlacementMode.ExistingDirectory, VmImportGuidMode.Preserve) };
        foreach (var (placement, guidMode) in modes)
        {
            await using var target = TemporaryVm.Reserve(c);
            string input = zip;
            if (guidMode == VmImportGuidMode.Preserve) await source.DisposeAsync();
            if (placement == VmImportPlacementMode.ExistingDirectory)
            {
                // Register the exported VM directly under the reserved root. VMMS appends its own paging paths.
                input = target.Root;
                string registrationZip = Path.Combine(c.DirectoryPath, "registration.zip");
                ZipFile.CreateFromDirectory(Path.Combine(export, source.Name), registrationZip, CompressionLevel.Fastest, false);
                ZipFile.ExtractToDirectory(registrationZip, input);
            }
            var prepared = await VmImportService.PreparePreviewsAsync(input, new VmImportArchiveOptions { TemporaryDirectory = c.DirectoryPath }, placement); await using var batch = prepared.Batch;
            c.Require(prepared.Success && batch is not null, prepared.Result.Error);
            var session = batch!.VirtualMachines.Single(); string targetConfig = Path.Combine(target.Root, "config"), targetDisks = Path.Combine(target.Root, "disks");
            var options = new VmImportOptions(guidMode)
            { Name = target.Name, ConfigurationDirectory = placement == VmImportPlacementMode.ExistingDirectory ? null : targetConfig, DiskDirectory = placement == VmImportPlacementMode.ExistingDirectory ? null : targetDisks };
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(targetConfig); string sentinel = Path.Combine(targetConfig, "keep.txt"); File.WriteAllText(sentinel, "existing-data");
                var rejected = await c.CallAsync(() => VmImportService.ImportAsync(session, options)); c.RequireSettled(rejected.OperationStateUnknown, rejected.Error ?? "导入冲突"); c.Require(!rejected.Success && !rejected.IsRealized, "有内容的目标目录拒绝覆盖"); c.Equal("existing-data", File.ReadAllText(sentinel), "冲突目录内容保留");
                await using var missingTarget = TemporaryVm.Reserve(c);
                string missingSource = Path.Combine(missingTarget.Root, "source");
                ZipFile.ExtractToDirectory(zip, missingSource);
                var valid = await VmImportService.PreparePreviewsAsync(missingSource, placement);
                await using var missingBatch = valid.Batch; c.Require(valid.Success && missingBatch is not null, valid.Result.Error);
                var missingSession = missingBatch.VirtualMachines.Single();
                foreach (var d in missingSession.Preview.Disks)
                {
                    string owned = Path.GetFullPath(d.SourcePath);
                    c.Require(owned.StartsWith(missingTarget.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "缺盘样例限定在解包目录内");
                    File.Delete(owned);
                }
                var missing = await c.CallAsync(() => VmImportService.ImportAsync(missingSession, new(VmImportGuidMode.GenerateNew)
                { Name = missingTarget.Name, ConfigurationDirectory = Path.Combine(missingTarget.Root, "config"), DiskDirectory = Path.Combine(missingTarget.Root, "disks") }));
                if (missing.IsRealized && missing.VmId is Guid identity) missingTarget.RecordIdentity(identity);
                c.RequireSettled(missing.OperationStateUnknown, missing.Error ?? "缺盘导入终态");
                c.Require(!missing.Success && !missing.IsRealized, "预览后磁盘消失返回失败，尚无已注册的目标虚拟机");
                c.Equal("transfer-marker", await TemporaryDisk.MarkerAsync(disk), "缺盘导入保留原主机的源磁盘内容");
                continue;
            }
            var imported = await c.CallAsync(() => VmImportService.ImportAsync(session, options)); c.RequireSettled(imported.OperationStateUnknown, imported.Error ?? "导入终态");
            if (imported.IsRealized && imported.VmId is Guid registered) target.RecordIdentity(registered);
            c.Require(imported.Success && imported.IsRealized && imported.VmId.HasValue, imported.Error ?? "导入完成");
            c.Equal(guidMode == VmImportGuidMode.Preserve, imported.VmId == source.Id, "GUID 策略");
            var native = SystemBaseline.Vm(imported.VmId!.Value); c.Equal(target.Name, Convert.ToString(native["ElementName"]), "实际注册名称");
            string[] paths = await TemporaryDisk.PathsAsync(imported.VmId.Value); c.Equal(1, paths.Length, "实际磁盘数");
            c.Require(Path.GetFullPath(paths[0]).StartsWith(target.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "磁盘位于专属目录");
            c.Equal("transfer-marker", await TemporaryDisk.MarkerAsync(paths[0]), "导入后的磁盘内容");
            c.Equal("导入导出·原值", VmMetadataApiCases.ReadNative(imported.VmId.Value)["ApiTransferMarker"], "系统元数据随导入保留");
            var nic = await WindowsTestCommand.RunAsync("@{switches=@(Get-VM -Id $a.id|Get-VMNetworkAdapter|ForEach-Object {$_.SwitchId.ToString()})}|ConvertTo-Json", new { id = imported.VmId.Value });
            c.Sequence(new[] { network.Id.ToString() }, nic.GetProperty("switches").EnumerateArray().Select(v => v.GetString()), "导入后仍连接同一隔离交换机");
            await batch.DisposeAsync(); c.Equal(VmImportCleanupState.Completed, batch.CleanupState, "导入后的会话清理状态");
            c.Equal(0, batch.RetainedPaths.Count, "成功导入后的暂存残留为空");
            await batch.DisposeAsync(); c.Require(SystemBaseline.Vm(imported.VmId.Value).Count > 0, "释放批次保留已导入虚拟机");
        }
    }

    private static async Task ExportAsync(CatalogTest p, ApiCaseContext c, TemporaryVm source, string disk)
    {
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Checkpoint-VM -SnapshotName 'export-A'; $true|ConvertTo-Json", new { id = source.Id });
        var native = await WindowsTestCommand.RunAsync("@{ids=@(Get-VM -Id ([guid]$a.id)|Get-VMSnapshot|ForEach-Object {$_.Id.ToString()})}|ConvertTo-Json", new { id = source.Id });
        if (p.Api == "VmExportService.GetCheckpointsAsync()")
        {
            var result = await c.CallAsync(() => VmExportService.GetCheckpointsAsync(source.Id)); c.Require(result.HasData, result.Error);
            c.Sequence(native.GetProperty("ids").EnumerateArray().Select(v => Guid.Parse(v.GetString()!)).OrderBy(v => v), result.Data!.Select(v => Guid.Parse(v.Id)).OrderBy(v => v), "检查点 GUID"); c.Equal("export-A", result.Data!.Single().Name, "检查点名称"); return;
        }
        var checkpoints = await VmExportService.GetCheckpointsAsync(source.Id); c.Require(checkpoints.HasData, checkpoints.Error);
        bool runningCase = p.Id.EndsWith("-2", StringComparison.Ordinal);
        int outputIndex = 0;
        foreach (var spec in runningCase ? new[] { (true, VmExportCheckpointMode.None, false), (true, VmExportCheckpointMode.None, true) } : new[] { (true, VmExportCheckpointMode.All, false), (false, VmExportCheckpointMode.None, false), (true, VmExportCheckpointMode.Single, false) })
        {
            if (spec.Item3) await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Start-VM; $true|ConvertTo-Json", new { id = source.Id });
            string output = Path.Combine(c.DirectoryPath, $"e{++outputIndex}"); Directory.CreateDirectory(output);
            var result = await c.CallAsync(() => VmExportService.ExportAsync(source.Id, output, spec.Item1, [], spec.Item2, spec.Item2 == VmExportCheckpointMode.Single ? checkpoints.Data![0] : null, spec.Item3));
            c.RequireSettled(result.OperationStateUnknown, result.Error ?? "导出终态未知"); c.Require(result.Success && Directory.Exists(result.OutputPath), result.Error ?? "导出目录存在");
            var expectedConfig = spec.Item2 == VmExportCheckpointMode.Single
                ? Guid.Parse(native.GetProperty("ids").EnumerateArray().Single().GetString()!) : source.Id;
            c.Require(File.Exists(Path.Combine(result.OutputPath!, "Virtual Machines", $"{expectedConfig:D}.vmcx")),
                $"导出主配置使用{(spec.Item2 == VmExportCheckpointMode.Single ? "检查点" : "虚拟机")}身份：{expectedConfig}");
            var exportedDisks = Directory.EnumerateFiles(result.OutputPath!, "*", SearchOption.AllDirectories).Where(f => new[] { ".vhd", ".vhdx", ".avhdx" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToArray(); c.Equal(spec.Item1, exportedDisks.Length > 0, "磁盘包含规则");
            if (spec.Item1)
            {
                var preview = await VmImportService.PreparePreviewsAsync(result.OutputPath!, VmImportPlacementMode.HostDirectories); await using var batch = preview.Batch;
                c.Require(preview.Success && batch!.VirtualMachines.Single().Preview.Disks.All(d => d.Exists), preview.Result.Error + "；导出产物可预览且磁盘引用有效");
                await using var verify = TemporaryVm.Reserve(c);
                var imported = await WindowsTestCommand.RunAsync("""
                    $files=@(Get-ChildItem -LiteralPath $a.source -Filter *.vmcx -Recurse|Where-Object {$_.Directory.Name -eq 'Virtual Machines'})
                    if($files.Count -ne 1){throw '导出主配置文件数量异常'}
                    $target=Join-Path $a.root 'verify';$disks=Join-Path $target 'Disks';New-Item -ItemType Directory -Force -Path $target,$disks|Out-Null
                    $vm=Import-VM -Path $files[0].FullName -Copy -GenerateNewId -VirtualMachinePath $target -VhdDestinationPath $disks -SnapshotFilePath $target -SmartPagingFilePath $target
                    $vm|Rename-VM -NewName $a.name;$vm|Set-VM -AutomaticStartAction Nothing -AutomaticCheckpointsEnabled $false
                    @{id=$vm.Id.ToString();snapshots=@($vm|Get-VMSnapshot).Count}|ConvertTo-Json
                    """, new { source = result.OutputPath, root = verify.Root, name = verify.Name }, 180);
                verify.RecordIdentity(Guid.Parse(imported.GetProperty("id").GetString()!));
                c.Equal(spec.Item2 == VmExportCheckpointMode.All ? 1 : 0, imported.GetProperty("snapshots").GetInt32(), "系统重新导入后的检查点数量");
                c.Equal("transfer-marker", await TemporaryDisk.MarkerAsync((await TemporaryDisk.PathsAsync(verify.Id)).Single()), "系统重新导入后磁盘内容保持");
            }
            c.Equal(spec.Item3 ? (ushort)2 : (ushort)3, Convert.ToUInt16(SystemBaseline.Vm(source.Id)["EnabledState"]), "源运行状态保留");
        }
        c.Require(File.Exists(disk), "源磁盘保留");
    }
}
