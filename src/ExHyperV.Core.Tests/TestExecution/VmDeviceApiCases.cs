using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmDeviceApiCases
{
    internal static bool Supports(CatalogTest plan) => plan.Api is "VmNetworkService.AddNetworkAdapterAsync()"
        or "VmNetworkService.RemoveNetworkAdapterAsync()" or "VmNetworkService.SetMacAddressAsync()"
        or "VmStorageService.RemoveDriveAsync()" or "VmStorageService.ModifyHardDrivePathAsync()"
        || plan.Api == "VmStorageService.AddDriveAsync()" && plan.Id is "T-6a85b8de6f18e415-1" or "T-6a85b8de6f18e415-2" or "T-6a85b8de6f18e415-6";

    internal static async Task RunAsync(CatalogTest plan, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (plan.Api.StartsWith("VmNetworkService.")) await NetworkAsync(plan, vm, c);
        else await StorageAsync(plan, vm, c);
    }

    private static async Task NetworkAsync(CatalogTest plan, TemporaryVm vm, ApiCaseContext c)
    {
        const string nicClass = "Msvm_SyntheticEthernetPortSettingData";
        if (plan.Api == "VmNetworkService.AddNetworkAdapterAsync()")
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMNetworkAdapter|Remove-VMNetworkAdapter -Confirm:$false; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
            c.Equal(0, VmSettingsApiCases.ReadMany(vm.Id, nicClass).Count, "起始网卡清单为空");
            var result = await c.CallAsync(() => VmNetworkService.AddNetworkAdapterAsync(vm.Id));
            c.Require(result.Success, $"创建结果 {result.ErrorKind}：{result.Error}；清理：{result.CleanupError}");
            c.Equal(VmNetworkAdapterCreationState.Present, result.AdapterState, "网卡已存在");
            c.Equal(VmNetworkAdapterInitializationState.Completed, result.InitializationState, "连接资源初始化完成");
            var actual = VmSettingsApiCases.ReadMany(vm.Id, nicClass);
            c.Equal(1, actual.Count, "系统清单恰好增加一张网卡");
            c.Require(result.DeviceId.HasValue && Convert.ToString(actual[0]["InstanceID"])!.Contains(result.DeviceId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase), "返回设备身份与实际网卡相符");
            return;
        }
        var adapters = await VmNetworkService.GetNetworkAdaptersAsync(vm.Id);
        c.Require(adapters.HasData && adapters.Data!.Count == 1, "准备一张断开交换机的测试网卡");
        var adapter = adapters.Data![0];
        if (plan.Api == "VmNetworkService.RemoveNetworkAdapterAsync()")
        {
            VmSettingsApiCases.Success(c, await c.CallAsync(() => VmNetworkService.RemoveNetworkAdapterAsync(vm.Id, adapter)));
            c.Equal(0, VmSettingsApiCases.ReadMany(vm.Id, nicClass).Count, "系统确认网卡已移除"); return;
        }
        int n = int.Parse(plan.Id.Split('-').Last());
        if (n == 3)
        {
            await vm.DisposeAsync();
            var result = await c.CallAsync(() => VmNetworkService.SetMacAddressAsync(vm.Id, adapter, "00155D123456"));
            c.Equal(ApiErrorKind.NotFound, result.ErrorKind, "已删除目标返回 NotFound"); return;
        }
        foreach (var mac in n == 1 ? new[] { "00:15:5d:12:34:56", "" } : new[] { "00155D123456", "00155D123456" })
        {
            var response = await c.CallAsync(() => VmNetworkService.SetMacAddressAsync(vm.Id, adapter, mac));
            if (!response.Success && mac.Length == 0)
            {
                c.RequireSettled(response.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, response.Error);
                var probe = await WindowsTestCommand.RunAsync("""
                    try {
                        Get-VM -Id ([guid]$a.id)|Get-VMNetworkAdapter|Set-VMNetworkAdapter -DynamicMacAddress
                        $nic=Get-VM -Id ([guid]$a.id)|Get-VMNetworkAdapter
                        @{accepted=$true;dynamic=$nic.DynamicMacAddressEnabled;mac=$nic.MacAddress}|ConvertTo-Json
                    } catch { @{accepted=$false;reason=$_.Exception.Message}|ConvertTo-Json }
                    """, new { id = vm.Id });
                c.Evidence.Add("系统 Set-VMNetworkAdapter 对照：" + probe.GetRawText());
                if (response.Code == 32773 && !probe.GetProperty("accepted").GetBoolean()
                    && probe.GetProperty("reason").GetString()?.Contains("00-00-00-00-00-00", StringComparison.Ordinal) == true)
                {
                    var retained = VmSettingsApiCases.ReadMany(vm.Id, nicClass).Single();
                    VmSettingsApiCases.EqualRaw(c, true, retained["StaticMacAddress"], "切换被拒后仍为静态模式");
                    VmSettingsApiCases.EqualRaw(c, "00155D123456", retained["Address"], "切换被拒后保留已写入的地址");
                    throw new ApiCheckSkippedException("静态 MAC 已核验；切回动态模式时，当前 VMMS 对 Core 和 Set-VMNetworkAdapter 都拒绝了全零动态地址。");
                }
            }
            VmSettingsApiCases.Success(c, response);
            var actual = VmSettingsApiCases.ReadMany(vm.Id, nicClass).Single();
            VmSettingsApiCases.EqualRaw(c, mac.Length > 0, actual["StaticMacAddress"], "静态地址模式");
            if (mac.Length > 0) c.Equal("00155D123456", Convert.ToString(actual["Address"])!.ToUpperInvariant(), "规范化 MAC 独立读回");
        }
    }

    private static async Task StorageAsync(CatalogTest plan, TemporaryVm vm, ApiCaseContext c)
    {
        string disk = Path.Combine(vm.Root, "disk.vhdx"), other = Path.Combine(vm.Root, "other.vhdx");
        int n = int.Parse(plan.Id.Split('-').Last());
        if (plan.Api == "VmStorageService.AddDriveAsync()" && n == 1)
        {
            int slot = 0;
            foreach (string type in new[] { "Dynamic", "Fixed" })
            {
                string path = Path.Combine(vm.Root, type + ".vhdx");
                var result = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, new() { IsNew = true, PathOrNumber = path, VhdType = type, SizeGiB = 1, Location = slot }));
                c.Require(result.HasData, "新磁盘挂接：" + result.Error);
                c.Equal(new VmStorageSlot("SCSI", 0, slot), result.Data, "返回槽位");
                await VerifyDiskAsync(vm.Id, path, slot, type, c); slot++;
            }
            return;
        }
        await WindowsTestCommand.RunAsync("New-VHD -Path $a.disk -Dynamic -SizeBytes 1GB|Out-Null; New-VHD -Path $a.other -Dynamic -SizeBytes 1GB|Out-Null; @{ready=$true}|ConvertTo-Json", new { disk, other });
        if (plan.Api == "VmStorageService.AddDriveAsync()" && n == 2)
        {
            var existing = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, new() { PathOrNumber = disk, Location = 0 }));
            c.Require(existing.HasData, "现有磁盘挂接：" + existing.Error);
            await VerifyDiskAsync(vm.Id, disk, 0, "Dynamic", c);
            string child = Path.Combine(vm.Root, "child.vhdx");
            var differencing = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, new() { IsNew = true, PathOrNumber = child, VhdType = "Differencing", ParentPath = other, Location = 1 }));
            c.Require(differencing.HasData, "差分磁盘挂接：" + differencing.Error);
            await VerifyDiskAsync(vm.Id, child, 1, "Differencing", c);
            var parent = await WindowsTestCommand.RunAsync("@{parent=(Get-VHD -Path $a.path).ParentPath}|ConvertTo-Json", new { path = child });
            c.Equal(other.ToUpperInvariant(), parent.GetProperty("parent").GetString()!.ToUpperInvariant(), "差分链父盘"); return;
        }
        await WindowsTestCommand.RunAsync("$vm=Get-VM -Id ([guid]$a.id); Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $a.disk; @{ready=$true}|ConvertTo-Json", new { id = vm.Id, disk });
        if (plan.Api == "VmStorageService.AddDriveAsync()")
        {
            var conflict = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, new() { PathOrNumber = other, Location = 0 }));
            c.Require(!conflict.Success, "已占用槽位返回失败：" + conflict.Error);
            var missing = await c.CallAsync(() => VmStorageService.AddDriveAsync(vm.Id, new() { PathOrNumber = Path.Combine(vm.Root, "missing.vhdx"), Location = 1 }));
            c.Require(!missing.Success, "失效路径返回失败：" + missing.Error);
            await VerifyDiskAsync(vm.Id, disk, 0, "Dynamic", c); return;
        }
        var items = await VmStorageService.GetVmStorageItemsAsync(vm.Id);
        c.Require(items.HasData, "读取用于操作的存储身份：" + items.Error);
        var drive = items.Data!.Single(x => x.DriveType == "HardDisk");
        if (plan.Api == "VmStorageService.RemoveDriveAsync()")
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Add-VMDvdDrive -ControllerNumber 0 -ControllerLocation 1; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
            var withDvd = await VmStorageService.GetVmStorageItemsAsync(vm.Id);
            c.Require(withDvd.HasData && withDvd.Data!.Count == 2, "准备硬盘和空光驱");
            foreach (var device in withDvd.Data!) VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.RemoveDriveAsync(vm.Id, device)));
            var check = await WindowsTestCommand.RunAsync("$vm=Get-VM -Id ([guid]$a.id); @{disks=@(Get-VMHardDiskDrive -VM $vm).Count;dvds=@(Get-VMDvdDrive -VM $vm).Count}|ConvertTo-Json", new { id = vm.Id });
            c.Equal(0, check.GetProperty("disks").GetInt32(), "硬盘清单为空"); c.Equal(0, check.GetProperty("dvds").GetInt32(), "光驱清单为空");
            c.Require(File.Exists(disk), "移除挂接保留磁盘文件"); return;
        }
        if (n == 3)
        {
            await vm.DisposeAsync();
            var missing = await c.CallAsync(() => VmStorageService.ModifyHardDrivePathAsync(vm.Id, drive, other));
            c.Equal(ApiErrorKind.NotFound, missing.ErrorKind, "已删除目标返回 NotFound"); return;
        }
        foreach (string path in n == 2 ? new[] { disk, disk } : new[] { other })
        {
            VmSettingsApiCases.Success(c, await c.CallAsync(() => VmStorageService.ModifyHardDrivePathAsync(vm.Id, drive, path)));
            await VerifyDiskAsync(vm.Id, path, 0, "Dynamic", c);
        }
    }

    private static async Task VerifyDiskAsync(Guid id, string path, int slot, string type, ApiCaseContext c)
    {
        var result = await WindowsTestCommand.RunAsync("$vm=Get-VM -Id ([guid]$a.id); $disks=@(Get-VMHardDiskDrive -VM $vm|Where-Object {$_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq $a.slot}); $vhd=Get-VHD -Path $a.path; @{count=$disks.Count;path=$disks[0].Path;type=$vhd.VhdType.ToString();size=$vhd.Size}|ConvertTo-Json", new { id, path, slot });
        c.Equal(1, result.GetProperty("count").GetInt32(), "指定槽位恰好一个挂接");
        c.Equal(path.ToUpperInvariant(), result.GetProperty("path").GetString()!.ToUpperInvariant(), "介质路径");
        c.Equal(type, result.GetProperty("type").GetString(), "独立读取 VHD 类型");
        c.Equal(1073741824L, result.GetProperty("size").GetInt64(), "虚拟容量字节");
    }
}
