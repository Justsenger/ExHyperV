using ExHyperV.Tools;
using ExHyperV.Models;
using System.Management;
using System.Runtime.InteropServices;

namespace ExHyperV.Services;

/// <summary>读取、校验和写入虚拟处理器配置及宿主相关高级选项。</summary>
/// <remarks>修改入口只读取 changedProperties 中的属性；同字段使用本次请求值，不比较调用方的旧快照或版本。
/// CPU 与内存写入按 VM 跨 Core 进程串行，锁覆盖重新读取与本次所有修改；不阻止 PowerShell 等外部工具，也不承诺失败时整体回滚。</remarks>
/// <example>
/// <para>只把处理器数量改为 6；无需先读取整份配置，也不会把模型中的其他默认值作为修改请求。</para>
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
        return response.HasData ? response.Data : null;
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
        /// <summary>按当前规则由非 ARM64 推导出的 x64 能力标记。</summary>
        public bool IsX64 => !IsArm64;
    }

    /// <summary>生成从 1 开始的二次幂选项，并包含宿主逻辑处理器总数。</summary>
    /// <param name="logicalProcessorCount">宿主逻辑处理器总数，调用者应提供正数。</param>
    /// <returns>去重后升序的处理器数量选项。</returns>
    public static IReadOnlyList<int> GetProcessorCountOptions(int logicalProcessorCount)
    {
        var options = new SortedSet<int>();
        for (long count = 1; count <= logicalProcessorCount; count *= 2) options.Add((int)count);
        options.Add(logicalProcessorCount);
        return options.ToArray();
    }

    /// <summary>根据宿主平台和隔离类型计算处理器设置所需的能力标记。</summary>
    /// <param name="platform">已检测的宿主原生 CPU 平台。</param>
    /// <param name="isolationSupported">宿主是否报告硬件隔离支持。</param>
    /// <param name="isolationTypes">宿主提供的隔离类型名称；SNP 或 TDX 用于判定硬件隔离能力。</param>
    /// <returns>Intel、ARM64 和 SNP/TDX 硬件隔离支持标记。</returns>
    public static HostCapabilities GetHostCapabilities(HostProcessorPlatform platform, bool isolationSupported, IEnumerable<string> isolationTypes)
        => new(platform == HostProcessorPlatform.Intel, platform == HostProcessorPlatform.Arm64,
            isolationSupported && isolationTypes.Any(type => type is "SNP" or "TDX"));

    /// <summary>读取宿主 CPU 平台和隔离支持后计算处理器配置能力。</summary>
    /// <returns>成功时返回当前宿主能力；查询异常返回失败原因。</returns>
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
    /// <summary>判断是否允许修改要求完全关机的处理器设置。</summary>
    /// <param name="isPoweredOff">是否已完全关机；保存、挂起或其他状态应传 false。</param>
    /// <returns>电源状态为关机时返回 true；运行、保存及挂起状态均返回 false。</returns>
    public static bool CanEditOfflineSettings(bool isPoweredOff) => isPoweredOff;
    /// <summary>判断 PMU 依赖功能能否切换；已启用项仍允许关闭。</summary>
    /// <param name="currentValue">当前 PMU 依赖项状态；null 表示未取得当前值，不代表不支持该属性。</param>
    /// <param name="pmu">当前 PMU 状态；null 表示未取得当前值，不代表不支持该属性。</param>
    /// <returns>该项已启用或 PMU 已启用时为 true。</returns>
    public static bool CanTogglePerfmonDependent(bool? currentValue, bool? pmu) => currentValue == true || pmu == true;
    /// <summary>判断 PMU 能否切换，防止在 PEBS 或 IPT 仍启用时关闭 PMU。</summary>
    /// <param name="pmu">当前 PMU 状态；null 表示未取得当前值，不代表不支持该属性。</param>
    /// <param name="pebs">当前 PEBS 状态；null 表示未取得当前值，不代表不支持该属性。</param>
    /// <param name="ipt">当前 IPT 状态；null 表示未取得当前值，不代表不支持该属性。</param>
    /// <returns>允许切换时为 true。</returns>
    public static bool CanTogglePerfmonPmu(bool? pmu, bool? pebs, bool? ipt) => pmu != true || (pebs != true && ipt != true);

    /// <summary>比较处理器设置，检查离线限制、宿主架构与 PMU 依赖。</summary>
    /// <param name="current">从主机读取的当前处理器配置。</param>
    /// <param name="requested">计划应用的新处理器配置。</param>
    /// <param name="isPoweredOff">是否已完全关机；保存、挂起或其他状态应传 false。</param>
    /// <param name="host">当前宿主架构和隔离能力。</param>
    /// <returns>首个限制说明；无当前检查错误时为 null。</returns>
    public static string? GetChangeError(VmProcessorSettings current, VmProcessorSettings requested,
        bool isPoweredOff, HostCapabilities host)
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
            if (!CanEditOfflineSettings(isPoweredOff) && !IsRuntimeSetting(property.Name))
                return Properties.Resources.Warn_VmOffRequired;
            bool available = property.Name switch
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
            if (!available) return Properties.Resources.Error_VerNotSupport;
        }

        int requestedDependents = (requested.EnablePerfmonPebs == true ? 1 : 0) + (requested.EnablePerfmonIpt == true ? 1 : 0);
        if (requestedDependents > 0 && requested.EnablePerfmonPmu != true)
        {
            int currentDependents = (current.EnablePerfmonPebs == true ? 1 : 0) + (current.EnablePerfmonIpt == true ? 1 : 0);
            if (!(current.EnablePerfmonPmu != true && requestedDependents < currentDependents))
                return Properties.Resources.Cpu_PerfmonPmuRequired;
        }
        return null;
    }

    private static bool IsRuntimeSetting(string propertyName) => propertyName is
        nameof(VmProcessorSettings.Reservation) or nameof(VmProcessorSettings.Limit) or nameof(VmProcessorSettings.RelativeWeight) or
        nameof(VmProcessorSettings.EnableHostResourceProtection) || IsPreviewFrequencySetting(propertyName);

    private static bool IsPreviewFrequencySetting(string propertyName) => propertyName is nameof(VmProcessorSettings.PerfCpuFreqMinMhz) or
        nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz) or nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference) or
        nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow) or nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency);

    /// <summary>读取处理器数量、资源限制和宿主支持的高级处理器属性。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功时返回处理器设置；虚拟机或配置不存在为 NotFound，查询失败保留错误；可空高级属性表示主机未提供数据。</returns>
    /// <remarks>可空字段未取得当前值时保留 null，不补为 false、0 或空字符串。宿主是否提供该字段由返回模型的 Supports 或 SupportedCapabilities 单独判断；字段存在不保证当前状态下允许修改。部分数值字段仍将底层未设置编码归一为 null，具体见设置模型说明。</remarks>
    public static async Task<ApiResponse<VmProcessorSettings>> GetVmProcessorAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<VmProcessorSettings>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        string query = $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";

        var results = await WmiApi.QueryAsync(query, vmEntry =>
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

            return MapProcessor(procData);
        });

        if (!results.Success)
            return ApiResponse<VmProcessorSettings>.Fail(results.Error, results.Code, results.ErrorSource, results.Exception, results.ErrorKind);
        var settings = results.Data?.FirstOrDefault();
        return settings == null
            ? ApiResponse<VmProcessorSettings>.Fail(Properties.Resources.Error_Cpu_ConfigNotFound, kind: ApiErrorKind.NotFound)
            : ApiResponse<VmProcessorSettings>.Ok(settings);
    }

    /// <summary>按当前宿主能力和虚拟机状态校验并写入处理器设置。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="newSettings">目标处理器配置；仅取 changedProperties 列出的属性；资源比例采用 0.001% 原始单位。</param>
    /// <param name="changedProperties">要提交的属性名集合，不能为空。例如仅修改处理器数量时包含 <c>nameof(VmProcessorSettings.Count)</c>。未选择字段保持取得虚拟机设置锁后读到的值。同字段按本次值覆盖，不比较旧值或版本。CPU 与内存设置在同一虚拟机上跨 Core 进程串行；不阻止外部工具修改。</param>
    /// <returns>成功响应或包含校验/原生执行原因的失败响应。页拆分和 L3 缓存路数写入后会再次读取，无法取得实际值时返回 OutcomeUnknown，读回值不同为 Conflict；不据空值推断该字段不受支持。</returns>
    /// <remarks>可空字段的 null 通常忽略，不写入。例外：CpuBrandString 的 null 清为空字符串；PerfCpuFreqDesiredMhz、PerfCpuEnergyPerformancePreference、PerfCpuAutonomousActivityWindow、MaxClusterCountPerSocket 和 MaxProcessorCountPerL3 的 null 写入底层未设置编码以恢复默认。未选择的字段始终不作为请求。全部选择均被忽略或与当前值相同则不提交配置。</remarks>
    /// <example><code>
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
            string? validationError = null;
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

                validationError = GetChangeError(current, newSettings, Convert.ToUInt16(vmEntry["EnabledState"]) == 3, host.Data);
                if (validationError != null) return null;

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
            if (!string.IsNullOrEmpty(validationError))
                return ApiResponse.Fail(validationError, kind: ApiErrorKind.InvalidState);
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
