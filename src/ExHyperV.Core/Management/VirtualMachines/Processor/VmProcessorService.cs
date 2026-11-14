using ExHyperV.Tools;
using ExHyperV.Models;
using System.Management;
using System.Runtime.InteropServices;

namespace ExHyperV.Services;

/// <summary>查询和修改虚拟机处理器配置。</summary>
/// <remarks>同一虚拟机的处理器和内存修改在 Core 进程之间依次执行。选中字段以本次请求值为准；一次操作失败时，已完成的修改可能保留。</remarks>
/// <example>
/// <para>将处理器数量改为 6：</para>
/// <code>
/// using ExHyperV.Models;
/// using ExHyperV.Services;
/// using ExHyperV.Tools;
/// public static class SelectedProcessorChangeExample
/// {
///     public static Task&lt;ApiResponse&gt; SetCountAsync(Guid vmId) =&gt;
///         VmProcessorService.SetVmProcessorAsync(vmId,
///             new VmProcessorSettings { Count = 6 }, [nameof(VmProcessorSettings.Count)]);
/// }
/// </code>
/// </example>
public static partial class VmProcessorService
{

    internal static async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return null;
        var response = await GetVmProcessorAsync(identity.Data);
        return response.HasData ? response.Data.Values : null;
    }


    internal static async Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VmProcessorSettings newSettings, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var response = await SetVmProcessorAsync(identity.Data, newSettings, changedProperties);
        return (response.Success, response.Error);
    }

    /// <summary>处理器配置规则所使用的宿主架构和硬件隔离能力。</summary>
    /// <param name="IsIntel">宿主是否为 Intel 平台。</param>
    /// <param name="IsArm64">宿主是否为 ARM64 平台。</param>
    /// <param name="HardwareIsolationSupported">是否支持 SNP 或 TDX 硬件隔离。</param>
    public sealed record HostCapabilities(bool IsIntel, bool IsArm64, bool HardwareIsolationSupported)
    {
        /// <summary>宿主是否为 x64 平台。</summary>
        public bool IsX64 => !IsArm64;
    }

    /// <summary>生成可供选择的虚拟处理器数量。</summary>
    /// <param name="logicalProcessorCount">宿主逻辑处理器总数，正整数。</param>
    /// <returns>按升序排列的 1、2、4、8 等数量，以及宿主逻辑处理器总数。例如总数为 12 时返回 [1, 2, 4, 8, 12]。</returns>
    internal static IReadOnlyList<int> GetProcessorCountOptions(int logicalProcessorCount)
    {
        var options = new SortedSet<int>();
        for (long count = 1; count <= logicalProcessorCount; count *= 2) options.Add((int)count);
        options.Add(logicalProcessorCount);
        return options.ToArray();
    }

    /// <summary>根据给定的平台和隔离信息生成宿主处理器能力。</summary>
    /// <param name="platform">已检测的宿主原生 CPU 平台。</param>
    /// <param name="isolationSupported">宿主是否报告硬件隔离支持。</param>
    /// <param name="isolationTypes">宿主提供的隔离类型名称；SNP 或 TDX 用于判定硬件隔离能力。</param>
    /// <returns>Intel、ARM64 和 SNP/TDX 硬件隔离支持标记。</returns>
    public static HostCapabilities GetHostCapabilities(HostProcessorPlatform platform, bool isolationSupported, IEnumerable<string> isolationTypes)
        => new(platform == HostProcessorPlatform.Intel, platform == HostProcessorPlatform.Arm64,
            isolationSupported && isolationTypes.Any(type => type is "SNP" or "TDX"));

    /// <summary>查询宿主处理器架构和硬件隔离能力。</summary>
    /// <returns>宿主能力，或查询失败的原因。</returns>
    public static async Task<ApiResponse<HostCapabilities>> GetHostCapabilitiesAsync()
    {
        try
        {
            var platform = await HostProcessorPlatformService.GetNativeHostPlatformAsync();
            var isolation = await VmCreateService.GetIsolationSupportAsync();
            if (!isolation.HasData)
                return ApiResponse<HostCapabilities>.Fail(isolation.Error, isolation.Code, isolation.ErrorSource,
                    isolation.Exception, isolation.ErrorKind);
            return ApiResponse<HostCapabilities>.Ok(GetHostCapabilities(platform, isolation.Data.Supported, isolation.Data.Types));
        }
        catch (Exception ex)
        {
            return ApiResponse<HostCapabilities>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    }

    /// <summary>当前操作系统是否为 ARM64 架构。</summary>
    public static bool IsArm64Host => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>检查计划修改的处理器配置是否满足电源状态、宿主架构和 PMU 依赖条件。</summary>
    /// <param name="current">从主机读取的当前处理器配置。</param>
    /// <param name="requested">计划应用的新处理器配置。</param>
    /// <param name="isPoweredOff">虚拟机是否已完全关机。</param>
    /// <param name="host">当前宿主架构和隔离能力。</param>
    /// <returns>发现的第一个问题；通过检查时为 null。</returns>
    internal static string? GetChangeError(VmProcessorSettings current, VmProcessorSettings requested,
        bool isPoweredOff, HostCapabilities host)
    {
        var result = ValidateProcessorChange(current, requested,
            isPoweredOff ? VmState.PoweredOff : VmState.Running, host);
        return result.Success ? null : result.Error;
    }

    internal static ApiResponse ValidateProcessorChange(VmProcessorSettings current, VmProcessorSettings requested,
        VmState state, HostCapabilities host)
    {
        foreach (var property in typeof(VmProcessorSettings).GetProperties())
        {
            if (!property.CanWrite || property.Name == nameof(VmProcessorSettings.SupportedProps)) continue;
            object? before = property.GetValue(current), after = property.GetValue(requested);
            if (property.Name == nameof(VmProcessorSettings.CpuBrandString))
            {
                before ??= "";
                after ??= "";
            }
            if (Equals(before, after)) continue;
            var condition = GetProcessorCondition(property.Name, state, host);
            if (!condition.CanModify) return condition.ToResponse();
        }

        return GetPerfmonCondition(current, requested).ToResponse();
    }

    /// <summary>评估一项处理器设置的修改条件。</summary>
    /// <param name="settings">已读取的配置或当前编辑草稿，保留查询返回的支持信息。</param>
    /// <param name="propertyName">要修改的处理器设置成员名。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <param name="host">查询得到的宿主处理器能力。</param>
    /// <returns>是否允许修改及原因。开关按切换到相反值评估，其他设置检查状态和平台条件。</returns>
    /// <remarks>使用传入的快照，不查询宿主。实际修改会重新读取配置并检查完整请求。</remarks>
    public static VmModificationCondition GetModificationCondition(VmProcessorSettings settings, string propertyName,
        VmState state, HostCapabilities host)
    {
        if (!ProcessorProperties.TryGetValue(propertyName, out var mapping))
            return VmModificationCondition.Denied("未知的处理器设置。", ApiErrorKind.InvalidArgument);
        if (!settings.SupportedProps.Contains(mapping.NativeName))
            return VmModificationCondition.Unsupported("当前宿主未提供此设置。");
        var condition = GetProcessorCondition(propertyName, state, host);
        if (!condition.CanModify) return condition;
        var requested = new VmProcessorSettings
        {
            EnablePerfmonPmu = settings.EnablePerfmonPmu,
            EnablePerfmonPebs = settings.EnablePerfmonPebs,
            EnablePerfmonIpt = settings.EnablePerfmonIpt
        };
        switch (propertyName)
        {
            case nameof(VmProcessorSettings.EnablePerfmonPmu): requested.EnablePerfmonPmu = settings.EnablePerfmonPmu != true; break;
            case nameof(VmProcessorSettings.EnablePerfmonPebs): requested.EnablePerfmonPebs = settings.EnablePerfmonPebs != true; break;
            case nameof(VmProcessorSettings.EnablePerfmonIpt): requested.EnablePerfmonIpt = settings.EnablePerfmonIpt != true; break;
            default: return condition;
        }
        return GetPerfmonCondition(settings, requested);
    }

    private static VmModificationCondition GetProcessorCondition(string propertyName, VmState state, HostCapabilities host)
    {
            if (state != VmState.PoweredOff && !IsRuntimeSetting(propertyName))
                return VmModificationCondition.PoweredOff(state);
            bool available = propertyName switch
            {
                nameof(VmProcessorSettings.CompatibilityForOlderOperatingSystemsEnabled) or
                nameof(VmProcessorSettings.CpuBrandString) or nameof(VmProcessorSettings.ApicMode) or
                nameof(VmProcessorSettings.AllowAcountMcount) => host.IsX64,
                nameof(VmProcessorSettings.LpiMode) => host.IsArm64,
                nameof(VmProcessorSettings.EnablePerfmonPmu) or nameof(VmProcessorSettings.EnablePerfmonLbr) or
                nameof(VmProcessorSettings.EnablePerfmonPebs) or nameof(VmProcessorSettings.EnablePerfmonIpt) => host.IsIntel,
                nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled) or nameof(VmProcessorSettings.MaxHwIsolatedGuests) => host.HardwareIsolationSupported,
                _ => true
            };
            if (!available) return VmModificationCondition.Unsupported();
        return VmModificationCondition.Allowed;
    }

    private static VmModificationCondition GetPerfmonCondition(VmProcessorSettings current, VmProcessorSettings requested)
    {
        int requestedDependents = (requested.EnablePerfmonPebs == true ? 1 : 0) + (requested.EnablePerfmonIpt == true ? 1 : 0);
        if (requestedDependents > 0 && requested.EnablePerfmonPmu != true)
        {
            int currentDependents = (current.EnablePerfmonPebs == true ? 1 : 0) + (current.EnablePerfmonIpt == true ? 1 : 0);
            if (!(current.EnablePerfmonPmu != true && requestedDependents < currentDependents))
                return VmModificationCondition.Denied(Properties.Resources.Cpu_PerfmonPmuRequired, ApiErrorKind.InvalidArgument);
        }
        return VmModificationCondition.Allowed;
    }

    private static bool IsRuntimeSetting(string propertyName) => propertyName is
        nameof(VmProcessorSettings.Reservation) or nameof(VmProcessorSettings.Limit) or nameof(VmProcessorSettings.RelativeWeight) or
        nameof(VmProcessorSettings.EnableHostResourceProtection) || IsPreviewFrequencySetting(propertyName);

    private static bool IsPreviewFrequencySetting(string propertyName) => propertyName is nameof(VmProcessorSettings.PerfCpuFreqMinMhz) or
        nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz) or nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference) or
        nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow) or nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency);

    /// <summary>读取虚拟机的处理器配置。</summary>
    /// <param name="vmId">目标虚拟机的标识。</param>
    /// <returns>处理器配置及支持的设置项。虚拟机或配置不存在时返回 NotFound；查询失败时附带原因。可空值的含义见 <see cref="VmProcessorSettings"/>。</returns>
    public static async Task<ApiResponse<VmSettingsSnapshot<VmProcessorSettings>>> GetVmProcessorAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<VmSettingsSnapshot<VmProcessorSettings>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        string query = $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";

        var results = await WmiApi.QueryAsync(query, vmEntry =>
        {
            using var settingData = WmiApi.GetVmSettings(vmEntry);
            if (settingData == null) return null;

            // Restrict association traversal to the current configuration's resources.
            // An unqualified traversal also visits snapshot/capability relationships.
            using var processors = settingData.GetRelated("Msvm_ProcessorSettingData",
                "Msvm_VirtualSystemSettingDataComponent", null, null, null, null, false, null);
            using var procData = processors.Cast<ManagementObject>().FirstOrDefault();
            if (procData == null) return null;

            return MapProcessorSnapshot(MapProcessor(procData),
                procData.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value));
        });

        if (!results.Success)
            return ApiResponse<VmSettingsSnapshot<VmProcessorSettings>>.Fail(results.Error, results.Code, results.ErrorSource, results.Exception, results.ErrorKind);
        var settings = results.Data?.FirstOrDefault();
        return settings == null
            ? ApiResponse<VmSettingsSnapshot<VmProcessorSettings>>.Fail(Properties.Resources.Error_Cpu_ConfigNotFound, kind: ApiErrorKind.NotFound)
            : ApiResponse<VmSettingsSnapshot<VmProcessorSettings>>.Ok(settings);
    }

    /// <summary>修改选中的处理器设置。</summary>
    /// <param name="vmId">目标虚拟机的标识。</param>
    /// <param name="newSettings">各项设置的新值。空值的写入规则见 <see cref="VmProcessorSettings"/>。</param>
    /// <param name="changedProperties">本次要修改的属性名，至少指定一项。</param>
    /// <returns>修改结果。需要关机时返回 InvalidState 和关机提示；平台不支持时返回 Unsupported，PMU 依赖不满足时返回 InvalidArgument。页拆分策略和 L3 缓存路数写入后回读：无法核实为 OutcomeUnknown，结果不一致为 Conflict。</returns>
    /// <remarks>Core 在提交前读取当前电源状态、处理器配置和宿主能力并检查修改条件。</remarks>
    /// <example><para>将处理器数量改为 4：</para><code>
    /// using System;
    /// using System.Threading.Tasks;
    /// using ExHyperV.Models;
    /// using ExHyperV.Services;
    ///
    /// public static class ProcessorFieldExample
    /// {
    ///     public static async Task SetCountAsync(Guid vmId)
    ///     {
    ///         var settings = new VmProcessorSettings { Count = 4 };
    ///         var result = await VmProcessorService.SetVmProcessorAsync(
    ///             vmId, settings, [nameof(VmProcessorSettings.Count)]);
    ///         if (!result.Success) throw new InvalidOperationException(result.Error);
    ///     }
    /// }
    /// </code></example>
    public static async Task<ApiResponse> SetVmProcessorAsync(
        Guid vmId, VmProcessorSettings newSettings, IReadOnlyCollection<string> changedProperties)
    {
        if (vmId == Guid.Empty || newSettings == null)
            return ApiResponse.Fail("虚拟机 GUID 和处理器配置不能为空。", kind: ApiErrorKind.InvalidArgument);
        SettingsSelection<VmProcessorSettings> selection;
        try { selection = SettingsSelection<VmProcessorSettings>.Capture(newSettings, changedProperties, ProcessorProperties.Keys); }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, kind: ApiErrorKind.InvalidArgument); }
        if (!selection.Names.Any(name => selection[name] != null || ProcessorProperties[name].AcceptNull)) return ApiResponse.Ok();
        bool gated = selection.Names.Any(IsPreviewFrequencySetting);
        // Hidden values must be visible when merging the snapshot, including when resetting a value to its default.
        return gated
            ? await HostAzureFeatureSetService.RunTemporarilyEnabledAsync(() =>
                VmSettingsWriteLock.RunAsync(vmId, () => ApplyProcessorSettingsAsync(vmId, selection)))
            : await VmSettingsWriteLock.RunAsync(vmId, () => ApplyProcessorSettingsAsync(vmId, selection));
    }

    private static async Task<ApiResponse> ApplyProcessorSettingsAsync(Guid vmId, SettingsSelection<VmProcessorSettings> selection)
    {
        try
        {
            var host = await GetHostCapabilitiesAsync();
            if (!host.HasData)
                return ApiResponse.Fail(host.Error, host.Code, host.ErrorSource, host.Exception, host.ErrorKind);
            string query = $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";
            ApiResponse? validation = null;
            ApiResponse? selectionError = null;
            bool noChanges = false;
            string? processorPath = null;
            var checkAfterWrite = new Dictionary<string, object?>();

            var xmlResults = await WmiApi.QueryAsync(query, vmEntry =>
            {
                var allSettings = vmEntry.GetRelated("Msvm_VirtualSystemSettingData")
                    .Cast<ManagementObject>().ToList();

                var settingData =
                    allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                 ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                if (settingData == null) return null;

                using var procData = settingData.GetRelated("Msvm_ProcessorSettingData")
                    .Cast<ManagementObject>().FirstOrDefault();
                if (procData == null) return null;

                // 仅对非 Realized 处理器设置写入虚拟处理器数量。
                var current = MapProcessor(procData);
                var newSettings = MergeProcessorSelection(current, selection);

                validation = ValidateProcessorChange(current, newSettings,
                    VmMapper.MapState(Convert.ToUInt16(vmEntry["EnabledState"])), host.Data);
                if (!validation.Success) return null;

                var writes = BuildProcessorWrites(current, newSettings, selection);
                if (writes.Count == 0) { noChanges = true; return null; }
                processorPath = procData.Path.Path;
                foreach (var (name, value) in writes)
                {
                    if (!procData.HasProperty(name))
                    {
                        selectionError = ApiResponse.Fail($"宿主不提供处理器属性 {name}。", kind: ApiErrorKind.Unsupported);
                        return null;
                    }
                    procData[name] = value;
                    // Some providers accept these optional settings but leave them unset.
                    if (name is "EnablePageShattering" or "L3CacheWays") checkAfterWrite[name] = value;
                }

                return procData.GetText(TextFormat.CimDtd20);
            });

            if (!xmlResults.Success)
                return ApiResponse.Fail(xmlResults.Error, xmlResults.Code, xmlResults.ErrorSource, xmlResults.Exception, xmlResults.ErrorKind);
            if (selectionError != null) return selectionError;
            if (validation is { Success: false }) return validation;
            if (noChanges) return ApiResponse.Ok();

            string? xml = xmlResults.Data?.FirstOrDefault();
            if (string.IsNullOrEmpty(xml))
                return ApiResponse.Fail(Properties.Resources.Error_Cpu_ConfigNotFound, kind: ApiErrorKind.NotFound);

            var result = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementService",
                "ModifyResourceSettings",
                p => p["ResourceSettings"] = new string[] { xml });

            if (!result.Success || checkAfterWrite.Count == 0) return result;
            var readBack = await WmiApi.GetByPathAsync(processorPath!,
                row => checkAfterWrite.Keys.ToDictionary(name => name, name => row[name]));
            if (!readBack.HasData)
                return ApiResponse.Fail("处理器设置已提交，无法核验实际值。" + readBack.Error, readBack.Code,
                    readBack.ErrorSource, readBack.Exception, ApiErrorKind.OutcomeUnknown);
            foreach (var (name, expected) in checkAfterWrite)
            {
                var actual = readBack.Data![name];
                if (actual is null) return ApiResponse.Fail($"宿主在提交后未提供 {name} 的值，无法确认修改结果。", kind: ApiErrorKind.OutcomeUnknown);
                if (!Equals(actual, expected))
                    return ApiResponse.Fail($"{name} 提交后读回 {actual}，预期 {expected}。", kind: ApiErrorKind.Conflict);
            }
            return result;
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(string.Format(Properties.Resources.VmProcessor_Exception, ex.Message),
                exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    }

    internal static VmSettingsSnapshot<VmProcessorSettings> MapProcessorSnapshot(VmProcessorSettings settings,
        IReadOnlyDictionary<string, object?> raw)
        => new(settings, ProcessorProperties.ToDictionary(pair => pair.Key,
            pair => SettingsSnapshot.State(raw, pair.Value.NativeName)), value => value.Clone());

    private static VmProcessorSettings MapProcessor(ManagementObject procData) =>
        new VmProcessorSettings
            {
                Count = Convert.ToInt32(procData["VirtualQuantity"]),
                Reservation = Convert.ToUInt64(procData["Reservation"]),
                Limit = Convert.ToUInt64(procData["Limit"]),
                RelativeWeight = Convert.ToInt32(procData["Weight"]),

                ExposeVirtualizationExtensions = procData.TryGet<bool>("ExposeVirtualizationExtensions"),
                CompatibilityForMigrationMode = (VmMigrationCompatibilityMode?)procData.TryGetByte("LimitProcessorFeaturesMode"),
                EnableHostResourceProtection = procData.TryGet<bool>("EnableHostResourceProtection"),
                CompatibilityForMigrationEnabled = procData.TryGet<bool>("LimitProcessorFeatures"),
                CompatibilityForOlderOperatingSystemsEnabled = procData.TryGet<bool>("LimitCPUID"),
                SmtMode = ConvertHwThreadsToSmtMode(procData.TryGet<ulong>("HwThreadsPerCore")),

                // 当前值与字段支持情况分别保留；空值不能当作关闭、零或默认模式。
                DisableSpeculationControls = procData.TryGet<bool>("DisableSpeculationControls"),
                EnablePerfmonArchPmu = procData.TryGet<bool>("EnablePerfmonArchPmu"),
                AllowAcountMcount = procData.TryGet<bool>("AllowAcountMcount"),
                CpuBrandString = procData.TryGetString("CpuBrandString"),

                ApicMode = (VmApicMode?)procData.TryGetByte("ApicMode"),
                L3CacheWays = procData.TryGet<uint>("L3CacheWays"),
                L3DistributionPolicy = (L3DistributionPolicy?)procData.TryGetByte("L3ProcessorDistributionPolicy"),
                PageShatterMode = (PageShatterMode?)procData.TryGetByte("EnablePageShattering"),

                // 保留现有未设置编码的归一规则，不为未返回的当前值补默认值。
                PerfCpuFreqCapMhz = Nz(procData.TryGet<uint>("PerfCpuFreqCapMhz")),
                PerfCpuFreqMinMhz = Nz(procData.TryGet<uint>("PerfCpuFreqMinMhz")),
                PerfCpuFreqDesiredMhz = Nz(procData.TryGet<uint>("PerfCpuFreqDesiredMhz")),
                PerfCpuEnergyPerformancePreference = Nz(procData.TryGet<uint>("PerfCpuEnergyPerformancePreference")),
                PerfCpuAutonomousActivityWindow = Nz(procData.TryGet<uint>("PerfCpuAutonomousActivityWindow")),
                PerfCpuIgnoreHostMaxFrequency = procData.TryGet<bool>("PerfCpuIgnoreHostMaxFrequency"),

                EnablePerfmonPmu = procData.TryGet<bool>("EnablePerfmonPmu"),
                EnablePerfmonLbr = procData.TryGet<bool>("EnablePerfmonLbr"),
                EnablePerfmonPebs = procData.TryGet<bool>("EnablePerfmonPebs"),
                EnablePerfmonIpt = procData.TryGet<bool>("EnablePerfmonIpt"),

                HardwareIsolationExtensionsEnabled = procData.TryGet<uint>("ExtendedVirtualizationExtensions") is { } isolation
                    ? isolation != 0u
                    : null,
                MaxHwIsolatedGuests = Nz(procData.TryGet<uint>("MaxHwIsolatedGuests")),
                MaxClusterCountPerSocket = Nz(procData.TryGet<uint>("MaxClusterCountPerSocket")),
                MaxProcessorCountPerL3 = Nz(procData.TryGet<uint>("MaxProcessorCountPerL3")),
                MaxProcessorsPerNumaNode = procData.TryGet<ulong>("MaxProcessorsPerNumaNode"),
                MaxNumaNodesPerSocket = procData.TryGet<ulong>("MaxNumaNodesPerSocket"),
                PhysicalAddressWidth = Nz(procData.TryGet<uint>("PhysicalAddressWidth")),
                LpiMode = (LpiMode?)procData.TryGetByte("LpiMode"),

                // 支持情况来自属性是否存在，与当前值是否已取得无关。
                SupportedProps = new HashSet<string>(
                    procData.Properties.Cast<PropertyData>().Select(p => p.Name), StringComparer.OrdinalIgnoreCase),
            };

    // 保留未设置编码的既有归一约定，实际零值仍为零。
    private static uint? Nz(uint? v) => v == uint.MaxValue ? (uint?)null : v;

    private static SmtMode? ConvertHwThreadsToSmtMode(ulong? hwThreads)
        => hwThreads switch
        {
            null => null,
            0 => SmtMode.Inherit,
            1 => SmtMode.SingleThread,
            _ => SmtMode.MultiThread,
        };

    private static uint ConvertSmtModeToHwThreads(SmtMode smtMode)
        => smtMode switch
        {
            SmtMode.Inherit => 0u,
            SmtMode.SingleThread => 1u,
            SmtMode.MultiThread => 2u,
            _ => 0u,
        };
}
