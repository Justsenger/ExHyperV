using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmStorageApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmStorageService.GetVirtualDiskSizesAsync()" or "VmStorageService.GetVmStorageItemsAsync()"
        or "VmStorageService.CompactDiskAsync()" or "VmStorageService.ModifyDvdDrivePathAsync()" or "VmStorageService.SetFullScsiCommandSetEnabledAsync()"
        || p.Id is "contract.storage-identity" or "T-6a85b8de6f18e415-3";

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (p.Api == "VmStorageService.SetFullScsiCommandSetEnabledAsync()")
        {
            var before = SystemBaseline.Settings(vm.Id); if (!before.ContainsKey("AllowFullSCSICommandSet")) throw new ApiCheckSkippedException("该宿主配置 schema 缺少完整 SCSI 命令集开关。");
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMSecurity -VirtualizationBasedSecurityOptOut $true; $true|ConvertTo-Json", new { id = vm.Id });
            foreach (bool value in new[] { true, false }) { var result = await c.CallAsync(() => VmStorageService.SetFullScsiCommandSetEnabledAsync(vm.Id, value)); Require(c, result, "完整 SCSI 命令集"); c.Equal(value, Convert.ToBoolean(SystemBaseline.Settings(vm.Id)["AllowFullSCSICommandSet"]), "系统开关读回"); }
            return;
        }
        if (p.Id == "contract.storage-identity")
        {
            var before = await TemporaryDisk.PathsAsync(vm.Id);
            foreach (var request in new[] { new VmStorageAddRequest { IsPhysical = true, PathOrNumber = "17" }, new() { IsPhysical = true, PhysicalDisk = new() { Number = 17 } }, new() { IsPhysical = true, PathOrNumber = "18", PhysicalDisk = new() { Number = 17, UniqueId = "fixture", SizeBytes = 1073741824 } } })
            { var result = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, request)); c.Equal(ApiErrorKind.InvalidArgument, result.ErrorKind, "物理盘身份参数校验"); c.Sequence(before, await TemporaryDisk.PathsAsync(vm.Id), "非法输入保持挂接清单"); }
            return;
        }
        if (p.Api is "VmStorageService.GetVirtualDiskSizesAsync()" or "VmStorageService.CompactDiskAsync()" or "VmStorageService.GetVmStorageItemsAsync()")
        {
            string disk = await TemporaryDisk.AttachAsync(vm, "storage-check");
            if (p.Api == "VmStorageService.GetVirtualDiskSizesAsync()")
            {
                string missing = Path.Combine(vm.Root, "missing.vhdx");
                var native = await WindowsTestCommand.RunAsync("$v=Get-VHD -Path $a.path; @{size=$v.Size;file=$v.FileSize}|ConvertTo-Json", new { path = disk });
                var result = await c.CallAsync(() => VmStorageService.GetVirtualDiskSizesAsync([disk, disk, missing]));
                c.Equal(native.GetProperty("file").GetInt64(), result[disk], "磁盘文件当前字节长度");
                c.Equal(new FileInfo(disk).Length, result[disk], "文件系统长度独立对照");
                c.Equal(1, result.Count, "重复路径合并");
                c.Require(!result.ContainsKey(missing), "缺失路径留在清单外");
                c.Require(native.GetProperty("size").GetInt64() != result[disk], "动态 VHD 样本区分逻辑容量与文件长度"); return;
            }
            if (p.Api == "VmStorageService.CompactDiskAsync()")
            {
                await WindowsTestCommand.RunAsync("""
                    Get-VM -Id ([guid]$a.id)|Get-VMHardDiskDrive|Remove-VMHardDiskDrive
                    $mounted=$false
                    try {$d=Mount-VHD -Path $a.path -NoDriveLetter -Passthru|Get-Disk;$mounted=$true;$volume=@($d|Get-Partition|Get-Volume|Where-Object FileSystemLabel -eq 'API_TEST')[0]
                    $bytes=New-Object byte[] (32MB);[Random]::new(17).NextBytes($bytes);$file=Join-Path $volume.Path 'reclaim.bin';[IO.File]::WriteAllBytes($file,$bytes);[IO.File]::Delete($file)
                    }finally{if($mounted){Dismount-VHD -Path $a.path}}
                    @{ok=$true}|ConvertTo-Json
                    """, new { id = vm.Id, path = disk }, 120);
                long before = new FileInfo(disk).Length; Require(c, await c.CallAsync(() => VmStorageService.CompactDiskAsync(disk)), "压缩虚拟磁盘");
                c.Require(new FileInfo(disk).Length <= before, "压缩后文件容量保持或减少"); c.Equal("storage-check", await TemporaryDisk.MarkerAsync(disk), "压缩后有效文件内容保持"); return;
            }
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Add-VMDvdDrive; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
            var actual = await c.CallAsync(() => VmStorageService.GetVmStorageItemsAsync(vm.Id)); c.Require(actual.HasData, "存储清单：" + actual.Error);
            var nativeDevices = await NativeAsync(vm.Id);
            c.Equal(nativeDevices.GetArrayLength(), actual.Data!.Count, "设备数量");
            foreach (var row in nativeDevices.EnumerateArray())
            {
                var item = actual.Data.Single(d => d.ControllerType == row.GetProperty("controller").GetString() && d.ControllerNumber == row.GetProperty("number").GetInt32() && d.ControllerLocation == row.GetProperty("location").GetInt32());
                c.Equal(row.GetProperty("type").GetString(), item.DriveType, "设备类型"); c.Equal(row.GetProperty("path").GetString() ?? "", item.PathOrDiskNumber, "介质路径");
                c.Require(!string.IsNullOrWhiteSpace(item.SelectionToken) && !string.IsNullOrWhiteSpace(item.DeviceToken), "可回传的设备与介质身份");
            }
            return;
        }
        string imageA = MakeIso(c, "A"), imageB = MakeIso(c, "B");
        if (p.Id == "T-6a85b8de6f18e415-3")
        {
            string source = Path.Combine(c.DirectoryPath, "source-C"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "marker.txt"), "C");
            foreach (var request in new[] { new VmStorageAddRequest { DriveType = "DvdDrive", PathOrNumber = imageA }, new() { DriveType = "DvdDrive", PathOrNumber = "", Location = 1 }, new() { DriveType = "DvdDrive", IsNew = true, Location = 2, IsoSourcePath = source, IsoOutputPath = Path.Combine(vm.Root, "C.iso") } })
            {
                var result = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, request)); c.Require(result.HasData, "添加光驱：" + result.Error);
                var native = (await NativeAsync(vm.Id)).EnumerateArray().Single(n => n.GetProperty("controller").GetString() == result.Data!.ControllerType && n.GetProperty("number").GetInt32() == result.Data.ControllerNumber && n.GetProperty("location").GetInt32() == result.Data.Location);
                c.Equal("DvdDrive", native.GetProperty("type").GetString(), "光驱类型"); c.Equal(request.IsNew ? request.IsoOutputPath : request.PathOrNumber, native.GetProperty("path").GetString() ?? "", "实际光驱介质");
                if (request.IsNew) c.Require(File.Exists(request.IsoOutputPath) && new FileInfo(request.IsoOutputPath!).Length > 0, "目录生成 ISO 文件");
            }
        }
        else
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Add-VMDvdDrive -Path $a.path; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, path = imageA });
            var read = await VmStorageService.GetVmStorageItemsAsync(vm.Id); c.Require(read.HasData, "准备光驱对象：" + read.Error); var item = read.Data!.Single(d => d.DriveType == "DvdDrive");
            if (p.Id.EndsWith("-3", StringComparison.Ordinal))
            { await vm.DisposeAsync(); var gone = await c.CallAsync(() => VmStorageService.ModifyDvdDrivePathAsync(vm.Id, item, imageB)); c.Require(!gone.Success, "消失虚拟机拒绝换盘"); return; }
            foreach (string path in p.Id.EndsWith("-2", StringComparison.Ordinal) ? new[] { imageA, imageA } : new[] { imageB, "", imageA })
            {
                Require(c, await c.CallAsync(() => VmStorageService.ModifyDvdDrivePathAsync(vm.Id, item, path)), "光驱换盘");
                var native = (await NativeAsync(vm.Id)).EnumerateArray().Single(n => n.GetProperty("type").GetString() == "DvdDrive"); c.Equal(path, native.GetProperty("path").GetString() ?? "", "光驱实际介质");
            }
            c.Require(File.Exists(imageA) && File.Exists(imageB), "换盘后两个 ISO 源文件保留");
        }
    }
    private static string MakeIso(ApiCaseContext c, string label)
    {
        string source = Path.Combine(c.DirectoryPath, "source-" + label), image = Path.Combine(c.DirectoryPath, label + ".iso"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "marker.txt"), label);
        IsoApiCases.OnSta(() => IsoBuilderService.BuildUdfIso(source, image, label)); return image;
    }
    private static Task<JsonElement> NativeAsync(Guid id) => WindowsTestCommand.RunAsync("""
        $v=Get-VM -Id ([guid]$a.id)
        $items=@($v|Get-VMHardDiskDrive|ForEach-Object {@{type='HardDisk';controller=$_.ControllerType.ToString();number=$_.ControllerNumber;location=$_.ControllerLocation;path=$_.Path}})
        $items+=@($v|Get-VMDvdDrive|ForEach-Object {@{type='DvdDrive';controller=$_.ControllerType.ToString();number=$_.ControllerNumber;location=$_.ControllerLocation;path=$_.Path}})
        ConvertTo-Json -InputObject $items
        """, new { id });
    private static void Require(ApiCaseContext c, ApiResponse r, string step) => c.Require(r.Success, step + "：" + r.Error);
}
