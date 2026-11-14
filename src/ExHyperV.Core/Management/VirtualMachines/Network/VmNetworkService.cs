using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services;

/// <summary>安全和卸载设置的组合写入结果，分别保留两步是否成功。</summary>
/// <param name="Success">该项操作是否完整成功；部分完成信息由同一结果中的其他字段表达。</param>
/// <param name="Message">供调用方展示的本地化结果或确认说明。</param>
/// <param name="SecurityApplied">安全设置步骤是否已经成功写入。</param>
/// <param name="OffloadApplied">卸载设置步骤是否已经成功写入。</param>
public sealed record VmNetworkSettingsApplyResult(bool Success, string Message, bool SecurityApplied, bool OffloadApplied)
{
    /// <summary>主失败的类别、来源和原始码；成功时为 null。</summary>
    public ApiFailure? Failure { get; init; }
    /// <summary>安全步骤的执行状态及错误；未选择或前置校验未通过时为 NotStarted。</summary>
    public OperationStepResult Security { get; init; } = new("Security", SecurityApplied ? OperationStepState.Succeeded : OperationStepState.NotStarted);
    /// <summary>卸载步骤的执行状态及错误；安全步骤失败或未选择时为 NotStarted。</summary>
    public OperationStepResult Offload { get; init; } = new("Offload", OffloadApplied ? OperationStepState.Succeeded : OperationStepState.NotStarted);
}

/// <summary>读取网卡和交换机，配置连接、MAC、VLAN、带宽、安全及卸载能力。</summary>
public static partial class VmNetworkService
{

    internal static async Task<List<VmNetworkAdapter>> GetNetworkAdaptersAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) throw new InvalidOperationException(identity.Error);
        var response = await GetNetworkAdaptersAsync(identity.Data);
        if (!response.HasData) throw new InvalidOperationException(response.Error);
        return response.Data;
    }


    internal static async Task<(bool Success, string Message)> AddNetworkAdapterAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var result = await AddNetworkAdapterAsync(identity.Data);
        return (result.Success, string.Join(" ", new[] { result.Error, result.CleanupError }.Where(x => !string.IsNullOrWhiteSpace(x))));
    }


    internal static async Task<(bool Success, string Message)> RemoveNetworkAdapterAsync(string vmName, VmNetworkAdapter adapter)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(RemoveNetworkAdapterAsync(identity.Data, adapter));
    }


    internal static async Task<(bool Success, string Message)> RemoveNetworkAdapterAsync(string vmName, string id)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(RemoveNetworkAdapterAsync(identity.Data, id));
    }


    internal static async Task<(bool Success, string Message)> UpdateConnectionAsync(string vmName, VmNetworkAdapter adapter)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(UpdateConnectionAsync(identity.Data, adapter, adapter.SwitchId));
    }


    internal static async Task<(bool Success, string Message)> SetMacAddressAsync(string vmName, VmNetworkAdapter adapter, string newMac)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(SetMacAddressAsync(identity.Data, adapter, newMac));
    }


    internal static async Task<(bool Success, string Message)> ApplyVlanSettingsAsync(string vmName, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(ApplyVlanSettingsAsync(identity.Data, adapter, changedProperties));
    }


    internal static async Task<(bool Success, string Message)> ApplyBandwidthSettingsAsync(string vmName, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(ApplyBandwidthSettingsAsync(identity.Data, adapter, changedProperties));
    }


    internal static async Task<(bool Success, string Message)> ApplySecuritySettingsAsync(string vmName, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(ApplySecuritySettingsAsync(identity.Data, adapter, changedProperties));
    }


    internal static async Task<(bool Success, string Message)> ApplyOffloadSettingsAsync(string vmName, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(ApplyOffloadSettingsAsync(identity.Data, adapter, changedProperties));
    }


    internal static async Task<(bool Success, string Message)> ApplySecurityAndOffloadSettingsAsync(string vmName, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        return await ToLegacyAsync(ApplySecurityAndOffloadSettingsAsync(identity.Data, adapter, changedProperties));
    }


    internal static async Task<VmNetworkSettingsApplyResult> ApplySecurityAndOffloadSettingsWithResultAsync(string vmName, VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) throw new InvalidOperationException(identity.Error);
        return await ApplySecurityAndOffloadSettingsWithResultAsync(identity.Data, adapter, changedProperties);
    }

    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    // ── 查询 ──────────────────────────────────────────────────────

    /// <summary>读取网卡、交换机连接及可用的 VLAN、带宽、安全和卸载设置。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功时包含网卡快照列表，可为空；虚拟机不存在或任一必要查询失败时返回失败，不用空集合代替错误。</returns>
    /// <remarks>当前连接没有某类可选功能设置时保留模型默认值；读取这些设置发生错误时仍返回失败。</remarks>
    public static Task<ApiResponse<List<VmNetworkAdapter>>> GetNetworkAdaptersAsync(Guid vmId) => Task.Run(async () =>
    {
        try
        {
            if (vmId == Guid.Empty)
                return ApiResponse<List<VmNetworkAdapter>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var vm = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            if (!vm.Success) return QueryFailure<List<VmNetworkAdapter>, string?>(vm);
            if (!vm.HasData)
                return ApiResponse<List<VmNetworkAdapter>>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

            var portsTask = QueryPortsAsync("Msvm_SyntheticEthernetPortSettingData", vmId);
            var emulatedPortsTask = QueryPortsAsync("Msvm_EmulatedEthernetPortSettingData", vmId);
            var allocationsTask = WmiApi.QueryAsync(
                $"SELECT EnabledState, InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{vmId:D}%'",
                obj => new NetworkAllocationSnapshot(obj["InstanceID"]?.ToString() ?? string.Empty,
                    obj["EnabledState"]?.ToString() == "2", obj["HostResource"] as string[] ?? []));
            await Task.WhenAll(portsTask, emulatedPortsTask, allocationsTask);
            var ports = await portsTask;
            var emulated = await emulatedPortsTask;
            var allocations = await allocationsTask;
            if (!ports.Success) return QueryFailure<List<VmNetworkAdapter>, List<NetworkPortSnapshot>>(ports);
            if (!emulated.Success) return QueryFailure<List<VmNetworkAdapter>, List<NetworkPortSnapshot>>(emulated);
            if (!allocations.Success) return QueryFailure<List<VmNetworkAdapter>, List<NetworkAllocationSnapshot>>(allocations);

            var adapters = new List<VmNetworkAdapter>();
            foreach (var port in (ports.Data ?? []).Concat(emulated.Data ?? []))
            {
                if (string.IsNullOrEmpty(port.Id))
                    return ApiResponse<List<VmNetworkAdapter>>.Fail("网卡查询返回了无效身份。", kind: ApiErrorKind.OperationFailed);
                var adapter = new VmNetworkAdapter
                {
                    Id = port.Id, Name = port.Name, MacAddress = MacAddress.Format(port.Address),
                    IsStaticMac = port.IsStaticMac
                };
                var allocation = allocations.Data?.FirstOrDefault(a =>
                    a.Id.StartsWith(port.Id + "\\", StringComparison.OrdinalIgnoreCase));
                if (allocation != null)
                {
                    adapter.IsConnected = allocation.IsConnected;
                    if (allocation.HostResources.Length > 0)
                    {
                        string? switchGuid = allocation.HostResources[0].Split('"').Reverse().Skip(1).FirstOrDefault();
                        adapter.SwitchId = Guid.TryParse(switchGuid, out var parsedSwitchId) ? parsedSwitchId : null;
                        var switchName = await GetSwitchNameByGuidAsync(switchGuid);
                        if (!switchName.Success) return QueryFailure<List<VmNetworkAdapter>, string?>(switchName);
                        adapter.SwitchIdentity = switchName.Data ?? string.Empty;
                    }
                    string safeId = allocation.Id.Replace(@"\", @"\\").Replace("'", "\\'");
                    string relPath = $"Msvm_EthernetPortAllocationSettingData.InstanceID=\"{safeId}\"";
                    var features = await WmiApi.QueryAsync(
                        $"ASSOCIATORS OF {{{relPath}}} WHERE AssocClass = Msvm_EthernetPortSettingDataComponent ResultClass = Msvm_EthernetSwitchPortFeatureSettingData",
                        feature => { ParseFeatureSettings(adapter, feature); return true; });
                    if (!features.Success) return QueryFailure<List<VmNetworkAdapter>, List<bool>>(features);
                }
                else
                {
                    adapter.IsConnected = false;
                    adapter.SwitchIdentity = string.Empty;
                }
                adapters.Add(adapter);
            }
            return ApiResponse<List<VmNetworkAdapter>>.Ok(adapters);
        }
        catch (Exception ex)
        {
            return ApiResponse<List<VmNetworkAdapter>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    });

    private sealed record NetworkPortSnapshot(string Id, string Name, string? Address, bool IsStaticMac);
    private sealed record NetworkAllocationSnapshot(string Id, bool IsConnected, string[] HostResources);

    private static Task<ApiResponse<List<NetworkPortSnapshot>>> QueryPortsAsync(string className, Guid vmId) =>
        WmiApi.QueryAsync($"SELECT * FROM {className} WHERE InstanceID LIKE 'Microsoft:{vmId:D}%'",
            obj => new NetworkPortSnapshot(obj["InstanceID"]?.ToString() ?? string.Empty,
                obj["ElementName"]?.ToString() ?? Properties.Resources.Common_NoName,
                obj["Address"]?.ToString(), obj.TryGet<bool>("StaticMacAddress") ?? false));

    private static ApiResponse<TResult> QueryFailure<TResult, TSource>(ApiResponse<TSource> response) =>
        ApiResponse<TResult>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

    /// <summary>读取当前宿主可用虚拟交换机的 GUID 和显示名称。</summary>
    /// <returns>以 GUID 为键、名称为值的只读字典；允许重名，空字典表示没有交换机。</returns>
    public static async Task<ApiResponse<IReadOnlyDictionary<Guid, string>>> GetAvailableSwitchesAsync()
    {
        var response = await WmiApi.QueryAsync("SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch",
            obj => (Id: obj["Name"]?.ToString(), Name: obj["ElementName"]?.ToString() ?? ""));
        if (!response.Success) return QueryFailure<IReadOnlyDictionary<Guid, string>, List<(string? Id, string Name)>>(response);
        return CreateSwitchCatalog(response.Data ?? []);
    }

    internal static ApiResponse<IReadOnlyDictionary<Guid, string>> CreateSwitchCatalog(IEnumerable<(string? Id, string Name)> rows)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var row in rows)
            if (!Guid.TryParse(row.Id, out var id) || id == Guid.Empty || !result.TryAdd(id, row.Name))
                return ApiResponse<IReadOnlyDictionary<Guid, string>>.Fail("交换机查询返回无效或重复身份。", kind: ApiErrorKind.OperationFailed);
        return ApiResponse<IReadOnlyDictionary<Guid, string>>.Ok(new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, string>(result));
    }

    /// <summary>依次解析网卡 IPv4 并更新传入网卡对象；宿主嗅探结果优先。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="adapters">该虚拟机查询返回的网卡对象集合。</param>
    /// <returns>全部解析完成时成功；失败时返回原因，已更新的地址保留。</returns>
    public static async Task<ApiResponse> FillDynamicIpsAsync(Guid vmId, IEnumerable<VmNetworkAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        foreach (var adapter in adapters)
        {
            var ownership = await ValidateAdapterOwnershipAsync(vmId, adapter.Id);
            if (!ownership.Success) return ownership;
            var resolution = VmIpService.ResolveAdapterAddresses(adapter.MacAddress, adapter.IpAddresses);
            adapter.IpAddresses = resolution.Addresses;
            if (!resolution.RequiresLookup) continue;
            var lookup = await VmIpService.LookupAsync(vmId, adapter.MacAddress);
            if (!lookup.Success) return Failure(lookup);
            if (lookup.HasData)
                adapter.IpAddresses = lookup.Data.Where(ip => ip.Association != VmIpAddressAssociation.VirtualMachine)
                    .Select(ip => ip.Address).Distinct().ToList();
        }
        return ApiResponse.Ok();
    }

    // ── 网卡生命周期 ──────────────────────────────────────────────

    /// <summary>为虚拟机添加合成网卡并初始化连接资源；确认失败时自动撤销本次创建。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>分别报告实际网卡状态、初始化结果、原始错误及撤销错误；失败后应刷新实际列表。</returns>
    /// <remarks>结果或资源归属待确认时保留现场。撤销前核对配置与附属资源；最终核对和提交之间仍可能发生外部修改。</remarks>
    /// <editentry label="添加网卡" category="网络"/>
    public static Task<VmNetworkAdapterCreationResult> AddNetworkAdapterAsync(Guid vmId) =>
        Task.Run(() => CreateNetworkAdapterAsync(vmId));

    /// <summary>移除查询返回的虚拟机网卡及其连接资源，执行前核对网卡属于指定虚拟机。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="adapter">GetNetworkAdaptersAsync 返回的目标网卡。</param>
    /// <returns>Success 表示移除成功，Error 为结果说明。</returns>
    /// <editentry label="移除网卡" category="网络"/>
    public static Task<ApiResponse> RemoveNetworkAdapterAsync(Guid vmId, VmNetworkAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return RemoveNetworkAdapterAsync(vmId, adapter.Id);
    }

    internal static async Task<ApiResponse> RemoveNetworkAdapterAsync(Guid vmId, string id)
    {
        var ownership = await ValidateAdapterOwnershipAsync(vmId, id);
        if (!ownership.Success) return ownership;
        string escapedId = WmiApi.Escape(id);

        var pathResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM {PortSettingClass(id)} WHERE InstanceID = '{escapedId}'",
            obj => obj.Path.Path);

        if (!pathResponse.Success) return Failure(pathResponse);
        if (!pathResponse.HasData)
            return ApiResponse.Fail(Properties.Resources.Error_Net_AllocNotFound, kind: ApiErrorKind.NotFound);

        var result = await WmiApi.InvokeAsync(
            ServiceWql,
            "RemoveResourceSettings",
            p => p["ResourceSettings"] = new string[] { pathResponse.Data! });

        return result;
    }

    /// <summary>按网卡连接开关及明确的交换机 GUID 更新连接，不按交换机名称重新匹配。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID；执行前核对查询所得网卡属于该虚拟机。</param>
    /// <param name="adapter">GetNetworkAdaptersAsync 返回并填入修改值的网卡；内部定位信息由 Core 保管。</param>
    /// <param name="switchId">接通时必须提供目标交换机 GUID；断开时忽略，保留原目标。可从宿主交换机查询或 adapter.SwitchId 取得。</param>
    /// <returns>Success 表示连接配置修改成功，Error 为错误或结果说明。</returns>
    public static async Task<ApiResponse> UpdateConnectionAsync(
        Guid vmId, VmNetworkAdapter adapter, Guid? switchId)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        bool isConnected = adapter.IsConnected;
        string adapterId = adapter.Id;
        if (isConnected && (switchId is null || switchId == Guid.Empty))
            return ApiResponse.Fail("接通网卡必须指定目标交换机 GUID。", kind: ApiErrorKind.InvalidArgument);
        var ownership = await ValidateAdapterOwnershipAsync(vmId, adapterId);
        if (!ownership.Success)
            return ownership;
        string? switchPath = null;
        if (isConnected)
        {
            var selected = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE Name = '{switchId:D}'", obj => obj.Path.Path);
            if (!selected.Success) return Failure(selected);
            if (!selected.HasData || string.IsNullOrEmpty(selected.Data))
                return ApiResponse.Fail("所选交换机已不存在，未按名称选择其他交换机。", kind: ApiErrorKind.NotFound);
            switchPath = selected.Data;
        }

        string escapedId = WmiApi.Escape(adapterId);

        var xmlResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM {PortSettingClass(adapterId)} WHERE InstanceID = '{escapedId}'",
            port =>
            {
                using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData")
                    .Cast<ManagementObject>().FirstOrDefault();
                if (allocation == null) return null;

                allocation["EnabledState"] = (ushort)(isConnected ? 2 : 3);

                if (isConnected) allocation["HostResource"] = new[] { switchPath! };

                return allocation.GetText(TextFormat.CimDtd20);
            });

        if (!xmlResponse.Success) return Failure(xmlResponse);
        if (!xmlResponse.HasData || string.IsNullOrEmpty(xmlResponse.Data))
            return ApiResponse.Fail(Properties.Resources.Error_Net_AllocNotFound);

        var result = await WmiApi.InvokeAsync(
            ServiceWql,
            "ModifyResourceSettings",
            p => p["ResourceSettings"] = new string[] { xmlResponse.Data! });

        return result;
    }

    /// <summary>在要求接通网卡时检查是否存在可选虚拟交换机。</summary>
    /// <param name="isConnected">是否要求网卡连接虚拟交换机。</param>
    /// <param name="availableSwitchCount">当前可用虚拟交换机数量。</param>
    /// <returns>需要交换机但数量为零时返回说明，否则为 null。</returns>
    public static string? GetConnectionError(bool isConnected, int availableSwitchCount) =>
        isConnected && availableSwitchCount == 0 ? Properties.Resources.Msg_Net_CreateSwitchFirst : null;

    internal static string? GetConnectionError(bool isConnected, ApiResponse<List<string?>> switches)
    {
        if (!isConnected) return null;
        if (!switches.Success) return switches.Error;
        return GetConnectionError(true, switches.Data?.Count(name => !string.IsNullOrEmpty(name)) ?? 0);
    }

    // ── 高级特性配置 ──────────────────────────────────────────────

    // 改静态 MAC：写网卡 setting(合成/旧版按 PortSettingClass 派发到对应类)的 Address + StaticMacAddress(本身不是 feature，用 ModifyResourceSettings)。
    // 输入空=改回动态(系统分配)。运行态沿用页面的不可修改规则；其余原生约束仍返回引擎错误。
    /// <summary>判断当前运行状态是否允许编辑 MAC 地址。</summary>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <returns>未运行时为 true。</returns>
    internal static bool CanEditMacAddress(bool isRunning) => !isRunning;

    /// <summary>评估 MAC 地址的修改条件。</summary>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改、原因及需要的电源状态。</returns>
    public static VmModificationCondition GetMacModificationCondition(VmState state) => VmModificationCondition.PoweredOff(state);

    /// <summary>核对虚拟机状态后写入静态 MAC，空地址恢复由系统动态分配。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="adapter">GetNetworkAdaptersAsync 返回并填入修改值的网卡；内部定位信息由 Core 保管。</param>
    /// <param name="newMac">新 MAC 字符串，会规范化并校验；空字符串用于恢复动态 MAC。</param>
    /// <returns>Success 表示写入成功，Error 为地址格式、状态或底层失败原因。</returns>
    public static async Task<ApiResponse> SetMacAddressAsync(
        Guid vmId, VmNetworkAdapter adapter, string newMac)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var ownership = await ValidateAdapterOwnershipAsync(vmId, adapter.Id);
        if (!ownership.Success)
            return ownership;
        string? norm = MacAddress.Normalize(newMac);
        if (norm == null)
            return ApiResponse.Fail(Properties.Resources.Error_Net_MacInvalid);

        var state = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            obj => VmMapper.MapState(Convert.ToUInt16(obj["EnabledState"])));
        if (!state.Success) return Failure(state);
        if (!state.HasData)
            return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);
        var condition = GetMacModificationCondition(state.Data);
        if (!condition.CanModify) return condition.ToResponse();

        bool toStatic = norm.Length == 12;
        string safeId = adapter.Id.Replace(@"\", @"\\").Replace("'", "\\'");

        // 按网卡类型派发到对应 setting 类(旧版=Emulated、合成=Synthetic;段数判断见 PortSettingClass)，一次定位、错误即真因。
        var result = await WmiApi.WithObjectAsync(
            wql: $"SELECT * FROM {PortSettingClass(adapter.Id)} WHERE InstanceID = '{safeId}'",
            modifier: obj =>
            {
                obj["StaticMacAddress"] = toStatic;
                if (toStatic) obj["Address"] = norm;
            },
            submitMethod: "ModifyResourceSettings", submitParamName: "ResourceSettings",
            wrapInArray: true, serviceWql: ServiceWql);

        return result;
    }

    /// <summary>只修改本次选择的VLAN字段；同字段按本次值覆盖。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="adapter">查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。</param>
    /// <param name="changedProperties">要提交的 VLAN 属性名集合，使用 nameof，不能为空。切换模式时同时包含 VlanMode 和目标模式所需的字段；其他类别的属性名在任何查询或写入前拒绝。</param>
    /// <returns>成功或带原因的失败响应。切换 VLAN 模式时清理不兼容的旧模式字段。</returns>
    public static Task<ApiResponse> ApplyVlanSettingsAsync(Guid vmId, VmNetworkAdapter adapter,
        IReadOnlyCollection<string> changedProperties) => ApplySelectedFeatureAsync(vmId, adapter, changedProperties,
            "Msvm_EthernetSwitchPortVlanSettingData", VlanFields);

    /// <summary>只修改本次选择的带宽字段；同字段按本次值覆盖。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="adapter">查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。</param>
    /// <param name="changedProperties">要提交的带宽属性名集合，不能为空。例如仅修改带宽上限时包含 <c>nameof(VmNetworkAdapter.BandwidthLimitBitsPerSecond)</c>，带宽保留值保持不变；其他类别的属性名在任何查询或写入前拒绝。</param>
    /// <returns>成功或带原因的失败响应。</returns>
    public static Task<ApiResponse> ApplyBandwidthSettingsAsync(Guid vmId, VmNetworkAdapter adapter,
        IReadOnlyCollection<string> changedProperties) => ApplySelectedFeatureAsync(vmId, adapter, changedProperties,
            "Msvm_EthernetSwitchPortBandwidthSettingData", BandwidthFields);

    /// <summary>只修改本次选择的安全字段；同字段按本次值覆盖。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="adapter">查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。</param>
    /// <param name="changedProperties">非空属性名集合，使用 nameof；支持本接口所属安全字段，其他名称在任何查询或写入前拒绝。</param>
    /// <returns>成功或带原因的失败响应。</returns>
    public static Task<ApiResponse> ApplySecuritySettingsAsync(Guid vmId, VmNetworkAdapter adapter,
        IReadOnlyCollection<string> changedProperties) => ApplySelectedFeatureAsync(vmId, adapter, changedProperties,
            "Msvm_EthernetSwitchPortSecuritySettingData", SecurityFields);

    /// <summary>只修改本次选择的卸载字段；同字段按本次值覆盖。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="adapter">查询所得网卡，承载设备身份与本次请求值；未选择的属性不回写。</param>
    /// <param name="changedProperties">非空属性名集合，使用 nameof；支持本接口所属卸载字段，其他名称在任何查询或写入前拒绝。</param>
    /// <returns>成功或带原因的失败响应。</returns>
    public static Task<ApiResponse> ApplyOffloadSettingsAsync(Guid vmId, VmNetworkAdapter adapter,
        IReadOnlyCollection<string> changedProperties) => ApplySelectedFeatureAsync(vmId, adapter, changedProperties,
            "Msvm_EthernetSwitchPortOffloadSettingData", OffloadFields);
    /// <summary>应用选中的安全与卸载字段。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="adapter">查询所得网卡及请求值。</param>
    /// <param name="changedProperties">安全或卸载属性名的非空集合，使用 nameof。</param>
    /// <returns>仅返回整体成败；需要分别显示安全与卸载设置结果时使用 ApplySecurityAndOffloadSettingsWithResultAsync。</returns>
    public static async Task<ApiResponse> ApplySecurityAndOffloadSettingsAsync(Guid vmId, VmNetworkAdapter adapter,
        IReadOnlyCollection<string> changedProperties)
    {
        var result = await ApplySecurityAndOffloadSettingsWithResultAsync(vmId, adapter, changedProperties);
        return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Message, result.Failure?.Code ?? -1, result.Failure?.Source ?? ApiErrorSource.None, kind: result.Failure?.Kind ?? ApiErrorKind.Unspecified);
    }

    /// <summary>应用安全与卸载设置，并分别返回两个步骤的结果。先应用安全设置，再应用卸载设置；后一步失败时，已经成功的安全设置保留。</summary>
    /// <param name="vmId">目标虚拟机 GUID。</param>
    /// <param name="adapter">查询所得网卡及请求值。</param>
    /// <param name="changedProperties">安全或卸载属性名的非空集合，使用 nameof；开始前复制全部请求值。</param>
    /// <returns>整体结果及各类别是否已执行成功；未选择的类别为 false。</returns>
    public static async Task<VmNetworkSettingsApplyResult> ApplySecurityAndOffloadSettingsWithResultAsync(Guid vmId,
        VmNetworkAdapter adapter, IReadOnlyCollection<string> changedProperties)
    {
        SettingsSelection<VmNetworkAdapter> selection;
        try { selection = SettingsSelection<VmNetworkAdapter>.Capture(adapter, changedProperties, SecurityFields.Concat(OffloadFields)); }
        catch (ArgumentException ex) { return new(false, ex.Message, false, false) { Failure = ApiFailure.From(ex) }; }
        string id = adapter.Id;
        var ownership = await ValidateAdapterOwnershipAsync(vmId, id);
        if (!ownership.Success) return new(false, ownership.Error, false, false) { Failure = ApiFailure.From(ownership) };
        bool securityDone = false, offloadDone = false;
        if (selection.Names.Any(SecurityFields.Contains))
        {
            var security = await ApplyCapturedFeatureAsync(id, "Msvm_EthernetSwitchPortSecuritySettingData", selection);
            if (!security.Success) return new(false, security.Error, false, false) { Failure = ApiFailure.From(security), Security = OperationStepResult.From("Security", security) };
            securityDone = true;
        }
        if (selection.Names.Any(OffloadFields.Contains))
        {
            var offload = await ApplyCapturedFeatureAsync(id, "Msvm_EthernetSwitchPortOffloadSettingData", selection);
            if (!offload.Success) return new(false, offload.Error, securityDone, false) { Failure = ApiFailure.From(offload), Offload = OperationStepResult.From("Offload", offload) };
            offloadDone = true;
        }
        return new(true, string.Empty, securityDone, offloadDone);
    }

    internal static async Task<ApiResponse> ApplySecurityAndOffloadSettingsAsync(
        Func<Task<ApiResponse>> applySecurity,
        Func<Task<ApiResponse>> applyOffload)
    {
        var result = await ApplySecurityAndOffloadSettingsWithResultAsync(applySecurity, applyOffload);
        return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Message);
    }

    internal static async Task<VmNetworkSettingsApplyResult> ApplySecurityAndOffloadSettingsWithResultAsync(
        Func<Task<ApiResponse>> applySecurity,
        Func<Task<ApiResponse>> applyOffload)
    {
        var security = await applySecurity();
        if (!security.Success)
            return new(false, string.Format(Properties.Resources.Error_Net_Security, security.Error), false, false) { Failure = ApiFailure.From(security), Security = OperationStepResult.From("Security", security) };

        var offload = await applyOffload();
        if (!offload.Success)
            return new(false, string.Format(Properties.Resources.Error_Net_Offload, offload.Error), true, false) { Failure = ApiFailure.From(offload), Offload = OperationStepResult.From("Offload", offload) };

        return new(true, string.Empty, true, true);
    }

    // ── 内部逻辑 ──────────────────────────────────────────────

    private static async Task<(bool Success, string Message)> ToLegacyAsync(Task<ApiResponse> operation)
    {
        var result = await operation;
        return (result.Success, result.Error);
    }

    private static ApiResponse Failure<T>(ApiResponse<T> result) =>
        ApiResponse.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);

    private static async Task<ApiResponse> ValidateAdapterOwnershipAsync(Guid vmId, string id)
    {
        if (vmId == Guid.Empty)
            return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        if (string.IsNullOrWhiteSpace(id))
            return ApiResponse.Fail("请使用查询返回的虚拟机网卡。", kind: ApiErrorKind.InvalidArgument);
        var vm = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            obj => obj["Name"]?.ToString());
        if (!vm.Success) return Failure(vm);
        if (!vm.HasData) return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);
        if (!Guid.TryParse(vm.Data, out Guid actualVmId)
            || !id.StartsWith($"Microsoft:{actualVmId:D}\\", StringComparison.OrdinalIgnoreCase))
            return ApiResponse.Fail("网卡不属于指定虚拟机，请重新查询。", kind: ApiErrorKind.Conflict);
        return ApiResponse.Ok();
    }

    // 网卡 setting 所在的 WMI 类:旧版网卡 InstanceID 3 段(Microsoft:VMGUID\DEVICEGUID\0)属 Emulated 类，
    // 两段式 Microsoft:VMGUID\DEVICEGUID 标识对应合成网卡。
    private static string PortSettingClass(string instanceId) =>
        (instanceId ?? "").Split('\\').Length >= 3
            ? "Msvm_EmulatedEthernetPortSettingData"
            : "Msvm_SyntheticEthernetPortSettingData";

    private static async Task<ApiResponse> EnsureAndModifyFeatureAsync(
        string portId, string featureClass, Action<ManagementObject> updateAction)
    {
        try
        {
            string escapedId = WmiApi.Escape(portId);

            var xmlInfo = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM {PortSettingClass(portId)} WHERE InstanceID = '{escapedId}'",
                port =>
                {
                    using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData")
                        .Cast<ManagementObject>().FirstOrDefault();
                    if (allocation == null) return null;

                    var existing = allocation.GetRelated(
                        featureClass, "Msvm_EthernetPortSettingDataComponent",
                        null, null, null, null, false, null)
                        .Cast<ManagementObject>().FirstOrDefault();

                    if (existing != null)
                    {
                        updateAction(existing);
                        return new { IsNew = false, Xml = existing.GetText(TextFormat.CimDtd20), Target = string.Empty };
                    }
                    else
                    {
                        var template = GetDefaultFeatureTemplate(featureClass);
                        if (template == null) return null;
                        template["InstanceID"] = Guid.NewGuid().ToString();
                        updateAction(template);
                        return new { IsNew = true, Xml = template.GetText(TextFormat.CimDtd20), Target = allocation.Path.Path };
                    }
                });

            if (!xmlInfo.Success) return Failure(xmlInfo);
            if (!xmlInfo.HasData || xmlInfo.Data == null)
                return ApiResponse.Fail(Properties.Resources.Error_Net_ConfigObject);

            var info = xmlInfo.Data;
            string method = info.IsNew ? "AddFeatureSettings" : "ModifyFeatureSettings";

            var result = await WmiApi.InvokeAsync(
                ServiceWql,
                method,
                p =>
                {
                    p["FeatureSettings"] = new string[] { info.Xml };
                    if (info.IsNew) p["AffectedConfiguration"] = info.Target;
                });

            return result;
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    }

    // ── 业务逻辑 ──────────────────────────────────────────────────

    private static void ParseFeatureSettings(VmNetworkAdapter adapter, ManagementObject feature)
    {
        string cls = feature.ClassPath.ClassName;

        if (cls == "Msvm_EthernetSwitchPortVlanSettingData")
        {
            uint rawMode = feature.TryGet<uint>("OperationMode") ?? 0;
            adapter.VlanMode = rawMode == 0 ? VlanOperationMode.Access : (VlanOperationMode)rawMode;
            adapter.AccessVlanId = (int)(feature.TryGet<uint>("AccessVlanId") ?? 0);
            adapter.NativeVlanId = (int)(feature.TryGet<uint>("NativeVlanId") ?? 0);
            adapter.PvlanMode = (PvlanMode)(feature.TryGet<uint>("PvlanMode") ?? 0);
            adapter.PvlanPrimaryId = (int)(feature.TryGet<uint>("PrimaryVlanId") ?? 0);
            adapter.PvlanSecondaryId = (int)(feature.TryGet<uint>("SecondaryVlanId") ?? 0);
            if (feature.HasProperty("TrunkVlanIdArray") && feature["TrunkVlanIdArray"] is ushort[] trunks)
                adapter.TrunkAllowedVlanIds = trunks.Select(x => (int)x).ToList();
        }
        else if (cls == "Msvm_EthernetSwitchPortBandwidthSettingData")
        {
            adapter.BandwidthLimitBitsPerSecond = (feature.TryGet<ulong>("Limit") ?? 0);
            adapter.BandwidthReservationBitsPerSecond = (feature.TryGet<ulong>("Reservation") ?? 0);
        }
        else if (cls == "Msvm_EthernetSwitchPortSecuritySettingData")
        {
            adapter.MacSpoofingAllowed = feature.TryGet<bool>("AllowMacSpoofing") ?? false;
            adapter.DhcpGuardEnabled = feature.TryGet<bool>("EnableDhcpGuard") ?? false;
            adapter.RouterGuardEnabled = feature.TryGet<bool>("EnableRouterGuard") ?? false;
            adapter.TeamingAllowed = feature.TryGet<bool>("AllowTeaming") ?? false;
            adapter.MonitorMode = (PortMonitorMode)(feature.TryGet<byte>("MonitorMode") ?? 0);
            adapter.StormLimit = feature.TryGet<uint>("StormLimit") ?? 0;
        }
        else if (cls == "Msvm_EthernetSwitchPortOffloadSettingData")
        {
            adapter.VmqEnabled = (feature.TryGet<uint>("VMQOffloadWeight") ?? 0) > 0;
            adapter.SriovEnabled = (feature.TryGet<uint>("IOVOffloadWeight") ?? 0) > 0;
            adapter.IpsecOffloadEnabled = (feature.TryGet<uint>("IPSecOffloadLimit") ?? 0) > 0;
        }
    }

    private static Task<ApiResponse<string?>> GetSwitchNameByGuidAsync(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return Task.FromResult(ApiResponse<string?>.Empty());
        return WmiApi.QueryFirstAsync(
            $"SELECT ElementName FROM Msvm_VirtualEthernetSwitch WHERE Name = '{WmiApi.Escape(guid)}'",
            obj => obj["ElementName"]?.ToString());
    }

    private static ManagementObject? GetDefaultFeatureTemplate(string className)
    {
        using var svcForScope = WmiApi.GetVirtualSystemManagementService();
        using var searcher = new ManagementObjectSearcher(svcForScope.Scope,
            new ObjectQuery($"SELECT * FROM {className} WHERE InstanceID LIKE '%Default%'"));
        using var col = searcher.Get();
        return col.Cast<ManagementObject>().FirstOrDefault();
    }
}
