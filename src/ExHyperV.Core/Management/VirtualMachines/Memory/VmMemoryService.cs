using ExHyperV.Tools;
using ExHyperV.Models;
using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services;

/// <summary>读取、校验和应用内存设置，处理动态内存、后备页和 vNUMA 的联动。</summary>
public static class VmMemoryService
{

    internal static async Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return null;
        var response = await GetVmMemorySettingsAsync(identity.Data);
        return response.HasData ? response.Data.Values : null;
    }


    internal static async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings newSettings, bool isVmRunning, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var response = await SetVmMemorySettingsAsync(identity.Data, newSettings, isVmRunning, changedProperties);
        return (response.Success, response.Error);
    }

    /// <summary>读取虚拟机内存配置及当前主机可提供的高级内存属性。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功时返回内存设置；虚拟机或配置缺失为 NotFound，查询异常保留失败原因。可空扩展属性未取得值时保留 null，包括 SgxSize；字段级缺测状态通过自由查询取得。</returns>
    public static async Task<ApiResponse<VmSettingsSnapshot<VmMemorySettings>>> GetVmMemorySettingsAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<VmSettingsSnapshot<VmMemorySettings>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";
            var vmResponse = await WmiApi.QueryFirstAsync(vmWql, obj => obj["Name"]?.ToString());

            if (!vmResponse.Success)
                return ApiResponse<VmSettingsSnapshot<VmMemorySettings>>.Fail(vmResponse.Error, vmResponse.Code, vmResponse.ErrorSource,
                    vmResponse.Exception, vmResponse.ErrorKind);
            if (!vmResponse.HasData || string.IsNullOrEmpty(vmResponse.Data))
                return ApiResponse<VmSettingsSnapshot<VmMemorySettings>>.Fail(Properties.Resources.Error_Memory_VmNotFound, kind: ApiErrorKind.NotFound);

            string vmInstanceId = vmResponse.Data;
            string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmInstanceId}%' AND ResourceType = 4";

            var memResponse = await WmiApi.QueryFirstAsync(memWql, obj => MapMemorySnapshot(MapMemory(obj),
                obj.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value)));

            return memResponse.IsEmpty
                ? ApiResponse<VmSettingsSnapshot<VmMemorySettings>>.Fail(Properties.Resources.Error_Memory_VmNotFound, kind: ApiErrorKind.NotFound)
                : memResponse;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmMemoryService_ErrReadConfig, ex));
            return ApiResponse<VmSettingsSnapshot<VmMemorySettings>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    }

    internal static VmSettingsSnapshot<VmMemorySettings> MapMemorySnapshot(VmMemorySettings settings,
        IReadOnlyDictionary<string, object?> raw)
    {
        var states = typeof(VmMemorySettings).GetProperties().Where(p => p.SetMethod?.IsPublic == true)
            .ToDictionary(p => p.Name, p => SettingsSnapshot.State(raw,
                p.Name == nameof(VmMemorySettings.BackingPageSize) && !raw.ContainsKey("BackingPageSize")
                    ? "HugePagesEnabled" : MemoryPropertyName(p.Name)));
        return new(settings, states, value => value.Clone());
    }

    private static VmMemorySettings MapMemory(ManagementObject obj)
    {
        var s = new VmMemorySettings { SupportedProps = obj.Properties.Cast<PropertyData>().Select(p => p.Name).ToHashSet(StringComparer.Ordinal) };

        s.Startup = Convert.ToInt64(obj["VirtualQuantity"] ?? 0);
        s.Minimum = Convert.ToInt64(obj["Reservation"] ?? 0);
        s.Maximum = Convert.ToInt64(obj["Limit"] ?? 0);
        s.Weight = obj["Weight"] != null ? Convert.ToUInt32(obj["Weight"]) : 5000u;
        s.DynamicMemoryEnabled = Convert.ToBoolean(obj["DynamicMemoryEnabled"] ?? false);
        s.Buffer = obj["TargetMemoryBuffer"] != null ? Convert.ToInt32(obj["TargetMemoryBuffer"]) : 20;

        bool supportsBackingPageSize = obj.HasProperty("BackingPageSize");
        s.ConfigurePageSizeSupport(supportsBackingPageSize);

        // VMMS 内部只有一个页面大小值。新系统直接使用 0/1/2；旧系统只能通过
        // HugePagesEnabled 表达 2MB(false) 与 1GB(true)，在 WMI 边界转换为统一列表值。
        s.BackingPageSize = supportsBackingPageSize
            ? obj.TryGetByte("BackingPageSize") ?? 1
            : obj.TryGet<bool>("HugePagesEnabled") == true ? (byte)2 : (byte)1;
        s.MemoryEncryptionPolicy = obj.TryGetByte("MemoryEncryptionPolicy");

        s.EnableColdHint = obj.TryGet<bool>("EnableColdHint");
        s.EnableHotHint = obj.TryGet<bool>("EnableHotHint");
        s.EnableEpf = obj.TryGet<bool>("EnableEpf");
        s.EnablePrivateCompressionStore = obj.TryGet<bool>("EnablePrivateCompressionStore");

        s.MaxMemoryBlocksPerNumaNode = obj.TryGet<ulong>("MaxMemoryBlocksPerNumaNode");

        s.BackingType = obj.TryGetByte("BackingType");
        s.DynMemOperationAlignment = obj.TryGet<uint>("DynMemOperationAlignment");
        s.MemoryAccessTrackingPolicy = obj.TryGetByte("MemoryAccessTrackingPolicy");
        s.MemoryAccessTrackingState = obj.TryGetByte("MemoryAccessTrackingState");

        s.SgxEnabled = obj.TryGet<bool>("SgxEnabled");
        s.SgxSize = obj.TryGet<ulong>("SgxSize");
        s.SgxLaunchControlMode = obj.TryGet<uint>("SgxLaunchControlMode");
        s.SgxLaunchControlDefault = obj.TryGetString("SgxLaunchControlDefault");

        s.EnableGpaPinning = obj.TryGet<bool>("EnableGpaPinning");
        s.CxlEnabled = obj.TryGet<bool>("CxlEnabled");

        return s;
    }

    /// <summary>按运行状态计算出的内存字段编辑能力。</summary>
    /// <param name="OfflineSettings">是否允许修改要求离线的内存字段。</param>
    /// <param name="Startup">是否允许修改启动内存。</param>
    /// <param name="DynamicLimits">是否允许编辑动态内存上下限及缓冲。</param>
    internal readonly record struct Capabilities(bool OfflineSettings, bool Startup, bool DynamicLimits);
    /// <summary>根据运行状态和当前动态内存开关计算内存编辑能力。</summary>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <param name="dynamicMemoryEnabled">当前是否启用动态内存。</param>
    /// <returns>离线设置、启动内存和动态上下限是否允许编辑的标记。</returns>
    internal static Capabilities GetCapabilities(bool isRunning, bool dynamicMemoryEnabled)
        => new(!isRunning, !(isRunning && dynamicMemoryEnabled), dynamicMemoryEnabled);

    /// <summary>比较当前与请求设置，检查运行中的虚拟机是否允许这些内存变更。</summary>
    /// <param name="current">从主机读取的当前内存配置。</param>
    /// <param name="requested">用于比较的目标内存配置。</param>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <param name="changedProperty">只修改的 VmMemorySettings 属性名，建议使用 nameof；null 表示检查或规范化传入模型的全部相关属性；不提交宿主。</param>
    /// <returns>首个状态限制说明；无状态限制时为 null。数值范围在 WMI 提交时校验。</returns>
    internal static string? GetChangeError(VmMemorySettings current, VmMemorySettings requested, bool isRunning, string? changedProperty = null)
    {
        if (changedProperty != null)
        {
            var condition = GetModificationCondition(current, changedProperty, isRunning ? VmState.Running : VmState.PoweredOff);
            if (!condition.CanModify) return condition.Reason;
        }
        return ValidateMemoryChange(current, requested, isRunning ? VmState.Running : VmState.PoweredOff, changedProperty) is { Success: false } error ? error.Error : null;
    }

    /// <summary>评估一项内存设置的修改条件。</summary>
    /// <param name="settings">查询得到的内存配置。</param>
    /// <param name="propertyName">要修改的内存设置成员名。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改及原因。具体取值和配置组合在提交时校验。</returns>
    public static VmModificationCondition GetModificationCondition(VmMemorySettings settings, string propertyName, VmState state)
    {
        if (!SupportsSingleMemorySetting(propertyName))
            return VmModificationCondition.Denied("未知的内存设置。", ApiErrorKind.InvalidArgument);
        if (settings.SupportedProps is { } props && !props.Contains(MemoryPropertyName(propertyName))
            && !(propertyName == nameof(VmMemorySettings.BackingPageSize) && props.Contains("HugePagesEnabled")))
            return VmModificationCondition.Unsupported("当前宿主未提供此设置。");
        return GetMemoryStateCondition(settings, propertyName, state);
    }

    private static VmModificationCondition GetMemoryStateCondition(VmMemorySettings current, string propertyName, VmState state)
    {
        if (state == VmState.PoweredOff || propertyName == nameof(VmMemorySettings.Weight)) return VmModificationCondition.Allowed;
        if (propertyName == nameof(VmMemorySettings.Startup)) return current.DynamicMemoryEnabled
            ? VmModificationCondition.PoweredOff(state, Properties.Resources.Warn_DynRamRunning) : VmModificationCondition.Allowed;
        if (propertyName is nameof(VmMemorySettings.Minimum) or nameof(VmMemorySettings.Maximum) or nameof(VmMemorySettings.Buffer))
            if (current.DynamicMemoryEnabled) return VmModificationCondition.Allowed;
        return VmModificationCondition.PoweredOff(state);
    }

    internal static ApiResponse ValidateMemoryChange(VmMemorySettings current, VmMemorySettings requested, VmState state, string? changedProperty = null)
    {
        if (changedProperty != null && !SupportsSingleMemorySetting(changedProperty))
            return ApiResponse.Fail($"不支持单独修改内存字段 {changedProperty}。", kind: ApiErrorKind.InvalidArgument);
        foreach (var property in typeof(VmMemorySettings).GetProperties())
        {
            if (property.SetMethod?.IsPublic != true || (changedProperty != null && property.Name != changedProperty)) continue;
            if (Equals(property.GetValue(current), property.GetValue(requested))) continue;
            var condition = GetModificationCondition(current, property.Name, state);
            if (!condition.CanModify) return condition.ToResponse();
        }
        return ApiResponse.Ok();
    }

    private static string MemoryPropertyName(string field) => field switch
    {
        nameof(VmMemorySettings.Startup) => "VirtualQuantity",
        nameof(VmMemorySettings.Minimum) => "Reservation",
        nameof(VmMemorySettings.Maximum) => "Limit",
        nameof(VmMemorySettings.Buffer) => "TargetMemoryBuffer",
        _ => field
    };

    /// <summary>调整传入的内存后备页设置及其互斥项；指定字段时保留用户最后修改的选择，null 使用整组应用规则。</summary>
    /// <param name="settings">要调整的内存配置对象；直接修改该对象的互斥项。</param>
    /// <param name="changedProperty">只修改的 VmMemorySettings 属性名，建议使用 nameof；null 表示检查或规范化传入模型的全部相关属性；不提交宿主。</param>
    internal static void NormalizeMemoryBackingSettings(VmMemorySettings settings, string? changedProperty)
    {
        bool backingTypeChanged = changedProperty == nameof(VmMemorySettings.BackingType);
        bool pageSizeChanged = changedProperty == nameof(VmMemorySettings.BackingPageSize);
        bool backingFeatureChanged = changedProperty is
            nameof(VmMemorySettings.EnableColdHint) or
            nameof(VmMemorySettings.EnableHotHint) or
            nameof(VmMemorySettings.EnableEpf) or
            nameof(VmMemorySettings.EnablePrivateCompressionStore);

        // Only normalize automatic edits for the settings participating in these constraints.
        // A manual Apply (changedProperty == null) is also normalized as a final safety net.
        if (!backingTypeChanged && !pageSizeChanged && !backingFeatureChanged && changedProperty != null)
            return;

        // The setting changed by the user wins: choosing 1 GB pages selects physical backing;
        // choosing virtual backing while 1 GB pages are active falls back to 2 MB pages.
        if (settings.BackingPageSize == 2 && settings.BackingType.HasValue)
        {
            if (backingTypeChanged && settings.BackingType != 0)
                settings.BackingPageSize = 1;
            else
                settings.BackingType = 0;
        }

        // VMMS rejects heat hints, EPF and private compression stores on physical backing.
        // Preserve null for properties that are not exposed by the current host.
        if (settings.BackingType == 0)
        {
            if (settings.EnableColdHint == true) settings.EnableColdHint = false;
            if (settings.EnableHotHint == true) settings.EnableHotHint = false;
            if (settings.EnableEpf == true) settings.EnableEpf = false;
            if (settings.EnablePrivateCompressionStore == true) settings.EnablePrivateCompressionStore = false;
        }
    }

    /// <summary>只应用明确选择的内存字段，并处理必要的页对齐、动态内存和 vNUMA 联动。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="newSettings">本次请求值；只读取 changedProperties 中的属性，容量单位为 MiB，Weight 为原始权重。</param>
    /// <param name="changedProperties">本次修改的属性名集合，至少指定一项。</param>
    /// <returns>应用结果；Steps 按顺序记录已执行的 DisableVirtualNuma、Memory、EnableVirtualNuma 步骤，含各步骤的状态与原始错误。不需要或未开始的步骤不列出。</returns>
    /// <remarks>
    /// <para>未选字段沿用当前值。切换动态内存、后备页、后备类型及 SGX 时按相应规则联动。同一虚拟机的 CPU 与内存修改在 Core 进程之间排队，同一字段以本次请求覆盖。</para>
    /// <para>实验性字段的用途和取值见 <see cref="VmMemoryFields"/>。</para>
    /// <para>null 保留原值；其他选中字段仍按各自规则联动。全部选择均为 null 时跳过提交。非空请求对应的属性缺失，或宿主页大小接口无法表达该请求时，提交前返回 Unsupported。</para>
    /// <para>CXL 写入在已知版本上可能导致 VMMS 崩溃，具体版本及限制见 <see cref="VmMemoryFields.CxlEnabled"/>。</para>
    /// </remarks>
    /// <example><code>
    /// using System;
    /// using System.Threading.Tasks;
    /// using ExHyperV.Models;
    /// using ExHyperV.Services;
    ///
    /// public static class MemoryFieldExample
    /// {
    ///     public static async Task SetStartupAsync(Guid vmId)
    ///     {
    ///         var settings = new VmMemorySettings { Startup = 4096 };
    ///         var result = await VmMemoryService.SetVmMemorySettingsAsync(
    ///             vmId, settings,
    ///             changedProperties: [nameof(VmMemorySettings.Startup)]);
    ///         if (!result.Success) throw new InvalidOperationException(result.Error);
    ///     }
    /// }
    /// </code></example>
    public static async Task<ApiResponse> SetVmMemorySettingsAsync(
        Guid vmId, VmMemorySettings newSettings, IReadOnlyCollection<string> changedProperties)
    {
        if (vmId == Guid.Empty || newSettings == null)
            return ApiResponse.Fail("虚拟机 GUID 和内存配置不能为空。", kind: ApiErrorKind.InvalidArgument);
        SettingsSelection<VmMemorySettings> selection;
        try { selection = SettingsSelection<VmMemorySettings>.Capture(newSettings, changedProperties); }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, kind: ApiErrorKind.InvalidArgument); }
        if (!selection.Names.Any(name => selection[name] != null)) return ApiResponse.Ok();
        return await VmSettingsWriteLock.RunAsync(vmId, () => ApplyMemorySettingsAsync(vmId, selection));
    }

    internal static Task<ApiResponse> SetVmMemorySettingsAsync(Guid vmId, VmMemorySettings settings, bool isVmRunning, IReadOnlyCollection<string> changedProperties)
        => SetVmMemorySettingsAsync(vmId, settings, changedProperties);

    private static async Task<ApiResponse> ApplyMemorySettingsAsync(Guid vmId, SettingsSelection<VmMemorySettings> selection)
    {
        bool isVmRunning;
        bool numaDisabled = false, memoryApplied = false;
        var steps = new List<OperationStepResult>();
        try
        {
            var vm = await WmiApi.QueryFirstAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => VmMapper.MapState(Convert.ToUInt16(obj["EnabledState"])));
            if (!vm.Success) return ApiResponse.Fail(vm.Error, vm.Code, vm.ErrorSource, vm.Exception, vm.ErrorKind);
            if (!vm.HasData) return ApiResponse.Fail(Properties.Resources.Error_Memory_VmNotFound, kind: ApiErrorKind.NotFound);
            isVmRunning = vm.Data != VmState.PoweredOff;
            ApiResponse? validation = null;
            ApiResponse? selectionError = null;
            var plan = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmId:D}%' AND ResourceType = 4",
                obj =>
                {
                    var raw = obj.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value);
                    selectionError = GetMemorySelectionError(raw, selection);
                    if (selectionError != null) return null;
                    var current = MapMemory(obj);
                    var target = MergeMemorySelection(current, selection);
                    validation = ValidateMemoryChange(current, target, vm.Data);
                    if (!validation.Success) return null;
                    var writes = BuildMemoryWrites(raw, target, selection, isVmRunning);
                    foreach (var (name, value) in writes) obj[name] = value;
                    return new { Xml = obj.GetText(TextFormat.CimDtd20), Target = target };
                });
            if (!plan.Success) return ApiResponse.Fail(plan.Error, plan.Code, plan.ErrorSource, plan.Exception, plan.ErrorKind);
            if (selectionError != null) return selectionError;
            if (validation is { Success: false }) return validation;
            if (!plan.HasData) return ApiResponse.Fail(Properties.Resources.Error_Memory_VmNotFound, kind: ApiErrorKind.NotFound);
            bool dynamicChanged = selection.Contains(nameof(VmMemorySettings.DynamicMemoryEnabled));
            if (dynamicChanged && !isVmRunning && plan.Data.Target.DynamicMemoryEnabled)
            {
                var numaOff = await SetVirtualNumaEnabledAsync(vmId, false);
                steps.Add(OperationStepResult.From("DisableVirtualNuma", numaOff));
                if (!numaOff.Success) return numaOff.WithSteps(steps);
                numaDisabled = true;
            }
            var result = await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService",
                "ModifyResourceSettings", p => p["ResourceSettings"] = new[] { plan.Data.Xml });
            steps.Add(OperationStepResult.From("Memory", result));
            if (!result.Success) return PartialMemoryFailure(result, numaDisabled, false).WithSteps(steps);
            memoryApplied = true;
            if (dynamicChanged && !isVmRunning && !plan.Data.Target.DynamicMemoryEnabled)
            {
                var numaOn = await SetVirtualNumaEnabledAsync(vmId, true);
                steps.Add(OperationStepResult.From("EnableVirtualNuma", numaOn));
                if (!numaOn.Success) return PartialMemoryFailure(numaOn, numaDisabled, true).WithSteps(steps);
            }
            return ApiResponse.Ok().WithSteps(steps);
        }
        catch (Exception ex)
        {
            return PartialMemoryFailure(ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed), numaDisabled, memoryApplied).WithSteps(steps);
        }
    }

    internal static VmMemorySettings MergeMemorySelection(VmMemorySettings current, SettingsSelection<VmMemorySettings> selection)
    {
        var target = current.Clone();
        var fields = selection.Names.Where(name => selection[name] != null).ToArray();
        foreach (string field in fields)
            typeof(VmMemorySettings).GetProperty(field)!.SetValue(target, selection[field]);
        if (fields.Contains(nameof(VmMemorySettings.SgxEnabled)) && target.SgxEnabled == true
            && (target.SgxSize == null || target.SgxSize < 2)) target.SgxSize = 2;
        var backingFields = fields.Where(p => p is nameof(VmMemorySettings.BackingPageSize) or nameof(VmMemorySettings.BackingType)
            or nameof(VmMemorySettings.EnableColdHint) or nameof(VmMemorySettings.EnableHotHint)
            or nameof(VmMemorySettings.EnableEpf) or nameof(VmMemorySettings.EnablePrivateCompressionStore)).ToArray();
        if (backingFields.Length != 0)
            NormalizeMemoryBackingSettings(target, backingFields.Length == 1 ? backingFields[0] : null);
        return target;
    }

    // WMI research memory:3: HugePagesEnabled cannot represent 4 KiB. Validate the
    // explicit request before any vNUMA change; optional absent dependency fields stay optional.
    internal static ApiResponse? GetMemorySelectionError(IReadOnlyDictionary<string, object?> raw,
        SettingsSelection<VmMemorySettings> selection)
    {
        foreach (string field in selection.Names)
        {
            if (selection[field] == null) continue;
            string property = MemoryPropertyName(field);
            if (field == nameof(VmMemorySettings.BackingPageSize) && !raw.ContainsKey(property)
                && raw.ContainsKey("HugePagesEnabled"))
            {
                if (selection[field] is not (byte)1 and not (byte)2)
                    return ApiResponse.Fail("宿主仅提供 HugePagesEnabled，只能选择 2 MiB 大页或 1 GiB 巨页，不能表达本次后备页请求。未修改任何配置。", kind: ApiErrorKind.Unsupported);
                continue;
            }
            if (!raw.ContainsKey(property))
                return ApiResponse.Fail($"宿主不提供内存属性 {property}，无法提交 {field}。未修改任何配置。", kind: ApiErrorKind.Unsupported);
        }
        return null;
    }

    internal static Dictionary<string, object?> BuildMemoryWrites(IReadOnlyDictionary<string, object?> raw,
        VmMemorySettings target, SettingsSelection<VmMemorySettings> selection, bool isRunning)
    {
        var values = new SettingsValues(raw);
        // 先应用模式与依赖，再应用调用方明确给出的数值；关联字段均来自当前值与本次选择的合并。
        foreach (string field in selection.Names.Where(name => selection[name] != null).OrderBy(p => p switch
        {
            nameof(VmMemorySettings.BackingType) => 0,
            nameof(VmMemorySettings.BackingPageSize) => 1,
            nameof(VmMemorySettings.DynamicMemoryEnabled) => 2,
            nameof(VmMemorySettings.SgxEnabled) => 3,
            _ => 4
        })) ApplyMemoryField(values, target, field, isRunning);
        return values.Writes;
    }

    internal static ApiResponse PartialMemoryFailure(ApiResponse failure, bool numaDisabled, bool memoryApplied)
    {
        string prefix = memoryApplied ? "内存已修改，但 vNUMA 设置失败："
            : numaDisabled ? "vNUMA 已关闭，但内存设置失败：" : "";
        return ApiResponse.Fail(prefix + failure.Error, failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
    }

    /// <summary>切换 VM 的 VirtualNumaEnabled（vNUMA 与动态内存互斥，对齐 Set-VMMemory；仅离线可改）。</summary>
    private static Task<ApiResponse> SetVirtualNumaEnabledAsync(Guid vmId, bool enabled)
        => WmiApi.WithObjectAsync(
            wql: $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            modifier: obj => { if (obj.HasProperty("VirtualNumaEnabled")) obj["VirtualNumaEnabled"] = enabled; },
            submitMethod: "ModifySystemSettings",
            submitParamName: "SystemSettings",
            wrapInArray: false);

    internal static bool SupportsSingleMemorySetting(string property) =>
        typeof(VmMemorySettings).GetProperty(property)?.SetMethod?.IsPublic == true;

    private static void ApplyMemoryField(
        SettingsValues memData, VmMemorySettings settings, string changedProperty, bool isRunning)
    {
        static ulong Align(long value, long alignment)
        {
            if (value <= 0) return (ulong)alignment;
            if (value > long.MaxValue - alignment) return (ulong)value;
            return (ulong)((value + alignment - 1) / alignment * alignment);
        }

        long alignment = settings.BackingPageSize == 2 ? 1024 : 2;
        switch (changedProperty)
        {
            case nameof(VmMemorySettings.Startup):
                memData["VirtualQuantity"] = Align(settings.Startup, alignment);
                if (!isRunning && !settings.DynamicMemoryEnabled)
                {
                    memData["Reservation"] = Align(settings.Startup, alignment);
                    memData["Limit"] = Align(settings.Startup, alignment);
                }
                break;
            case nameof(VmMemorySettings.Minimum):
                if (settings.DynamicMemoryEnabled) memData["Reservation"] = Align(settings.Minimum, alignment);
                break;
            case nameof(VmMemorySettings.Maximum):
                if (settings.DynamicMemoryEnabled) memData["Limit"] = Align(settings.Maximum, alignment);
                break;
            case nameof(VmMemorySettings.Buffer):
                if (settings.DynamicMemoryEnabled) memData.TrySetAlways("TargetMemoryBuffer", checked((uint)settings.Buffer));
                break;
            case nameof(VmMemorySettings.Weight):
                memData["Weight"] = settings.Weight;
                break;
            case nameof(VmMemorySettings.SgxSize):
                if (settings.SgxSize.HasValue && settings.SgxEnabled == true)
                    memData.TrySetAlways("SgxSize", Math.Max(2UL, checked((ulong)settings.SgxSize.Value)) / 2 * 2);
                break;
            case nameof(VmMemorySettings.SgxLaunchControlMode):
                memData.TrySet("SgxLaunchControlMode", settings.SgxLaunchControlMode);
                break;
            case nameof(VmMemorySettings.SgxLaunchControlDefault):
                memData.TrySet("SgxLaunchControlDefault", settings.SgxLaunchControlDefault);
                break;
            case nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode):
                if (settings.MaxMemoryBlocksPerNumaNode.HasValue)
                {
                    ulong value = settings.MaxMemoryBlocksPerNumaNode.Value / (ulong)alignment * (ulong)alignment;
                    memData.TrySet("MaxMemoryBlocksPerNumaNode", Math.Max(value, (ulong)((32 + alignment - 1) / alignment * alignment)));
                }
                break;
            case nameof(VmMemorySettings.BackingPageSize):
            {
                byte pageSize = settings.BackingPageSize ?? 1;
                if (memData.HasProperty("BackingPageSize"))
                    memData["BackingPageSize"] = pageSize;
                else
                    memData.TrySet("HugePagesEnabled", (bool?)(pageSize == 2));

                // 1GB 页必须使用物理后端，相关内存范围也必须按 1GB 对齐。
                if (pageSize == 2)
                {
                    memData.TrySet("BackingType", (byte?)0);
                    memData.TrySet("EnableColdHint", settings.EnableColdHint);
                    memData.TrySet("EnableHotHint", settings.EnableHotHint);
                    memData.TrySet("EnableEpf", settings.EnableEpf);
                    memData.TrySet("EnablePrivateCompressionStore", settings.EnablePrivateCompressionStore);
                    memData["VirtualQuantity"] = Align(settings.Startup, alignment);
                    memData["Reservation"] = Align(settings.DynamicMemoryEnabled ? settings.Minimum : settings.Startup, alignment);
                    memData["Limit"] = Align(settings.DynamicMemoryEnabled ? settings.Maximum : settings.Startup, alignment);
                    if (settings.MaxMemoryBlocksPerNumaNode.HasValue)
                    {
                        ulong value = Math.Max(1024UL, settings.MaxMemoryBlocksPerNumaNode.Value);
                        memData.TrySet("MaxMemoryBlocksPerNumaNode", (ulong?)((value / 1024UL) * 1024UL));
                    }
                }
                break;
            }

            case nameof(VmMemorySettings.DynamicMemoryEnabled):
                memData["DynamicMemoryEnabled"] = settings.DynamicMemoryEnabled;
                if (settings.DynamicMemoryEnabled)
                {
                    memData["Reservation"] = Align(settings.Minimum, alignment);
                    memData["Limit"] = Align(settings.Maximum, alignment);
                    memData.TrySetAlways("TargetMemoryBuffer", (uint)settings.Buffer);
                }
                else
                {
                    memData["Reservation"] = Align(settings.Startup, alignment);
                    memData["Limit"] = Align(settings.Startup, alignment);
                }
                break;

            case nameof(VmMemorySettings.MemoryEncryptionPolicy):
                memData.TrySet("MemoryEncryptionPolicy", settings.MemoryEncryptionPolicy);
                break;

            case nameof(VmMemorySettings.BackingType):
                memData.TrySet("BackingType", settings.BackingType);
                // 从物理切回虚拟时，共享规范化会把不兼容的 1GB 页同步改成 2MB。
                if (settings.BackingType != 0 && settings.BackingPageSize == 1)
                {
                    if (memData.HasProperty("BackingPageSize"))
                        memData["BackingPageSize"] = (byte)1;
                    else
                        memData.TrySet("HugePagesEnabled", (bool?)false);
                }
                // 物理后端不接受以下四项；共享规范化已同步关闭，因此一并落盘。
                if (settings.BackingType == 0)
                {
                    memData.TrySet("EnableColdHint", settings.EnableColdHint);
                    memData.TrySet("EnableHotHint", settings.EnableHotHint);
                    memData.TrySet("EnableEpf", settings.EnableEpf);
                    memData.TrySet("EnablePrivateCompressionStore", settings.EnablePrivateCompressionStore);
                }
                break;

            case nameof(VmMemorySettings.MemoryAccessTrackingState):
                memData.TrySet("MemoryAccessTrackingState", settings.MemoryAccessTrackingState);
                break;
            case nameof(VmMemorySettings.MemoryAccessTrackingPolicy):
                memData.TrySet("MemoryAccessTrackingPolicy", settings.MemoryAccessTrackingPolicy);
                break;
            case nameof(VmMemorySettings.EnableColdHint):
                memData.TrySet("EnableColdHint", settings.EnableColdHint);
                break;
            case nameof(VmMemorySettings.EnableHotHint):
                memData.TrySet("EnableHotHint", settings.EnableHotHint);
                break;
            case nameof(VmMemorySettings.EnableEpf):
                memData.TrySet("EnableEpf", settings.EnableEpf);
                break;
            case nameof(VmMemorySettings.EnablePrivateCompressionStore):
                memData.TrySet("EnablePrivateCompressionStore", settings.EnablePrivateCompressionStore);
                break;

            case nameof(VmMemorySettings.SgxEnabled):
                memData.TrySet("SgxEnabled", settings.SgxEnabled);
                if (settings.SgxEnabled == true && settings.SgxSize.HasValue)
                {
                    ulong size = Math.Max(2UL, (ulong)settings.SgxSize.Value);
                    memData.TrySetAlways("SgxSize", (size / 2UL) * 2UL);
                }
                break;

            case nameof(VmMemorySettings.CxlEnabled):
                memData.TrySet("CxlEnabled", settings.CxlEnabled);
                break;
            case nameof(VmMemorySettings.EnableGpaPinning):
                memData.TrySet("EnableGpaPinning", settings.EnableGpaPinning);
                break;
            case nameof(VmMemorySettings.DynMemOperationAlignment):
                memData.TrySet("DynMemOperationAlignment", settings.DynMemOperationAlignment);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedProperty), changedProperty, "Unsupported automatic memory setting");
        }
    }
}
