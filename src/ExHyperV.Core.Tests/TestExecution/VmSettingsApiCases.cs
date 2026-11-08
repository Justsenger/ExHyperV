using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmSettingsApiCases
{
    private sealed record Field(string Model, string Native, object Value, bool Dynamic = false, bool X64 = false, object? Expected = null);
    // Reviewed values and units, independent of Core's mapping tables.
    private static readonly Dictionary<int, Field> Memory = new()
    {
        [1] = new("Startup", "VirtualQuantity", 1024L),
        [2] = new("Minimum", "Reservation", 256L, true),
        [3] = new("Maximum", "Limit", 4096L, true),
        [4] = new("Weight", "Weight", 6000u),
        [5] = new("Buffer", "TargetMemoryBuffer", 30, true),
        [6] = new("DynamicMemoryEnabled", "DynamicMemoryEnabled", true),
        [7] = new("BackingPageSize", "BackingPageSize", (byte)1),
        [8] = new("MemoryEncryptionPolicy", "MemoryEncryptionPolicy", (byte)1),
        [9] = new("EnableColdHint", "EnableColdHint", true),
        [10] = new("EnableHotHint", "EnableHotHint", true),
        [11] = new("EnableEpf", "EnableEpf", true),
        [12] = new("EnablePrivateCompressionStore", "EnablePrivateCompressionStore", true),
        [13] = new("MaxMemoryBlocksPerNumaNode", "MaxMemoryBlocksPerNumaNode", 2048ul),
        [14] = new("BackingType", "BackingType", (byte)1),
        [15] = new("DynMemOperationAlignment", "DynMemOperationAlignment", 2u, true),
        [16] = new("MemoryAccessTrackingPolicy", "MemoryAccessTrackingPolicy", (byte)1),
        [17] = new("SgxEnabled", "SgxEnabled", true, X64: true),
        [18] = new("SgxSize", "SgxSize", 4d, X64: true),
        [19] = new("SgxLaunchControlMode", "SgxLaunchControlMode", 1u, X64: true),
        [20] = new("SgxLaunchControlDefault", "SgxLaunchControlDefault", new string('0', 64), X64: true),
        [21] = new("EnableGpaPinning", "EnableGpaPinning", true),
        [22] = new("CxlEnabled", "CxlEnabled", true)
    };
    private static readonly Dictionary<int, Field> Processor = new()
    {
        [1] = new("Count", "VirtualQuantity", 2), [2] = new("Reservation", "Reservation", 1000ul),
        [3] = new("Limit", "Limit", 50000ul), [4] = new("RelativeWeight", "Weight", 200),
        [5] = new("CompatibilityForMigrationEnabled", "LimitProcessorFeatures", true),
        [6] = new("CompatibilityForMigrationMode", "LimitProcessorFeaturesMode", VmMigrationCompatibilityMode.MinimumFeatureSet, Expected: (byte)0),
        [7] = new("CompatibilityForOlderOperatingSystemsEnabled", "LimitCPUID", true, X64: true),
        [8] = new("ExposeVirtualizationExtensions", "ExposeVirtualizationExtensions", true),
        [9] = new("EnableHostResourceProtection", "EnableHostResourceProtection", true),
        [10] = new("EnablePerfmonPmu", "EnablePerfmonPmu", true, X64: true),
        [11] = new("EnablePerfmonArchPmu", "EnablePerfmonArchPmu", true),
        [12] = new("EnablePerfmonPebs", "EnablePerfmonPebs", true, X64: true),
        [13] = new("EnablePerfmonLbr", "EnablePerfmonLbr", true, X64: true),
        [14] = new("EnablePerfmonIpt", "EnablePerfmonIpt", true, X64: true),
        [15] = new("HardwareIsolationExtensionsEnabled", "ExtendedVirtualizationExtensions", true, Expected: 1u),
        [16] = new("SmtMode", "HwThreadsPerCore", SmtMode.SingleThread, Expected: 1ul),
        [17] = new("ApicMode", "ApicMode", VmApicMode.X2Apic, X64: true, Expected: (byte)2),
        [18] = new("CpuBrandString", "CpuBrandString", "ExHyperV API Test CPU", X64: true),
        [19] = new("DisableSpeculationControls", "DisableSpeculationControls", true),
        [20] = new("AllowAcountMcount", "AllowAcountMcount", true, X64: true),
        [21] = new("LpiMode", "LpiMode", LpiMode.Disabled, Expected: (byte)2),
        [22] = new("PageShatterMode", "EnablePageShattering", PageShatterMode.AlwaysEnabled, Expected: (byte)1),
        [23] = new("L3DistributionPolicy", "L3ProcessorDistributionPolicy", L3DistributionPolicy.LargeToSmall, Expected: (byte)1),
        [24] = new("L3CacheWays", "L3CacheWays", 1u),
        [25] = new("MaxClusterCountPerSocket", "MaxClusterCountPerSocket", 1u),
        [26] = new("MaxHwIsolatedGuests", "MaxHwIsolatedGuests", 1u),
        [27] = new("MaxNumaNodesPerSocket", "MaxNumaNodesPerSocket", 1ul),
        [28] = new("MaxProcessorCountPerL3", "MaxProcessorCountPerL3", 1u),
        [29] = new("MaxProcessorsPerNumaNode", "MaxProcessorsPerNumaNode", 1ul),
        [30] = new("PhysicalAddressWidth", "PhysicalAddressWidth", 40u),
        [31] = new("PerfCpuAutonomousActivityWindow", "PerfCpuAutonomousActivityWindow", 100u),
        [32] = new("PerfCpuEnergyPerformancePreference", "PerfCpuEnergyPerformancePreference", 50u),
        [33] = new("PerfCpuFreqCapMhz", "PerfCpuFreqCapMhz", 2000u),
        [34] = new("PerfCpuFreqDesiredMhz", "PerfCpuFreqDesiredMhz", 1000u),
        [35] = new("PerfCpuFreqMinMhz", "PerfCpuFreqMinMhz", 100u),
        [36] = new("PerfCpuIgnoreHostMaxFrequency", "PerfCpuIgnoreHostMaxFrequency", true)
    };

    internal static bool Supports(CatalogTest plan)
    {
        int n = Suffix(plan.Id);
        return plan.Api switch
        {
            "VmMemoryService.SetVmMemorySettingsAsync()" => Memory.ContainsKey(n) || n is 23 or 24,
            "VmProcessorService.SetVmProcessorAsync()" => Processor.ContainsKey(n) || n is 37 or 38,
            "VmEditService.RenameVmAsync()" => true,
            "VmMmioService.SetSettingAsync()" => true,
            "VmMmioService.GetSettingsAsync()" => true,
            "VmDisplaySettingsService.GetResolutionAsync()" => true,
            "VmDisplaySettingsService.SetResolutionAsync()" => true,
            _ => false
        };
    }

    internal static async Task RunAsync(CatalogTest plan, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        int n = Suffix(plan.Id);
        switch (plan.Api)
        {
            case "VmMemoryService.SetVmMemorySettingsAsync()":
                await MemoryAsync(vm, n, c); break;
            case "VmProcessorService.SetVmProcessorAsync()":
                await ProcessorAsync(vm, n, c); break;
            case "VmEditService.RenameVmAsync()":
                await RenameAsync(vm, n, c); break;
            case "VmDisplaySettingsService.SetResolutionAsync()":
                if (n == 2)
                {
                    var before = Read(vm.Id, "Msvm_SyntheticDisplayControllerSettingData");
                    foreach (var input in new[] { (0, 1280, 720), (3, 199, 720), (3, 7681, 720), (3, 1280, 4321) })
                    {
                        var invalid = await c.CallAsync(() => VmDisplaySettingsService.SetResolutionAsync(vm.Id, input.Item1, input.Item2, input.Item3));
                        c.Equal(ApiErrorKind.InvalidArgument, invalid.ErrorKind, "非法显示参数被拒绝");
                        Unchanged(c, before, Read(vm.Id, "Msvm_SyntheticDisplayControllerSettingData"), ["ResolutionType", "HorizontalResolution", "VerticalResolution"], []);
                    }
                    break;
                }
                foreach (var mode in new[] { 3, 4, 2 })
                {
                    var response = await c.CallAsync(() => VmDisplaySettingsService.SetResolutionAsync(vm.Id, mode, 1281, 721));
                    Success(c, response);
                    var raw = Read(vm.Id, "Msvm_SyntheticDisplayControllerSettingData");
                    EqualRaw(c, mode, raw["ResolutionType"], "显示策略");
                    if (mode == 3)
                    {
                        EqualRaw(c, 1280, raw["HorizontalResolution"], "宽度向下取偶数");
                        EqualRaw(c, 720, raw["VerticalResolution"], "高度向下取偶数");
                    }
                }
                break;
            case "VmDisplaySettingsService.GetResolutionAsync()":
            {
                var raw = Read(vm.Id, "Msvm_SyntheticDisplayControllerSettingData");
                var response = await c.CallAsync(() => VmDisplaySettingsService.GetResolutionAsync(vm.Id));
                c.Require(response.HasData, "取得显示设置：" + response.Error);
                EqualRaw(c, raw["ResolutionType"], response.Data!.ResolutionType, "显示策略");
                EqualRaw(c, raw["HorizontalResolution"], response.Data.Width, "宽度");
                EqualRaw(c, raw["VerticalResolution"], response.Data.Height, "高度");
                break;
            }
            case "VmMmioService.GetSettingsAsync()":
            {
                var raw = Read(vm.Id, "Msvm_VirtualSystemSettingData");
                var response = await c.CallAsync(() => VmMmioService.GetSettingsAsync(vm.Id));
                c.Require(response.HasData, "取得 MMIO 设置：" + response.Error);
                EqualRaw(c, raw["LowMmioGapSize"], response.Data!.LowSizeMiB, "低位空间 MiB");
                EqualRaw(c, raw["HighMmioGapSize"], response.Data.HighSizeMiB, "高位空间 MiB");
                EqualRaw(c, raw["HighMmioGapBase"], response.Data.HighBaseMiB, "高位基址 MiB");
                break;
            }
            case "VmMmioService.SetSettingAsync()":
                await MmioAsync(vm, n, c); break;
        }
    }

    private static async Task MemoryAsync(TemporaryVm vm, int n, ApiCaseContext c)
    {
        if (n == 24)
        {
            await vm.DisposeAsync();
            var missing = await c.CallAsync(() => VmMemoryService.SetVmMemorySettingsAsync(vm.Id, new() { Startup = 1024 }, false, [nameof(VmMemorySettings.Startup)]));
            c.Equal(ApiErrorKind.NotFound, missing.ErrorKind, "已删除目标返回 NotFound"); return;
        }
        var field = n == 23 ? Memory[4] : Memory[n];
        if (field.X64 && RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new ApiCheckSkippedException(field.Model + " 需要 x64 SGX 平台，当前为 ARM64。");
        if (n == 22 && RuntimeInformation.OSArchitecture == Architecture.Arm64 && Environment.OSVersion.Version.Build == 29671)
            throw new ApiCheckSkippedException("ARM64 Build 29671 的 CXL 写入已有 VMMS 崩溃记录；此组合隔离，等待问题修复后解除。");
        if (n is >= 9 and <= 12)
        {
            Success(c, await VmMemoryService.SetVmMemorySettingsAsync(vm.Id, new() { BackingType = 1 }, false, [nameof(VmMemorySettings.BackingType)]));
            EqualRaw(c, 1, Read(vm.Id, "Msvm_MemorySettingData")["BackingType"], "冷热提示与压缩功能的虚拟后备前提");
        }
        if (n == 18)
        {
            RequireField(Read(vm.Id, "Msvm_MemorySettingData"), "SgxEnabled");
            Success(c, await VmMemoryService.SetVmMemorySettingsAsync(vm.Id, new() { SgxEnabled = true }, false, [nameof(VmMemorySettings.SgxEnabled)]));
        }
        if (field.Dynamic)
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMMemory -DynamicMemoryEnabled $true -MinimumBytes 128MB -MaximumBytes 2GB -Buffer 20; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
        var before = Read(vm.Id, "Msvm_MemorySettingData");
        RequireField(before, field.Native);
        c.Evidence.Add($"写入前：动态内存={before["DynamicMemoryEnabled"]}，启动={before["VirtualQuantity"]}，保留={before["Reservation"]}，上限={before["Limit"]}。");
        Dictionary<string, object?>? systemExpected = null;
        if (field.Model == "Startup")
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMMemory -StartupBytes 1GB; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
            systemExpected = Read(vm.Id, "Msvm_MemorySettingData");
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VMMemory -StartupBytes 512MB; @{ready=$true}|ConvertTo-Json", new { id = vm.Id });
        }
        var request = new VmMemorySettings();
        object value = field.Value;
        if (value is bool && before[field.Native] is bool old) value = !old;
        if (n == 23) value = Convert.ToUInt32(before["Weight"]);
        typeof(VmMemorySettings).GetProperty(field.Model)!.SetValue(request, value);
        var response = await c.CallAsync(() => VmMemoryService.SetVmMemorySettingsAsync(vm.Id, request, false, [field.Model]));
        if (n == 21 && !response.Success && response.Code == 32770)
            await CheckProviderAsync(vm.Id, "Msvm_MemorySettingData", field, value, before[field.Native], response, c);
        Success(c, response);
        var after = Read(vm.Id, "Msvm_MemorySettingData");
        EqualRaw(c, value, after[field.Native], field.Model + " 独立读回");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { field.Native };
        if (field.Model is "Startup" or "DynamicMemoryEnabled")
        {
            allowed.UnionWith(["Reservation", "Limit", "TargetMemoryBuffer"]);
            if (field.Model == "Startup")
            {
                c.Evidence.Add($"系统命令保留动态内存参数：保留={systemExpected!["Reservation"]}，上限={systemExpected["Limit"]}；Core 静态内存约定同步这两个值到启动内存。");
                EqualRaw(c, value, after["Reservation"], "静态内存保留量随启动容量同步");
                EqualRaw(c, value, after["Limit"], "静态内存上限随启动容量同步");
            }
            else
            {
                var settings = Read(vm.Id, "Msvm_VirtualSystemSettingData");
                EqualRaw(c, false, settings["VirtualNumaEnabled"], "启用动态内存后关闭虚拟 NUMA");
            }
        }
        Unchanged(c, before, after, ["VirtualQuantity", "Reservation", "Limit", "Weight", "TargetMemoryBuffer", "DynamicMemoryEnabled"], allowed);
    }

    private static async Task ProcessorAsync(TemporaryVm vm, int n, ApiCaseContext c)
    {
        if (n == 38)
        {
            await vm.DisposeAsync();
            var missing = await c.CallAsync(() => VmProcessorService.SetVmProcessorAsync(vm.Id, new() { Count = 2 }, [nameof(VmProcessorSettings.Count)]));
            c.Equal(ApiErrorKind.NotFound, missing.ErrorKind, "已删除目标返回 NotFound"); return;
        }
        var field = n == 37 ? Processor[4] : Processor[n];
        if (field.X64 && RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new ApiCheckSkippedException($"{field.Model} 是 x64 处理器设置；当前宿主为 {RuntimeInformation.OSArchitecture}。");
        if (n == 21 && RuntimeInformation.OSArchitecture != Architecture.Arm64)
            throw new ApiCheckSkippedException("局部外设中断模式需要 ARM64 平台。");
        if (n is 10 or 12 or 13 or 14)
        {
            var processors = SystemBaseline.Query("SELECT Manufacturer FROM Win32_Processor", @"root\cimv2");
            if (!processors.Any(row => Convert.ToString(row["Manufacturer"])?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true))
                throw new ApiCheckSkippedException("PMU、PEBS、LBR 与 IPT 写入依赖 Intel 平台。");
        }
        if (n is 15 or 26)
        {
            var capabilities = await VmProcessorService.GetHostCapabilitiesAsync();
            c.Require(capabilities.HasData, "查询硬件隔离前提：" + capabilities.Error);
            if (!capabilities.Data!.HardwareIsolationSupported) throw new ApiCheckSkippedException("宿主未报告 SNP 或 TDX 硬件隔离能力。");
        }
        if (n is 12 or 14)
        {
            Success(c, await VmProcessorService.SetVmProcessorAsync(vm.Id, new() { EnablePerfmonPmu = true }, [nameof(VmProcessorSettings.EnablePerfmonPmu)]));
            EqualRaw(c, true, Read(vm.Id, "Msvm_ProcessorSettingData")["EnablePerfmonPmu"], "性能监测单元依赖已启用");
        }
        var before = Read(vm.Id, "Msvm_ProcessorSettingData"); RequireField(before, field.Native);
        if (n is 22 or 24 && before[field.Native] is null)
            throw new ApiCheckSkippedException($"宿主未返回 {field.Native} 的当前值，无法核验写入效果；本项尚未提交修改，留待能够读回该字段的环境测试。");
        object value = field.Value;
        if (value is bool && before[field.Native] is bool old) value = !old;
        if (n == 37) value = Convert.ToInt32(before["Weight"]);
        var request = new VmProcessorSettings();
        typeof(VmProcessorSettings).GetProperty(field.Model)!.SetValue(request, value);
        var response = await c.CallAsync(() => VmProcessorService.SetVmProcessorAsync(vm.Id, request, [field.Model]));
        if (n is 22 or 24 && response.ErrorKind == ApiErrorKind.Unsupported && before[field.Native] is null)
            await CheckProviderAsync(vm.Id, "Msvm_ProcessorSettingData", field, value, before[field.Native], response, c);
        if (!response.Success && field.Model is "CompatibilityForMigrationEnabled" or "ExposeVirtualizationExtensions")
        {
            var probe = await WindowsTestCommand.RunAsync("""
                $vm=Get-VM -Id ([guid]$a.id); $parameters=@{VM=$vm}; $parameters[$a.property]=[bool]$a.value
                try { Set-VMProcessor @parameters; @{accepted=$true}|ConvertTo-Json }
                catch { @{accepted=$false;reason=$_.Exception.Message}|ConvertTo-Json }
                """, new { id = vm.Id, property = field.Model, value });
            c.Evidence.Add($"Core 返回 {response.ErrorKind}：{response.Error}");
            if (!probe.GetProperty("accepted").GetBoolean())
            {
                EqualRaw(c, before[field.Native], Read(vm.Id, "Msvm_ProcessorSettingData")[field.Native], "平台拒绝后配置保持原值");
                throw new ApiCheckSkippedException("当前宿主与虚拟机版本拒绝此配置；系统 Set-VMProcessor 对照也失败：" + probe.GetProperty("reason").GetString());
            }
            c.Evidence.Add("系统 Set-VMProcessor 对照成功，Core 写入失败。");
        }
        Success(c, response);
        var after = n is 31 or 32 or 34 or 35 or 36
            ? await HostAzureFeatureSetService.RunTemporarilyEnabledAsync(() => Task.FromResult(Read(vm.Id, "Msvm_ProcessorSettingData")))
            : Read(vm.Id, "Msvm_ProcessorSettingData");
        EqualRaw(c, field.Expected ?? value, after[field.Native], field.Model + " 独立读回");
        Unchanged(c, before, after, ["VirtualQuantity", "Reservation", "Limit", "Weight", "LimitProcessorFeatures", "ExposeVirtualizationExtensions"], [field.Native]);
        if (n is 31 or 32 or 34)
        {
            var reset = await c.CallAsync(() => VmProcessorService.SetVmProcessorAsync(vm.Id, new VmProcessorSettings(), [field.Model]));
            Success(c, reset);
            var originalMode = await HostAzureFeatureSetService.RunTemporarilyEnabledAsync(() => Task.FromResult(Read(vm.Id, "Msvm_ProcessorSettingData")));
            c.Require(originalMode[field.Native] is null or uint.MaxValue, field.Model + " 已清除显式值");
        }
    }

    private static async Task RenameAsync(TemporaryVm vm, int n, ApiCaseContext c)
    {
        if (n == 3)
        {
            await vm.DisposeAsync();
            var result = await c.CallAsync(() => VmEditService.RenameVmAsync(vm.Id, "已删除目标"));
            c.Equal(ApiErrorKind.NotFound, result.ErrorKind, "已删除目标返回 NotFound"); return;
        }
        foreach (var name in n == 2 ? new[] { vm.Name, vm.Name } : new[] { "中文测试", "Test With Spaces", "Test'Quote\"Name" })
        {
            Success(c, await c.CallAsync(() => VmEditService.RenameVmAsync(vm.Id, name)));
            var raw = Read(vm.Id, "Msvm_VirtualSystemSettingData");
            c.Equal(name, Convert.ToString(raw["ElementName"]), "按 GUID 读取重命名结果");
        }
    }

    private static async Task MmioAsync(TemporaryVm vm, int n, ApiCaseContext c)
    {
        var names = new[] { ("LowSizeMiB", "LowMmioGapSize", 512ul), ("HighSizeMiB", "HighMmioGapSize", 32768ul), ("HighBaseMiB", "HighMmioGapBase", 65536ul) };
        if (n == 3)
        {
            await vm.DisposeAsync();
            var missing = await c.CallAsync(() => VmMmioService.SetSettingAsync(vm.Id, new() { LowSizeMiB = 512 }, nameof(VmMmioSettings.LowSizeMiB)));
            c.Equal(ApiErrorKind.NotFound, missing.ErrorKind, "已删除目标返回 NotFound"); return;
        }
        foreach (var field in names)
        {
            var before = Read(vm.Id, "Msvm_VirtualSystemSettingData"); RequireField(before, field.Item2);
            ulong value = n == 2 ? Convert.ToUInt64(before[field.Item2]) : field.Item3;
            var request = new VmMmioSettings(); typeof(VmMmioSettings).GetProperty(field.Item1)!.SetValue(request, value);
            for (int repeat = 0; repeat < (n == 2 ? 2 : 1); repeat++)
                Success(c, await c.CallAsync(() => VmMmioService.SetSettingAsync(vm.Id, request, field.Item1)));
            var after = Read(vm.Id, "Msvm_VirtualSystemSettingData"); EqualRaw(c, value, after[field.Item2], field.Item1);
            Unchanged(c, before, after, names.Select(x => x.Item2), [field.Item2]);
        }
    }

    internal static Dictionary<string, object?> Read(Guid id, string className)
        => ReadMany(id, className).FirstOrDefault() ?? throw new ApiCheckFailureException("独立系统查询未取得临时虚拟机配置：" + className);

    private static async Task CheckProviderAsync(Guid id, string className, Field field, object value,
        object? before, ApiResponse response, ApiCaseContext c)
    {
        c.RequireSettled(response.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, response.Error);
        // Compare against VMMS directly on this disposable VM. A working native write keeps the Core failure visible.
        var probe = await WindowsTestCommand.RunAsync("""
            $ns='root\virtualization\v2'
            $rows=@(Get-WmiObject -Namespace $ns -Query ("SELECT * FROM "+$a.className+" WHERE InstanceID LIKE 'Microsoft:"+$a.id+"%'"))
            if($rows.Count -ne 1){throw '独立核验未能唯一定位临时虚拟机的设置'}
            $row=$rows[0]; $row[$a.field]=$a.value
            $svc=Get-WmiObject -Namespace $ns -Class Msvm_VirtualSystemManagementService
            $argsIn=$svc.psbase.GetMethodParameters('ModifyResourceSettings')
            $argsIn['ResourceSettings']=[string[]]@($row.psbase.GetText([System.Management.TextFormat]::WmiDtd20))
            $reply=$svc.psbase.InvokeMethod('ModifyResourceSettings',$argsIn,$null)
            $code=$reply.ReturnValue
            if($code -eq 4096){
                $job=[wmi]$reply.Job
                while($job.JobState -in @(2,3,4,5,6)){Start-Sleep -Milliseconds 150; $job.psbase.Get()}
                if($job.JobState -eq 7){$code=$job.ErrorCode}
                elseif($job.JobState -eq 10 -and $job.ErrorCode){$code=$job.ErrorCode}
                else{throw ('独立系统作业未确认成功或失败：'+$job.JobState)}
            }
            @{code=[uint32]$code}|ConvertTo-Json
            """, new { id, className, field = field.Native, value = field.Expected ?? value });
        uint code = probe.GetProperty("code").GetUInt32();
        object? actual = Read(id, className)[field.Native];
        c.Evidence.Add($"直接向 VMMS 写入 {field.Native}，系统代码 {code}，读回 {actual ?? "空"}。");
        bool ignored = response.ErrorKind == ApiErrorKind.Unsupported && code == 0 && actual is null;
        bool rejected = response.Code == 32770 && code == 32770 && Equals(before, actual);
        if (ignored || rejected)
            throw new ApiCheckSkippedException($"当前宿主与虚拟机配置无法写入 {field.Native}。Core 和直接调用 VMMS 的结果已对照，实际值为 {actual ?? "空"}。");
    }

    internal static List<Dictionary<string, object?>> ReadMany(Guid id, string className)
    {
        using var search = new ManagementObjectSearcher("root/virtualization/v2", $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier='{id:D}' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'");
        using var settings = search.Get();
        foreach (ManagementObject system in settings) using (system)
        {
            if (className == "Msvm_VirtualSystemSettingData") return [Copy(system)];
            using var related = system.GetRelated(className);
            var result = new List<Dictionary<string, object?>>();
            foreach (ManagementObject row in related) using (row) result.Add(Copy(row));
            return result;
        }
        throw new ApiCheckFailureException("独立系统查询未取得临时虚拟机配置：" + className);
    }
    private static Dictionary<string, object?> Copy(ManagementObject value) => value.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
    private static void RequireField(Dictionary<string, object?> values, string field)
    {
        if (!values.ContainsKey(field))
            throw new ApiCheckSkippedException($"当前 Windows Build {Environment.OSVersion.Version.Build} 的 Hyper-V 未提供有效的 {field} 属性；此字段写入项跳过。");
    }
    internal static void Success(ApiCaseContext c, ApiResponse response)
    {
        c.RequireSettled(response.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, response.Error);
        c.Require(response.Success, $"Core 返回 {response.ErrorKind} / {response.ErrorSource} / {response.Code}：{response.Error}");
    }
    internal static void EqualRaw(ApiCaseContext c, object? expected, object? actual, string label) =>
        c.Equal(Convert.ToString(expected, CultureInfo.InvariantCulture), Convert.ToString(actual, CultureInfo.InvariantCulture), label);
    private static void Unchanged(ApiCaseContext c, Dictionary<string, object?> before, Dictionary<string, object?> after, IEnumerable<string> observed, IEnumerable<string> allowed)
    {
        var changed = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in observed.Where(key => !changed.Contains(key)))
            if (before.TryGetValue(key, out var value)) EqualRaw(c, value, after.GetValueOrDefault(key), "未选字段保持：" + key);
    }
    private static int Suffix(string id) => int.TryParse(id.Split('-').Last(), out int n) ? n : 0;
}
