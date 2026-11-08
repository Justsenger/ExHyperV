using System.Management;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmPlatformApiCases
{
    private sealed record SwitchCase(Func<Guid, bool, Task<ApiResponse>> Set, string Property, bool Security = false, bool Tpm = false);
    private static readonly Dictionary<string, SwitchCase> Switches = new()
    {
        ["VmSecurityService.SetSecureBootAsync()"] = new(VmSecurityService.SetSecureBootAsync, "SecureBootEnabled"),
        ["VmSecurityService.SetEncryptionAsync()"] = new(VmSecurityService.SetEncryptionAsync, "EncryptStateAndVmMigrationTraffic", true, true),
        ["VmSecurityService.SetTpmAsync()"] = new(VmSecurityService.SetTpmAsync, "TpmEnabled", true),
        ["VmSecurityService.SetShieldingAsync()"] = new(VmSecurityService.SetShieldingAsync, "ShieldingRequested", true, true),
        ["VmConsoleSupportService.SetLockOnDisconnectAsync()"] = new(VmConsoleSupportService.SetLockOnDisconnectAsync, "LockOnDisconnect"),
        ["VmPowerService.SetHibernationEnabledAsync()"] = new(VmPowerService.SetHibernationEnabledAsync, "EnableHibernation"),
        ["VmPowerService.SetTurnOffOnGuestRestartAsync()"] = new(VmPowerService.SetTurnOffOnGuestRestartAsync, "TurnOffOnGuestRestart"),
        ["VmSpacetimeService.SetCheckpointsEnabledAsync()"] = new(VmSpacetimeService.SetCheckpointsEnabledAsync, "UserSnapshotType"),
        ["VmBootService.SetBootNumLockAsync()"] = new(VmBootService.SetBootNumLockAsync, "BIOSNumLock")
    };
    internal static bool Supports(CatalogTest plan) => Switches.ContainsKey(plan.Api) || plan.Api is
        "VmConfigurationState.ReadAsync()" or "VmSecurityService.GetSecuritySettingsAsync()" or "VmSecurityService.GetSecureBootTemplatesAsync()"
        or "VmSecurityService.SetSecureBootTemplateAsync()" or "VmConsoleSupportService.GetConsoleStateAsync()"
        or "VmConsoleSupportService.IsConsoleSupportEnabledAsync()" or "VmConsoleSupportService.IsEnhancedSessionAvailableAsync()"
        or "VmConsoleSupportService.SetConsoleSupportAsync()" or "VmPowerService.GetBatteryStateAsync()"
        or "VmPowerService.SetBatteryEnabledAsync()" or "VmSpacetimeService.GetCheckpointsEnabledAsync()" or "VmBootService.GetBootNumLockAsync()";

    internal static async Task RunAsync(CatalogTest plan, ApiCaseContext c)
    {
        if (plan.Api == "VmSecurityService.GetSecureBootTemplatesAsync()") { await TemplatesAsync(c); return; }
        int n = int.TryParse(plan.Id.Split('-').Last(), out var number) ? number : 1;
        int generation = plan.Api == "VmBootService.SetBootNumLockAsync()" ? 1 : 2;
        await using var vm = await TemporaryVm.CreateAsync(c, generation);
        if (Switches.TryGetValue(plan.Api, out var test))
        {
            if (n == 3)
            {
                await vm.DisposeAsync();
                var result = await c.CallAsync(() => test.Set(vm.Id, false));
                c.Equal(ApiErrorKind.NotFound, result.ErrorKind, "已删除的目标身份"); return;
            }
            if (test.Tpm) await PrepareTpmAsync(vm.Id);
            else if (test.Property == "TpmEnabled") RequireGuardian();
            Dictionary<string, object?> Raw() => test.Security ? SystemBaseline.Security(vm.Id) : SystemBaseline.Settings(vm.Id);
            var before = Raw();
            if (!before.ContainsKey(test.Property) || before[test.Property] is null)
                throw new ApiCheckSkippedException($"宿主 Build {Environment.OSVersion.Version.Build} 未提供 {test.Property} 属性。");
            bool original = test.Property == "UserSnapshotType" ? Convert.ToUInt16(before[test.Property]) != 2 : Convert.ToBoolean(before[test.Property]);
            foreach (bool value in n == 2 ? new[] { original, original } : new[] { !original, original })
            {
                VmSettingsApiCases.Success(c, await c.CallAsync(() => test.Set(vm.Id, value)));
                var after = Raw();
                if (test.Property == "UserSnapshotType") c.Equal(value, Convert.ToUInt16(after[test.Property]) != 2, "检查点允许状态独立读回");
                else VmSettingsApiCases.EqualRaw(c, value, after[test.Property], test.Property + " 独立读回");
                if (test.Property == "ShieldingRequested" && value)
                {
                    c.Equal(true, Convert.ToBoolean(after["TpmEnabled"]), "屏蔽启用 TPM");
                    c.Equal(true, Convert.ToBoolean(after["EncryptStateAndVmMigrationTraffic"]), "屏蔽启用加密");
                }
                if (test.Property == "TpmEnabled" && value)
                {
                    var protector = await WindowsTestCommand.RunAsync("$bytes=Get-VM -Id ([guid]$a.id)|Get-VMKeyProtector; @{length=([byte[]]$bytes).Length}|ConvertTo-Json", new { id = vm.Id }, mayMutate: false);
                    c.Require(protector.GetProperty("length").GetInt32() > 4, "TPM 对应真实密钥保护器");
                }
            }
            return;
        }
        if (plan.Api == "VmSecurityService.SetSecureBootTemplateAsync()")
        {
            var templates = NativeTemplates();
            if (templates.Length == 0) throw new ApiCheckSkippedException("宿主安全启动模板清单为空。");
            if (n == 3)
            {
                await vm.DisposeAsync();
                c.Equal(ApiErrorKind.NotFound, (await c.CallAsync(() => VmSecurityService.SetSecureBootTemplateAsync(vm.Id, templates[0]))).ErrorKind, "已删除目标"); return;
            }
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMFirmware -EnableSecureBoot On; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
            foreach (Guid template in n == 2 ? new[] { templates[0], templates[0] } : templates)
            {
                VmSettingsApiCases.Success(c, await c.CallAsync(() => VmSecurityService.SetSecureBootTemplateAsync(vm.Id, template)));
                c.Equal(template, Guid.Parse(Convert.ToString(SystemBaseline.Settings(vm.Id)["SecureBootTemplateId"])!), "安全启动模板身份独立读回");
            }
            return;
        }
        if (plan.Api is "VmConsoleSupportService.SetConsoleSupportAsync()" or "VmPowerService.SetBatteryEnabledAsync()")
        {
            bool console = plan.Api.StartsWith("VmConsoleSupportService.", StringComparison.Ordinal);
            Func<Guid, bool, Task<ApiResponse>> setter = console ? VmConsoleSupportService.SetConsoleSupportAsync : VmPowerService.SetBatteryEnabledAsync;
            if (n == 3)
            {
                await vm.DisposeAsync();
                c.Equal(ApiErrorKind.NotFound, (await c.CallAsync(() => setter(vm.Id, false))).ErrorKind, "已删除目标"); return;
            }
            if (!console && !BatteryAvailable()) throw new ApiCheckSkippedException("宿主缺少虚拟电池默认设备模板。");
            foreach (bool enabled in n == 2 ? new[] { true, true } : new[] { false, true })
            {
                VmSettingsApiCases.Success(c, await c.CallAsync(() => setter(vm.Id, enabled)));
                if (console)
                {
                    c.Equal(enabled ? 1 : 0, Devices(vm.Id, "Msvm_SyntheticDisplayControllerSettingData").Count, "显示设备数量");
                    foreach (string subtype in new[] { "Microsoft:Hyper-V:Synthetic Keyboard", "Microsoft:Hyper-V:Synthetic Mouse" })
                        c.Equal(enabled ? 1 : 0, Devices(vm.Id, "Msvm_ResourceAllocationSettingData").Count(r => Equals(r["ResourceSubType"], subtype)), subtype);
                }
                else c.Equal(enabled ? 1 : 0, Devices(vm.Id, "Msvm_BatterySettingData").Count, "电池设备数量");
            }
            return;
        }
        await ReadAsync(plan.Api, vm, c);
    }

    private static async Task ReadAsync(string api, TemporaryVm vm, ApiCaseContext c)
    {
        switch (api)
        {
            case "VmConfigurationState.ReadAsync()":
                var state = await c.CallAsync(() => VmConfigurationState.ReadAsync(vm.Id));
                c.Require(state.HasData, "配置状态读取：" + state.Error);
                c.Equal(vm.Id, Guid.Parse(state.Data!.Id), "稳定身份"); c.Equal(2, state.Data!.Generation, "代数");
                c.Equal(VmState.PoweredOff, state.Data!.State, "关机状态"); c.Require(state.Data!.IsPoweredOff && !state.Data!.IsRunning, "活动状态标记"); break;
            case "VmSecurityService.GetSecuritySettingsAsync()":
                var security = await c.CallAsync(() => VmSecurityService.GetSecuritySettingsAsync(vm.Id));
                c.Require(security.HasData, "安全设置读取：" + security.Error);
                var native = SystemBaseline.Security(vm.Id); var settings = SystemBaseline.Settings(vm.Id);
                VmSettingsApiCases.EqualRaw(c, settings["SecureBootEnabled"], security.Data!.SecureBootEnabled, "安全启动");
                c.Equal(Guid.Parse(Convert.ToString(settings["SecureBootTemplateId"])!), Guid.Parse(security.Data!.SecureBootTemplateId), "模板身份");
                VmSettingsApiCases.EqualRaw(c, native["TpmEnabled"], security.Data!.TpmEnabled, "TPM");
                VmSettingsApiCases.EqualRaw(c, native["EncryptStateAndVmMigrationTraffic"], security.Data!.EncryptEnabled, "状态加密");
                VmSettingsApiCases.EqualRaw(c, native["ShieldingRequested"], security.Data!.Shielded, "屏蔽"); break;
            case "VmConsoleSupportService.GetConsoleStateAsync()":
                var console = await c.CallAsync(() => VmConsoleSupportService.GetConsoleStateAsync(vm.Id));
                c.Require(console.HasData, "控制台状态：" + console.Error); c.Equal(vm.Id, console.Data!.VmId, "控制台身份");
                c.Equal(vm.Name, console.Data!.Name, "名称"); c.Require(!console.Data!.IsRunning && !console.Data!.IsEnhancedAvailable, "关机时活动及增强会话状态"); break;
            case "VmConsoleSupportService.IsConsoleSupportEnabledAsync()":
                var enabled = await c.CallAsync(() => VmConsoleSupportService.IsConsoleSupportEnabledAsync(vm.Id));
                c.Require(enabled.HasData, "控制台支持读取：" + enabled.Error);
                c.Equal(Devices(vm.Id, "Msvm_SyntheticDisplayControllerSettingData").Count > 0, enabled.Data, "显示设备作为支持基准"); break;
            case "VmConsoleSupportService.IsEnhancedSessionAvailableAsync()":
                var enhanced = await c.CallAsync(() => VmConsoleSupportService.IsEnhancedSessionAvailableAsync(vm.Id));
                c.Require(enhanced.HasData, "增强会话读取：" + enhanced.Error);
                c.Equal(Convert.ToUInt16(SystemBaseline.Vm(vm.Id)["EnhancedSessionModeState"] ?? (ushort)3) == 2, enhanced.Data, "系统报告增强会话就绪值"); break;
            case "VmPowerService.GetBatteryStateAsync()":
                var battery = await c.CallAsync(() => VmPowerService.GetBatteryStateAsync(vm.Id));
                c.Require(battery.HasData, "电池状态：" + battery.Error); c.Equal(BatteryAvailable(), battery.Data!.Available, "默认模板可用性");
                c.Equal(BatteryAvailable() && Devices(vm.Id, "Msvm_BatterySettingData").Count > 0, battery.Data!.Enabled, "电池挂接状态"); break;
            case "VmSpacetimeService.GetCheckpointsEnabledAsync()":
                var snapshots = await c.CallAsync(() => VmSpacetimeService.GetCheckpointsEnabledAsync(vm.Id));
                c.Require(snapshots.HasData, "检查点许可：" + snapshots.Error);
                c.Equal(Convert.ToUInt16(SystemBaseline.Settings(vm.Id)["UserSnapshotType"]) != 2, snapshots.Data, "检查点策略"); break;
            case "VmBootService.GetBootNumLockAsync()":
                var numLock = await c.CallAsync(() => VmBootService.GetBootNumLockAsync(vm.Id));
                c.Require(numLock.HasData, "Num Lock：" + numLock.Error);
                VmSettingsApiCases.EqualRaw(c, SystemBaseline.Settings(vm.Id)["BIOSNumLock"], numLock.Data, "BIOSNumLock"); break;
            default: throw new InvalidDataException("平台检查点映射缺失：" + api);
        }
    }

    private static List<Dictionary<string, object?>> Devices(Guid id, string type) => SystemBaseline.Query($"SELECT * FROM {type} WHERE InstanceID LIKE 'Microsoft:{id:D}%'");
    private static bool BatteryAvailable()
    {
        try { return SystemBaseline.Query("SELECT * FROM Msvm_BatterySettingData WHERE InstanceID LIKE '%Default%'").Count > 0; }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.InvalidClass) { return false; }
    }
    private static Guid[] NativeTemplates() => SystemBaseline.Query("ASSOCIATORS OF {Msvm_VirtualSystemManagementCapabilities.InstanceID=\"Microsoft:VirtualSystemManagementCapabilities\"} WHERE AssocClass=Msvm_SettingsDefineCapabilities ResultClass=Msvm_VirtualSystemSettingData")
        .Select(r => Guid.TryParse(Convert.ToString(r.GetValueOrDefault("SecureBootTemplateId")), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray();
    private static async Task TemplatesAsync(ApiCaseContext c)
    {
        var response = await c.CallAsync(VmSecurityService.GetSecureBootTemplatesAsync);
        c.Require(response.HasData, "宿主安全启动模板：" + response.Error);
        c.Sequence(NativeTemplates().Order(), response.Data!.Select(t => t.Id).Order(), "模板 GUID 集合");
        c.Require(response.Data!.All(t => !string.IsNullOrWhiteSpace(t.Name)), "模板名称均可显示");
    }
    private static void RequireGuardian()
    {
        if (SystemBaseline.Query("SELECT * FROM MSFT_HgsGuardian WHERE Name='UntrustedGuardian'", @"root\microsoft\windows\hgs").Count == 0)
            throw new ApiCheckSkippedException("宿主尚未建立 UntrustedGuardian 及证书；请先完成宿主的虚拟 TPM 环境准备。");
    }
    private static async Task PrepareTpmAsync(Guid id)
    {
        RequireGuardian();
        await WindowsTestCommand.RunAsync("$vm=Get-VM -Id ([guid]$a.id); $vm|Set-VMKeyProtector -NewLocalKeyProtector; $vm|Enable-VMTPM; @{ready=$true}|ConvertTo-Json", new { id });
    }
}
