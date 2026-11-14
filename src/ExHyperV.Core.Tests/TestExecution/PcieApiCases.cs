using System.Text.Json;
using ExHyperV.Services;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class PcieApiCases
{
    private sealed record Journal(string InstanceId, string Path, Guid VmId);
    internal static bool Supports(CatalogTest p) => p.Api is "HostPcieService.ExecutePCIeOperationAsync()" or "VmPcieService.SetDeviceModeAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        var policy = HostGpuService.GetAssignmentRestrictionsRelaxed();
        if (!HostProductTypeService.IsServerSystem() && !(policy.HasData && policy.Data)) throw new ApiCheckSkippedException("PCIe 直通需要服务器系统或已开启的设备分配策略。");
        if (c.Configuration.PcieDevices.Length == 0) throw new ApiCheckSkippedException("第一步尚未选择 PCIe 测试设备。");
        var inventory = await HostPcieService.GetPCIeInfoAsync(); c.Require(inventory.HasData, inventory.Error);
        var selected = c.Configuration.PcieDevices.OrderBy(v => v.InstanceId, StringComparer.OrdinalIgnoreCase).First();
        var device = inventory.Data!.Devices.SingleOrDefault(v => v.InstanceId.Equals(selected.InstanceId, StringComparison.OrdinalIgnoreCase) && v.Path.Equals(selected.LocationPath, StringComparison.OrdinalIgnoreCase));
        if (device is null) throw new ApiCheckSkippedException("已选 PCIe 设备的身份或位置已变化，请在第一步重新选择。");
        if (device.Assignment.Kind != PcieAssignmentKind.Host) throw new ApiCheckSkippedException("已选设备当前归属为 " + device.Assignment.Kind + "；本项使用宿主拥有的闲置设备。");
        var disks = await HostPcieService.GetControllerDisksAsync(device.InstanceId); c.Require(disks.HasData, disks.Error);
        if (disks.Data!.Any(v => v.IsBoot || v.IsSystem)) throw new ApiCheckSkippedException("已选控制器连接着宿主系统盘或启动盘。");
        if (disks.Data!.Any(v => !v.IsOffline)) throw new ApiCheckSkippedException("已选控制器还有联机磁盘；请提前释放这些磁盘后再测直通。");
        await using var restore = new HostStateJournal(c); restore.ConfigurationElement(AppDataPaths.ConfigFilePath, "MmioCeilingMb");
        await using var vm = await TemporaryVm.CreateAsync(c);
        string path = Path.Combine(c.DirectoryPath, "pcie-device.json"); var journal = new Journal(device.InstanceId, device.Path, vm.Id);
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(journal)); File.Move(path + ".new", path, true);
        try
        {
            if (p.Id == "T-48b56efaf9a7073f-2")
            {
                var invalid = await c.CallAsync(() => HostPcieService.ExecutePCIeOperationAsync(PcieAssignmentTarget.ForVirtualMachine(vm.Id), PcieAssignmentTarget.Host, device.InstanceId, device.Path + "#changed"));
                c.Require(!invalid.Success, "错位位置路径拒绝转移"); c.RequireSettled(invalid.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, invalid.Error); return;
            }
            var moved = await c.CallAsync(() => HostPcieService.ExecutePCIeOperationAsync(PcieAssignmentTarget.ForVirtualMachine(vm.Id), device.Assignment, device.InstanceId, device.Path)); Require(c, moved);
            var native = await WindowsTestCommand.RunAsync("@{paths=@(Get-VM -Id $a.id|Get-VMAssignableDevice|ForEach-Object LocationPath)}|ConvertTo-Json", new { id = vm.Id }); c.Require(native.GetProperty("paths").EnumerateArray().Any(v => string.Equals(v.GetString(), device.Path, StringComparison.OrdinalIgnoreCase)), "系统直通列表包含准确位置路径");
            if (p.Api.StartsWith("VmPcieService."))
            {
                var settings = await VmPcieService.GetSettingsAsync(vm.Id); c.Require(settings.HasData, settings.Error);
                var target = settings.Data!.Devices.Single(v => v.Path.Equals(device.Path, StringComparison.OrdinalIgnoreCase));
                if (!target.GuestModeAvailable) throw new ApiCheckSkippedException("当前设备配置缺少 GuestPciExpressMode 属性。");
                if (p.Id.EndsWith("-3", StringComparison.Ordinal))
                {
                    await RestoreAsync(c.DirectoryPath); var old = await c.CallAsync(() => VmPcieService.SetDeviceModeAsync(vm.Id, target, target.GuestMode)); c.Equal(ApiErrorKind.Conflict, old.ErrorKind, "移除后的设备快照拒绝修改"); return;
                }
                foreach (var mode in Enum.GetValues<VmPcieGuestMode>())
                {
                    Require(c, await c.CallAsync(() => VmPcieService.SetDeviceModeAsync(vm.Id, target, mode)));
                    var row = SystemBaseline.Query("SELECT InstanceID,GuestPciExpressMode FROM Msvm_PciExpressSettingData").Single(v => Equals(v["InstanceID"], target.WmiInstanceId)); c.Equal((byte)mode, Convert.ToByte(row["GuestPciExpressMode"]), "系统设备模式读回");
                    if (p.Id.EndsWith("-2", StringComparison.Ordinal)) Require(c, await c.CallAsync(() => VmPcieService.SetDeviceModeAsync(vm.Id, target, mode)));
                }
            }
            else
            {
                var current = (await HostPcieService.GetPCIeInfoAsync()).Data!.Devices.Single(v => v.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase));
                Require(c, await c.CallAsync(() => HostPcieService.ExecutePCIeOperationAsync(PcieAssignmentTarget.Host, current.Assignment, device.InstanceId, device.Path)));
                var returned = (await HostPcieService.GetPCIeInfoAsync()).Data!.Devices.Single(v => v.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase)); c.Equal(PcieAssignmentKind.Host, returned.Assignment.Kind, "设备归还宿主");
            }
        }
        finally
        {
            if (!File.Exists(Path.Combine(c.DirectoryPath, "recovery-hold.txt")))
                try { await RestoreAsync(c.DirectoryPath); }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(c.DirectoryPath, "recovery-hold.txt"), "PCIe 归还宿主失败：" + error.Message);
                    throw;
                }
        }
    }
    private static void Require(ApiCaseContext c, ApiResponse result) { c.RequireSettled(result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, result.Error); c.Require(result.Success, "PCIe 操作：" + result.Error); }
    internal static async Task RestoreAsync(string directory)
    {
        string path = Path.Combine(directory, "pcie-device.json"); if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))!;
        var inventory = await HostPcieService.GetPCIeInfoAsync(); if (!inventory.HasData) throw new IOException(inventory.Error);
        var device = inventory.Data!.Devices.SingleOrDefault(v => v.InstanceId.Equals(journal.InstanceId, StringComparison.OrdinalIgnoreCase) && v.Path.Equals(journal.Path, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("PCIe 设备身份或位置变化，保留恢复记录。");
        if (device.Assignment.Kind == PcieAssignmentKind.VirtualMachine && device.Assignment.VirtualMachineId != journal.VmId) throw new IOException("PCIe 设备已转移至其他虚拟机，保留现场。");
        if (device.Assignment.Kind != PcieAssignmentKind.Host)
        {
            if (device.Assignment.VirtualMachineId is Guid vmId)
            {
                bool owned = Directory.GetFiles(directory, "temporary-vm.json", SearchOption.AllDirectories).Select(file => JsonSerializer.Deserialize<TemporaryVm.Journal>(File.ReadAllText(file), ResourceInspection.InspectionJson.Options)).Any(v => v?.Id == vmId);
                if (!owned) throw new IOException("PCIe 恢复 VM 归属记录缺失。");
                await WindowsTestCommand.RunAsync("$v=Get-VM -Id $a.id;if($v.State -ne 'Off'){$v|Stop-VM -TurnOff -Force};@{ok=$true}|ConvertTo-Json", new { id = vmId });
            }
            var returned = await HostPcieService.ExecutePCIeOperationAsync(PcieAssignmentTarget.Host, device.Assignment, device.InstanceId, device.Path);
            if (!returned.Success) throw new IOException("PCIe 归还宿主失败：" + returned.Error);
        }
        inventory = await HostPcieService.GetPCIeInfoAsync(); if (!inventory.HasData || !inventory.Data!.Devices.Any(v => v.InstanceId.Equals(journal.InstanceId, StringComparison.OrdinalIgnoreCase) && v.Assignment.Kind == PcieAssignmentKind.Host)) throw new IOException("PCIe 归还结果尚未确认。");
        File.Delete(path);
    }
}
