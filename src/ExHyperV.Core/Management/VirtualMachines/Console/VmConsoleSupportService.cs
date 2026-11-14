using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 控制台支持开关——忠实复刻 Enable/Disable-VMConsoleSupport：增删合成 [鼠标 + 键盘 + 显示] 三件套。
//   显示    = Msvm_SyntheticDisplayControllerSettingData
//   鼠标/键盘 = 带特定 ResourceSubType 的通用 Msvm_ResourceAllocationSettingData
//     （源码 IVMSyntheticMouseControllerSetting 的 WmiName 即 Msvm_ResourceAllocationSettingData；
//      子类型常量取自 DLL 的 VMDeviceSettingTypeMapper）。
// 增删均走 Add/RemoveResourceSettings。需 VM 关机。
/// <summary>读取虚拟机控制台状态，管理连接相关配置。</summary>
public static partial class VmConsoleSupportService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string DisplayClass = "Msvm_SyntheticDisplayControllerSettingData";
    private const string RasdClass = "Msvm_ResourceAllocationSettingData";
    private const string MouseSubType = "Microsoft:Hyper-V:Synthetic Mouse";
    private const string KeyboardSubType = "Microsoft:Hyper-V:Synthetic Keyboard";

    /// <summary>判断是否允许添加或移除基本控制台所需的合成设备。</summary>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <returns>仅第二代且未运行时允许。</returns>
    internal static bool CanToggleConsoleSupport(int generation, bool isRunning) => generation == 2 && !isRunning;

    /// <summary>评估基本控制台开关的修改条件。</summary>
    /// <param name="generation">查询得到的虚拟机代数。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>第二代虚拟机关机后允许修改；不满足时返回原因。</returns>
    public static VmModificationCondition GetModificationCondition(int generation, VmState state) => generation == 2
        ? VmModificationCondition.PoweredOff(state) : VmModificationCondition.Unsupported();

    /// <summary>按稳定身份读取控制台名称、运行状态及增强会话可用性。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>读取结果；找不到目标与查询失败均明确报告，不按同名虚拟机回退。</returns>
    public static Task<ApiResponse<VmConsoleState>> GetConsoleStateAsync(Guid vmId) =>
        GetConsoleStateAsync(vmId,
            () => WmiApi.QueryAsync("SELECT Name, ElementName, EnabledState FROM Msvm_SummaryInformation", obj =>
                (Id: Guid.Parse(obj["Name"]!.ToString()!), Name: obj["ElementName"]?.ToString() ?? "",
                 State: Convert.ToUInt16(obj["EnabledState"] ?? (ushort)0))), IsEnhancedSessionAvailableAsync);

    internal static Task<ApiResponse<VmConsoleState>> GetConsoleStateAsync(Guid vmId,
        Func<Task<ApiResponse<List<(Guid Id, string Name, ushort State)>>>> queryStates,
        Func<Guid, Task<ApiResponse<bool>>> queryEnhanced) => Task.Run(async () =>
    {
        if (vmId == Guid.Empty) return ApiResponse<VmConsoleState>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var response = await queryStates().ConfigureAwait(false);
        if (!response.Success) return ApiResponse<VmConsoleState>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
        var matches = (response.Data ?? []).Where(vm => vm.Id == vmId).ToList();
        if (matches.Count != 1) return ApiResponse<VmConsoleState>.Fail("找不到唯一的目标虚拟机。", kind: matches.Count == 0 ? ApiErrorKind.NotFound : ApiErrorKind.Conflict);
        var vm = matches[0];
        bool running = VmMapper.IsActiveState(vm.State);
        var enhanced = running ? await queryEnhanced(vmId).ConfigureAwait(false) : ApiResponse<bool>.Ok(false);
        if (!enhanced.HasData) return ApiResponse<VmConsoleState>.Fail(enhanced.Error, enhanced.Code, enhanced.ErrorSource, enhanced.Exception, enhanced.ErrorKind);
        return ApiResponse<VmConsoleState>.Ok(new(vm.Id, vm.Name, running, enhanced.Data));
    });

    /// <summary>以合成显示控制器是否存在判断基本控制台支持是否启用。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>true 表示有显示控制器，false 表示未配置；不存在的虚拟机与读取失败返回错误。</returns>
    public static async Task<ApiResponse<bool>> IsConsoleSupportEnabledAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<bool>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var vm = await VmConfigurationState.ReadAsync(vmId);
        if (!vm.HasData) return ApiResponse<bool>.Fail(vm.Success ? "找不到虚拟机。" : vm.Error, vm.Code, vm.ErrorSource, vm.Exception, vm.Success ? ApiErrorKind.NotFound : vm.ErrorKind);
        var response = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM {DisplayClass} WHERE InstanceID LIKE 'Microsoft:{vmId:D}%'", obj => obj["InstanceID"]?.ToString());
        return response.Success ? ApiResponse<bool>.Ok(response.HasData) :
            ApiResponse<bool>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
    }

    /// <summary>检查 Hyper-V 当前是否报告增强会话可用。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>状态 2 为 true，其余状态或空值为 false。读取失败或虚拟机不存在时返回错误。</returns>
    public static async Task<ApiResponse<bool>> IsEnhancedSessionAvailableAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<bool>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var response = await WmiApi.QueryFirstAsync(
            $"SELECT EnhancedSessionModeState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            obj => obj["EnhancedSessionModeState"] is { } value ? Convert.ToUInt16(value) : (ushort)3);
        return response.HasData ? ApiResponse<bool>.Ok(response.Data == 2) :
            ApiResponse<bool>.Fail(response.Success ? "找不到虚拟机。" : response.Error, response.Code, response.ErrorSource,
                response.Exception, response.Success ? ApiErrorKind.NotFound : response.ErrorKind);
    }

    // 启用/禁用控制台支持。Disable 依 cmdlet 顺序删 鼠标→键盘→显示；Enable 缺则补 显示→键盘→鼠标。需 VM 关机；幂等。
    // 整体放进 Task.Run：首个 await 前的同步 WMI(GetVmComputerSystem/GetVirtualSystemManagementService)及循环里的
    // FindForVm/FindDefaultTemplate(searcher.Get) 都跑在调用线程上；被控制台开关在 UI 线程 await 调到就卡界面。
    /// <summary>为已停止的第二代虚拟机添加或移除基本控制台的键盘、鼠标及显示设备。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enable">true 请求启用该设置，false 请求关闭。</param>
    /// <returns>操作结果；步骤失败时返回错误，并保留此前已完成的设备变更。</returns>
    public static Task<ApiResponse> SetConsoleSupportAsync(Guid vmId, bool enable) => Task.Run(async () =>
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try
        {

        using var vm = WmiApi.GetVmComputerSystem(vmId);
        if (vm == null) return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);
        var state = VmConfigurationState.Read(vm);
        var condition = GetModificationCondition(state.Generation, state.State);
        if (!condition.CanModify) return condition.ToResponse();
        using var svcForScope = WmiApi.GetVirtualSystemManagementService();
        var scope = svcForScope.Scope;
        string guid = vm["Name"]?.ToString() ?? "";
        using var settings = WmiApi.GetVmSettings(vm);
        if (settings == null) return ApiResponse.Fail("找不到虚拟机当前配置。", kind: ApiErrorKind.NotFound);
        string affected = settings.Path.Path;

        // (设置类, 子类型)。显示无子类型(类名即专属)；鼠标/键盘为通用 RASD + 子类型区分。
        var devices = enable
            ? new (string Cls, string? Sub)[] { (DisplayClass, null), (RasdClass, KeyboardSubType), (RasdClass, MouseSubType) }
            : new (string Cls, string? Sub)[] { (RasdClass, MouseSubType), (RasdClass, KeyboardSubType), (DisplayClass, null) };

        foreach (var (cls, sub) in devices)
        {
            string? existingPath = FindForVm(scope, cls, guid, sub);

            if (enable)
            {
                if (existingPath != null) continue;   // 已有 → 跳过(幂等)
                string? tmplXml = FindDefaultTemplate(scope, cls, sub);
                if (tmplXml == null) return ApiResponse.Fail(string.Format(Properties.Resources.VmConsole_TemplateNotFound, cls, sub));
                var r = await WmiApi.InvokeAsync(ServiceWql, "AddResourceSettings",
                    p => { p["AffectedConfiguration"] = affected; p["ResourceSettings"] = new string[] { tmplXml }; });
                if (!r.Success) return r;
            }
            else
            {
                if (existingPath == null) continue;   // 已无 → 跳过(幂等)
                var r = await WmiApi.InvokeAsync(ServiceWql, "RemoveResourceSettings",
                    p => p["ResourceSettings"] = new string[] { existingPath });
                if (!r.Success) return r;
            }
        }
        return ApiResponse.Ok();
        }
        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex); }
    });

    // 查该 VM 现有的设备设置 __PATH（无则 null）
    private static string? FindForVm(ManagementScope scope, string cls, string vmGuid, string? subType)
    {
        string wql = $"SELECT * FROM {cls} WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"
            + (subType != null ? $" AND ResourceSubType = '{subType}'" : "");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var col = searcher.Get();
        using var obj = col.Cast<ManagementObject>().FirstOrDefault();
        return obj?.Path.Path;
    }

    // 取该设备类型的默认(Default)模板 XML（无则 null）
    private static string? FindDefaultTemplate(ManagementScope scope, string cls, string? subType)
    {
        string wql = $"SELECT * FROM {cls} WHERE InstanceID LIKE '%Default%'"
            + (subType != null ? $" AND ResourceSubType = '{subType}'" : "");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var col = searcher.Get();
        using var tmpl = col.Cast<ManagementObject>().FirstOrDefault();
        return tmpl?.GetText(TextFormat.CimDtd20);
    }
}

/// <summary>一次读取的控制台状态；只表示查询时的事实。</summary>
/// <param name="VmId">目标虚拟机稳定 GUID。</param>
/// <param name="Name">查询时的显示名称。</param>
/// <param name="IsRunning">是否处于活动状态。</param>
/// <param name="IsEnhancedAvailable">Hyper-V 是否报告增强会话可用。</param>
public sealed record VmConsoleState(Guid VmId, string Name, bool IsRunning, bool IsEnhancedAvailable);
