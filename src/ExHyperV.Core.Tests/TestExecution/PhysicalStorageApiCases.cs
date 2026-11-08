using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class PhysicalStorageApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmStorageService.FindStalePassthroughDisksAsync()" or "VmStorageService.DetachPhysicalDiskAsync()" || p.Id is "T-6a85b8de6f18e415-4" or "T-6a85b8de6f18e415-5";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Id == "T-6a85b8de6f18e415-5")
        {
            var ready = await TestDeviceResources.OpticalAsync(c.Configuration);
            if (ready.Status != InspectionStatus.Ready) throw new ApiCheckSkippedException(ready.Detail);
            await using var vm = await TemporaryVm.CreateAsync(c, 1);
            await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Get-VMDvdDrive|Remove-VMDvdDrive;@{ok=$true}|ConvertTo-Json", new { id = vm.Id });
            var selected = c.Configuration.OpticalDrive!;
            var drives = await HostDiskService.GetHostOpticalDrivesAsync(); c.Require(drives.HasData, drives.Error);
            var drive = drives.Data!.Single(d => d.PnpDeviceId.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
            var request = new VmStorageAddRequest { ControllerType = "IDE", ControllerNumber = 1, Location = 0, DriveType = "DvdDrive", IsPhysical = true, PathOrNumber = drive.PnpDeviceId };
            var response = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, request)); c.RequireSettled(response.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, response.Error); c.Require(response.HasData, response.Error);
            var native = await WindowsTestCommand.RunAsync("@{drives=@(Get-VM -Id $a.id|Get-VMDvdDrive|Select-Object ControllerNumber,ControllerLocation,Path)}|ConvertTo-Json -Depth 4", new { id = vm.Id });
            c.Require(native.GetProperty("drives").EnumerateArray().Any(v => v.GetProperty("ControllerNumber").GetInt32() == 1 && v.GetProperty("ControllerLocation").GetInt32() == 0), "第一代 IDE 光驱槽位存在");
            var actual = await VmStorageService.GetVmStorageItemsAsync(vm.Id); c.Require(actual.HasData && actual.Data!.Any(v => v.DriveType == "DvdDrive" && v.DiskType == "Physical"), "物理光驱身份回读"); return;
        }
        await using var physical = await PhysicalDiskLease.AcquireAsync(c);
        await using var target = await TemporaryVm.CreateAsync(c);
        var added = await c.CallAsync(() => VmStorageService.AddDriveAsync(target.Id, new() { IsPhysical = true, PhysicalDisk = physical.Disk, PathOrNumber = physical.Disk.Number.ToString(), Location = 0 }));
        c.RequireSettled(added.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, added.Error); c.Require(added.HasData, added.Error);
        var nativeDisk = await WindowsTestCommand.RunAsync("$v=Get-VM -Id $a.id|Get-VMHardDiskDrive; @{numbers=@($v|ForEach-Object DiskNumber);offline=(Get-Disk -Number $a.number).IsOffline}|ConvertTo-Json", new { id = target.Id, number = physical.Disk.Number });
        c.Require(nativeDisk.GetProperty("offline").GetBoolean(), "挂接时宿主磁盘离线"); c.Sequence(new[] { physical.Disk.Number }, nativeDisk.GetProperty("numbers").EnumerateArray().Select(v => v.GetInt32()), "实际物理盘号挂接");
        if (p.Api.Contains("FindStale"))
        {
            var healthy = await c.CallAsync(() => VmStorageService.FindStalePassthroughDisksAsync(target.Id)); c.Require(healthy.HasData && healthy.Data!.Count == 0, "完整物理盘挂接返回空失效清单");
            await WindowsTestCommand.RunAsync("Set-Disk -Number $a.number -IsOffline $false; @{ok=$true}|ConvertTo-Json", new { number = physical.Disk.Number });
            var stale = await c.CallAsync(() => VmStorageService.FindStalePassthroughDisksAsync(target.Id)); c.Require(stale.HasData, stale.Error); c.Require(stale.Data!.Count == 1 && stale.Data[0].IsPassthroughStale, "宿主联机后精确识别失效挂接");
            await WindowsTestCommand.RunAsync("Set-Disk -Number $a.number -IsOffline $true; @{ok=$true}|ConvertTo-Json", new { number = physical.Disk.Number });
        }
        if (p.Api.Contains("Detach"))
        {
            var detached = await c.CallAsync(() => VmStorageService.DetachPhysicalDiskAsync(target.Id, physical.Disk)); c.RequireSettled(detached.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, detached.Error); c.Require(detached.HasData, detached.Error);
            c.Equal(added.Data, detached.Data, "移除回执记录原槽位");
            var remaining = await WindowsTestCommand.RunAsync("@(Get-VM -Id $a.id|Get-VMHardDiskDrive).Count|ConvertTo-Json", new { id = target.Id }); c.Equal(0, remaining.GetInt32(), "VM 的物理磁盘挂接已移除");
        }
    }
}
