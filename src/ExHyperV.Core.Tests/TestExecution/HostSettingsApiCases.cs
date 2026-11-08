using Microsoft.Win32;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostSettingsApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "HostGpuService.GetAssignmentRestrictionsRelaxed()" or "HostGpuService.SetAssignmentRestrictionsRelaxed()"
        or "HostOpenHclService.SetFirmwareLoadFromFileEnabled()" or "HyperVNumaService.GetNumaSpanningEnabledAsync()" or "HyperVNumaService.SetNumaSpanningEnabledAsync()"
        or "HyperVHostSettingsService.GetAsync()" or "HyperVHostSettingsService.SetDefaultVirtualMachinePathAsync()" or "HostDiskService.SetDefaultVirtualHardDiskPathAsync()"
        or "HostNetworkService.SetDynamicMacAddressRangeAsync()" or "HostConsoleService.SetEnhancedSessionModeEnabledAsync()" or "HostPowerPlanService.SetActivePowerPlan()"
        or "HostVolumeAutoMountService.GetState()" or "HostVolumeAutoMountService.SetEnabledAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var restore = new HostStateJournal(c);
        if (p.Api.StartsWith("HostGpuService.", StringComparison.Ordinal))
        {
            const string key = @"SOFTWARE\Policies\Microsoft\Windows\HyperV";
            string[] names = ["RequireSecureDeviceAssignment", "RequireSupportedDeviceAssignment"];
            foreach (var name in names) restore.Registry(key, name);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var policy = root.CreateSubKey(key);
            if (p.Api.Contains("GetAssignment"))
            {
                foreach (int bits in new[] { 0, 1, 2, 3 })
                {
                    for (int i = 0; i < 2; i++) { if ((bits & (1 << i)) != 0) policy.SetValue(names[i], 0, RegistryValueKind.DWord); else policy.DeleteValue(names[i], false); }
                    var actual = c.Call(HostGpuService.GetAssignmentRestrictionsRelaxed); c.Require(actual.HasData, actual.Error); c.Equal(bits == 3, actual.Data, "双策略存在性判定");
                }
                policy.SetValue(names[0], 1, RegistryValueKind.DWord); var restricted = c.Call(HostGpuService.GetAssignmentRestrictionsRelaxed); c.Require(restricted.HasData && !restricted.Data, "任一限制为 1 时返回默认限制");
            }
            else
            {
                bool enabled = p.Id == "T-fe0bf12c34caeca6-1";
                policy.SetValue(names[0], 7, RegistryValueKind.DWord); policy.SetValue(names[1], 9, RegistryValueKind.DWord);
                var result = c.Call(() => HostGpuService.SetAssignmentRestrictionsRelaxed(enabled)); c.Require(result.Success, result.Error);
                foreach (string name in names) c.Require(enabled ? policy.GetValue(name) is int v && v == 0 : !policy.GetValueNames().Contains(name), "策略原始注册表值：" + name);
            }
            return;
        }
        if (p.Api == "HostOpenHclService.SetFirmwareLoadFromFileEnabled()")
        {
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization"; const string value = "AllowFirmwareLoadFromFile";
            restore.Registry(key, value);
            foreach (bool enabled in new[] { true, false })
            { var result = c.Call(() => HostOpenHclService.SetFirmwareLoadFromFileEnabled(enabled)); c.Require(result.Success, result.Error); using var opened = Registry.LocalMachine.OpenSubKey(key); c.Require(enabled ? opened?.GetValue(value) is int v && v == 1 : opened?.GetValue(value) is null, "OpenHCL 开关原始值"); }
            return;
        }
        if (p.Api == "HostPowerPlanService.SetActivePowerPlan()")
        {
            var plans = await WindowsTestCommand.RunAsync("$s=powercfg /list; if($LASTEXITCODE){throw ($s -join ' ')}; @{ids=@([regex]::Matches(($s -join ' '),'[0-9a-fA-F-]{36}')|ForEach-Object {$_.Value})}|ConvertTo-Json");
            var ids = plans.GetProperty("ids").EnumerateArray().Select(v => Guid.Parse(v.GetString()!)).ToArray();
            if (ids.Length < 2) throw new ApiCheckSkippedException("宿主仅有一个电源计划，缺少可切换的第二个计划。");
            await restore.PowerAsync();
            foreach (var id in ids)
            { c.Call(() => HostPowerPlanService.SetActivePowerPlan(id)); var actual = await WindowsTestCommand.RunAsync("$s=powercfg /getactivescheme; [regex]::Match(($s -join ' '),'[0-9a-fA-F-]{36}').Value|ConvertTo-Json"); c.Equal(id, Guid.Parse(actual.GetString()!), "活动电源计划 GUID"); }
            return;
        }
        if (p.Api.StartsWith("HostVolumeAutoMountService.", StringComparison.Ordinal))
        {
            if (p.Api.Contains("SetEnabled")) restore.AutoMount();
            foreach (bool enabled in p.Api.Contains("SetEnabled") ? new[] { false, true } : Array.Empty<bool>())
            { var changed = await c.CallAsync(() => HostVolumeAutoMountService.SetEnabledAsync(enabled)); c.Require(changed.Success, changed.Error); c.Equal(enabled, HostStateJournal.ReadAutoMount(), "Mount Manager 持久值"); }
            if (p.Api.Contains("GetState")) { var read = c.Call(HostVolumeAutoMountService.GetState); c.Require(read.HasData, read.Error); c.Equal(HostStateJournal.ReadAutoMount(), read.Data, "自动挂载状态"); }
            return;
        }
        var before = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData").Single();
        if (p.Api == "HyperVHostSettingsService.GetAsync()")
        {
            var result = await c.CallAsync(HyperVHostSettingsService.GetAsync); c.Require(result is not null, "宿主配置存在");
            c.Equal(Convert.ToBoolean(before["EnhancedSessionModeEnabled"]), result!.EnhancedSessionModeEnabled, "增强会话");
            c.Equal(Convert.ToString(before.GetValueOrDefault("DefaultExternalDataRoot") ?? before.GetValueOrDefault("DefaultVirtualMachinePath")), result.DefaultVirtualMachinePath, "VM 默认目录");
            c.Equal(Convert.ToString(before["DefaultVirtualHardDiskPath"]), result.DefaultVirtualHardDiskPath, "VHD 默认目录");
            c.Equal(Convert.ToString(before["MinimumMacAddress"]), result.MinimumMacAddress, "MAC 下界"); c.Equal(Convert.ToString(before["MaximumMacAddress"]), result.MaximumMacAddress, "MAC 上界"); return;
        }
        if (p.Api == "HyperVNumaService.GetNumaSpanningEnabledAsync()")
        { var result = await c.CallAsync(HyperVNumaService.GetNumaSpanningEnabledAsync); c.Require(result.HasData, result.Error); c.Equal(Convert.ToBoolean(before["NumaSpanningEnabled"]), result.Data, "NUMA 跨节点"); return; }
        if (p.Api == "HostNetworkService.SetDynamicMacAddressRangeAsync()")
        {
            await restore.SettingAsync("MacAddressMinimum"); await restore.SettingAsync("MacAddressMaximum");
            var result = await c.CallAsync(() => HostNetworkService.SetDynamicMacAddressRangeAsync("02-15-5d-70-00-00", "02-15-5d-70-ff-ff")); c.Require(result.Success, result.Error);
            var actual = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData").Single(); c.Equal("02155D700000", Convert.ToString(actual["MinimumMacAddress"]), "规范化 MAC 下界"); c.Equal("02155D70FFFF", Convert.ToString(actual["MaximumMacAddress"]), "规范化 MAC 上界"); return;
        }
        if (p.Api.Contains("SetDefault"))
        {
            bool disk = p.Api.Contains("HardDisk"); await restore.SettingAsync(disk ? "VirtualHardDiskPath" : "VirtualMachinePath");
            string target = Path.Combine(c.DirectoryPath, disk ? "host-default-disks" : "host-default-vms"); Directory.CreateDirectory(target);
            var result = await c.CallAsync(() => disk ? HostDiskService.SetDefaultVirtualHardDiskPathAsync(target) : HyperVHostSettingsService.SetDefaultVirtualMachinePathAsync(target)); c.Require(result.Success, result.Error);
            var actual = await WindowsTestCommand.RunAsync("$v=Get-VMHost; @{vms=$v.VirtualMachinePath;disks=$v.VirtualHardDiskPath}|ConvertTo-Json"); c.Equal(target.TrimEnd('\\'), actual.GetProperty(disk ? "disks" : "vms").GetString()!.TrimEnd('\\'), "默认目录已读回"); return;
        }
        bool numa = p.Api.Contains("Numa");
        if (numa)
        {
            var active = await WindowsTestCommand.RunAsync("@{names=@(Get-VM|Where-Object {$_.State -notin @('Off','Saved')}|ForEach-Object {$_.Name})}|ConvertTo-Json", mayMutate: false);
            var names = active.GetProperty("names").EnumerateArray().Select(vm => vm.GetString()).ToArray();
            if (names.Length > 0)
                throw new ApiCheckSkippedException("开关 NUMA 跨节点需要宿主上的虚拟机全部关机或保存。当前仍活动：" + string.Join("、", names));
        }
        await restore.SettingAsync(numa ? "NumaSpanningEnabled" : "EnableEnhancedSessionMode");
        foreach (bool enabled in new[] { true, false })
        {
            var result = await c.CallAsync(() => numa ? HyperVNumaService.SetNumaSpanningEnabledAsync(enabled) : HostConsoleService.SetEnhancedSessionModeEnabledAsync(enabled)); c.Require(result.Success, result.Error);
            var actual = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData").Single(); c.Equal(enabled, Convert.ToBoolean(actual[numa ? "NumaSpanningEnabled" : "EnhancedSessionModeEnabled"]), "宿主开关已读回");
        }
    }
}
