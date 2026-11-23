using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmDiskMaintenanceApiCases
{
    internal static async Task RunAsync(string api, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (api == "VmStorageService.GetPassthroughCandidatesAsync()")
        {
            var result = await c.CallAsync(() => VmStorageService.GetPassthroughCandidatesAsync(vm.Id));
            c.Require(result.HasData, result.Error);
            var disks = await HostDiskService.GetHostDisksAsync();
            c.Require(disks.HasData, disks.Error);
            c.Sequence(disks.Data.Select(d => d.Number).Order(), result.Data.Where(x => x.Disk != null).Select(x => x.Disk!.Number).Order(), "宿主物理盘编号集合");
            c.Require(result.Data.All(x => (x.OpticalDrive == null) != (x.Disk == null)), "每个候选只对应一种设备");
            return;
        }

        bool chain = api is "VmStorageService.InspectDifferencingChainAsync()" or "VmStorageService.ReconnectParentDiskAsync()"
            or "VmStorageService.ExportDifferencingDiskAsync()" or "VmStorageService.MergeDifferencingDiskAsync()";
        string parent = Path.Combine(vm.Root, "base.vhdx");
        string path = Path.Combine(vm.Root, api == "VmStorageService.UpgradeVhdToVhdxAsync()" ? "media.vhd" : "media.vhdx");
        await WindowsTestCommand.RunAsync("""
            if ($a.chain) {
                New-VHD -Path $a.parent -SizeBytes 64MB -Dynamic | Out-Null
                New-VHD -Path $a.path -ParentPath $a.parent -Differencing | Out-Null
            } else {
                New-VHD -Path $a.path -SizeBytes 64MB -Dynamic | Out-Null
            }
            Get-VM -Id ([guid]$a.id) | Add-VMHardDiskDrive -Path $a.path -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
            $true | ConvertTo-Json
            """, new { id = vm.Id, path, parent, chain });
        var items = await VmStorageService.GetVmStorageItemsAsync(vm.Id);
        c.Require(items.HasData, items.Error);
        var drive = items.Data.Single(d => string.Equals(d.PathOrDiskNumber, path, StringComparison.OrdinalIgnoreCase));
        c.Require(drive.SelectionToken.Length > 0, "取得独立测试盘的挂接令牌");

        switch (api)
        {
            case "VmStorageService.InspectDiskAsync()":
                var inspected = await c.CallAsync(() => VmStorageService.InspectDiskAsync(vm.Id, drive));
                c.Require(inspected.HasData && inspected.Data.Kind == VmDiskAccessKind.Normal && inspected.Data.CanExpand,
                    "关机独占动态盘可扩展。" + inspected.Error);
                break;
            case "VmStorageService.InspectDifferencingChainAsync()":
                var checkedChain = await c.CallAsync(() => VmStorageService.InspectDifferencingChainAsync(path));
                c.Require(checkedChain.HasData && checkedChain.Data.IsComplete, checkedChain.Error);
                c.Sequence(new[] { path, parent }, checkedChain.Data.Nodes.Select(n => n.Path), "实际叶盘和父盘的顺序");
                break;
            case "VmStorageService.InspectResizeSafetyAsync()":
                var safety = await c.CallAsync(() => VmStorageService.InspectResizeSafetyAsync(vm.Id, drive));
                c.RequireSettled(safety.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, safety.Error);
                c.Require(safety.HasData && safety.Data.CanExpand, safety.Error);
                c.Equal<long?>(64L << 20, safety.Data.CurrentCapacityBytes, "当前虚拟容量");
                c.Require(!safety.Data.CanShrink, "64 MiB 样本无整 GiB 收缩空间");
                break;
            case "VmStorageService.ResizeVirtualDiskAsync()":
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.ResizeVirtualDiskAsync(vm.Id, drive, 1)));
                var size = await WindowsTestCommand.RunAsync("(Get-VHD -Path $a.path).Size | ConvertTo-Json", new { path });
                c.Equal(1L << 30, size.GetInt64(), "提供者回读扩展容量");
                break;
            case "VmStorageService.ModifyDriveSlotAsync()":
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.ModifyDriveSlotAsync(vm.Id, drive, "SCSI", 0, 8)));
                var slot = await WindowsTestCommand.RunAsync("(Get-VM -Id ([guid]$a.id) | Get-VMHardDiskDrive).ControllerLocation | ConvertTo-Json", new { id = vm.Id });
                c.Equal(8, slot.GetInt32(), "原介质迁移到指定槽位");
                break;
            case "VmStorageService.DeleteAttachedMediaFileAsync()":
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.DeleteAttachedMediaFileAsync(vm.Id, drive)));
                c.Require(!File.Exists(path) && (await TemporaryDisk.PathsAsync(vm.Id)).Length == 0, "介质文件和挂接均已移除");
                break;
            case "VmStorageService.ConvertVirtualDiskTypeAsync()":
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.ConvertVirtualDiskTypeAsync(vm.Id, drive, "Fixed")));
                string converted = (await TemporaryDisk.PathsAsync(vm.Id)).Single();
                var type = await WindowsTestCommand.RunAsync("(Get-VHD -Path $a.path).VhdType.ToString() | ConvertTo-Json", new { path = converted });
                c.Require(converted != path && File.Exists(path) && type.GetString() == "Fixed", "切换到固定盘并保留原文件");
                break;
            case "VmStorageService.UpgradeVhdToVhdxAsync()":
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.UpgradeVhdToVhdxAsync(vm.Id, drive)));
                c.Sequence(new[] { Path.ChangeExtension(path, ".vhdx") }, await TemporaryDisk.PathsAsync(vm.Id), "挂接转换后的 VHDX");
                c.Require(File.Exists(path), "原 VHD 保留");
                break;
            case "VmStorageService.ReconnectParentDiskAsync()":
                string copy = Path.Combine(vm.Root, "parent-copy.vhdx");
                File.Copy(parent, copy);
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.ReconnectParentDiskAsync(vm.Id, drive, copy, false)));
                var locator = await WindowsTestCommand.RunAsync("(Get-VHD -Path $a.path).ParentPath | ConvertTo-Json", new { path });
                c.Equal(copy, locator.GetString(), "直接父盘已切换到同身份副本");
                break;
            case "VmStorageService.ExportDifferencingDiskAsync()":
                string export = Path.Combine(vm.Root, "export.vhdx");
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.ExportDifferencingDiskAsync(vm.Id, drive, export)));
                var standalone = await WindowsTestCommand.RunAsync("$d=Get-VHD -Path $a.path; @{type=$d.VhdType.ToString();size=$d.Size;parent=$d.ParentPath}|ConvertTo-Json", new { path = export });
                c.Require(standalone.GetProperty("type").GetString() == "Dynamic" && standalone.GetProperty("size").GetInt64() == 64L << 20
                    && string.IsNullOrEmpty(standalone.GetProperty("parent").GetString()), "导出盘为独立动态盘且容量一致");
                c.Sequence(new[] { path }, await TemporaryDisk.PathsAsync(vm.Id), "原差分盘挂接保留");
                break;
            case "VmStorageService.MergeDifferencingDiskAsync()":
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.MergeDifferencingDiskAsync(vm.Id, drive, parent)));
                c.Sequence(new[] { parent }, await TemporaryDisk.PathsAsync(vm.Id), "合并后挂接祖先盘");
                c.Require(!File.Exists(path) && File.Exists(parent), "已消费的叶盘移除，祖先盘保留");
                break;
            default: throw new InvalidDataException(api);
        }
    }
}
