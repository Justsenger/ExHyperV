using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmConfigurationReadApiCases
{
    internal static async Task RunAsync(string api, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (api == "VmMemoryService.GetVmMemorySettingsAsync()")
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMMemory -DynamicMemoryEnabled $true -StartupBytes 1GB -MinimumBytes 256MB -MaximumBytes 2GB -Buffer 25 -Priority 73; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
            var raw = VmSettingsApiCases.Read(vm.Id, "Msvm_MemorySettingData");
            var result = await c.CallAsync(() => VmMemoryService.GetVmMemorySettingsAsync(vm.Id));
            c.Require(result.HasData, "读取内存设置：" + result.Error);
            var model = result.Data!;
            c.Equal(1024L, model.Startup, "1 GiB 转为 1024 MiB");
            c.Equal(256L, model.Minimum, "256 MiB 下限"); c.Equal(2048L, model.Maximum, "2 GiB 上限");
            c.Equal(25, model.Buffer, "25% 缓冲"); c.Equal(7300u, model.Weight, "优先级系统百分比转权重");
            c.Require(model.DynamicMemoryEnabled, "动态内存已开启");
            foreach (var name in new[] { "MemoryEncryptionPolicy", "EnableColdHint", "EnableHotHint", "EnableEpf", "EnablePrivateCompressionStore", "MaxMemoryBlocksPerNumaNode", "BackingType", "DynMemOperationAlignment", "MemoryAccessTrackingPolicy", "MemoryAccessTrackingState", "SgxEnabled", "SgxLaunchControlMode", "SgxLaunchControlDefault", "EnableGpaPinning", "CxlEnabled" })
                Compare(c, raw.GetValueOrDefault(name), typeof(VmMemorySettings).GetProperty(name)!.GetValue(model), name);
            Compare(c, raw.GetValueOrDefault("SgxSize") ?? 0UL, model.SgxSize, "SgxSize");
            Compare(c, raw.ContainsKey("BackingPageSize") ? raw["BackingPageSize"] ?? (byte)1 : Equals(raw.GetValueOrDefault("HugePagesEnabled"), true) ? (byte)2 : (byte)1, model.BackingPageSize, "后备页面大小与旧版兼容映射");
            c.Equal(raw.ContainsKey("BackingPageSize"), model.SupportsBackingPageSize, "后备页属性支持标记");
            return;
        }
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMProcessor -Count 2 -Reserve 10 -Maximum 70 -RelativeWeight 400; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
        var native = VmSettingsApiCases.Read(vm.Id, "Msvm_ProcessorSettingData");
        var response = await c.CallAsync(() => VmProcessorService.GetVmProcessorAsync(vm.Id));
        c.Require(response.HasData, "读取处理器设置：" + response.Error);
        var settings = response.Data!;
        c.Equal(2, settings.Count, "处理器数量"); c.Equal(10000UL, settings.Reservation, "10% 保留换算");
        c.Equal(70000UL, settings.Limit, "70% 限制换算"); c.Equal(400, settings.RelativeWeight, "权重");
        Compare(c, native.GetValueOrDefault("LimitProcessorFeatures"), settings.CompatibilityForMigrationEnabled, "迁移兼容");
        Compare(c, native.GetValueOrDefault("LimitCPUID"), settings.CompatibilityForOlderOperatingSystemsEnabled, "旧系统兼容");
        var renamed = new Dictionary<string, string> { ["CompatibilityForMigrationMode"] = "LimitProcessorFeaturesMode", ["L3DistributionPolicy"] = "L3ProcessorDistributionPolicy", ["PageShatterMode"] = "EnablePageShattering" };
        foreach (var name in new[] { "ExposeVirtualizationExtensions", "CompatibilityForMigrationMode", "EnableHostResourceProtection", "DisableSpeculationControls", "EnablePerfmonArchPmu", "AllowAcountMcount", "CpuBrandString", "ApicMode", "L3CacheWays", "L3DistributionPolicy", "PageShatterMode", "PerfCpuIgnoreHostMaxFrequency", "EnablePerfmonPmu", "EnablePerfmonLbr", "EnablePerfmonPebs", "EnablePerfmonIpt", "MaxProcessorsPerNumaNode", "MaxNumaNodesPerSocket", "LpiMode" })
        {
            string field = renamed.GetValueOrDefault(name, name);
            var property = typeof(VmProcessorSettings).GetProperty(name)!;
            object? expected = native.GetValueOrDefault(field);
            Compare(c, expected, property.GetValue(settings), name);
        }
        foreach (var name in new[] { "PerfCpuFreqCapMhz", "PerfCpuFreqMinMhz", "PerfCpuFreqDesiredMhz", "PerfCpuEnergyPerformancePreference", "PerfCpuAutonomousActivityWindow", "MaxHwIsolatedGuests", "MaxClusterCountPerSocket", "MaxProcessorCountPerL3", "PhysicalAddressWidth" })
        {
            object? value = native.GetValueOrDefault(name);
            Compare(c, value is null || Convert.ToUInt64(value) == uint.MaxValue ? null : value, typeof(VmProcessorSettings).GetProperty(name)!.GetValue(settings), name + "：未设置哨兵值");
        }
        Compare(c, native.GetValueOrDefault("ExtendedVirtualizationExtensions") is { } isolation ? Convert.ToUInt64(isolation) != 0 : null, settings.HardwareIsolationExtensionsEnabled, "硬件隔离扩展");
        int? smt = native.GetValueOrDefault("HwThreadsPerCore") is { } threads ? Convert.ToUInt64(threads) switch { 0 => 0, 1 => 1, _ => 2 } : null;
        Compare(c, smt, settings.SmtMode, "SMT 模式");
        c.Sequence(native.Keys.Order(StringComparer.OrdinalIgnoreCase), settings.SupportedProps.Order(StringComparer.OrdinalIgnoreCase), "完整属性支持集合");
    }

    private static void Compare(ApiCaseContext c, object? expected, object? actual, string label)
    {
        if (expected?.GetType().IsEnum == true) expected = Convert.ToInt32(expected);
        if (actual?.GetType().IsEnum == true) actual = Convert.ToInt32(actual);
        VmSettingsApiCases.EqualRaw(c, expected, actual, label);
    }
}
