using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 单台虚拟机的 PCIe 设置。
/// 所有可选属性均从实际 WMI 对象探测，旧版主机缺少属性时将对应设置标记为不可用。
/// </summary>
public static class VmPcieService
{
    private const string VsmsWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    /// <summary>评估 PCIe 呈现配置或启动配置的修改条件。</summary>
    /// <param name="settings">查询得到的 PCIe 配置。</param>
    /// <param name="propertyName">EmulationEnabled、Topology 或 BootPciExpress。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改及原因；不用于设备分配或取消分配。</returns>
    public static VmModificationCondition GetModificationCondition(VmPcieSettings settings, string propertyName, VmState state)
    {
        bool available;
        switch (propertyName)
        {
            case nameof(VmPcieSettings.EmulationEnabled):
                if (settings.EmulationEnabled)
                    return VmModificationCondition.Unsupported("PCIe 仿真启用后不能关闭。");
                available = settings.SystemSettingsAvailable;
                break;
            case nameof(VmPcieSettings.Topology): available = settings.SystemSettingsAvailable; break;
            case nameof(VmPcieSettings.BootPciExpress): available = settings.BootPciExpressAvailable; break;
            default: return VmModificationCondition.Denied("未知的 PCIe 设置。", ApiErrorKind.InvalidArgument);
        }
        return available ? VmModificationCondition.PoweredOff(state)
            : VmModificationCondition.Unsupported("当前虚拟机未提供此 PCIe 设置。");
    }

    /// <summary>评估已分配设备的来宾呈现模式能否修改。</summary>
    /// <param name="device">查询得到的 PCIe 设备配置。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改及原因。</returns>
    public static VmModificationCondition GetDeviceModeModificationCondition(VmPcieDeviceSetting device, VmState state)
        => device.GuestModeAvailable ? VmModificationCondition.PoweredOff(state)
            : VmModificationCondition.Unsupported("此设备不支持修改来宾模式。");

    private static async Task<ApiResponse> CheckPoweredOffAsync(Guid vmId)
    {
        var current = await VmConfigurationState.ReadAsync(vmId);
        if (!current.HasData) return current.Success
            ? ApiResponse.Fail("未找到虚拟机。", kind: ApiErrorKind.NotFound)
            : ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
        return VmModificationCondition.PoweredOff(current.Data.State).ToResponse();
    }

    /// <summary>读取虚拟机 PCIe 模拟、拓扑和设备来宾模式等设置。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>当前配置和支持情况；读取失败通过响应返回。</returns>
    public static async Task<ApiResponse<VmPcieSettings>> GetSettingsAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<VmPcieSettings>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var vssdResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => new
            {
                InstanceId = WmiApi.PropStr(obj, "InstanceID"),
                BootAvailable = obj.HasProperty("BootPciExpress"),
                BootEnabled = obj.TryGet<bool>("BootPciExpress") ?? false,
            });

        if (!vssdResponse.Success)
            return ApiResponse<VmPcieSettings>.Fail(
                vssdResponse.Error, vssdResponse.Code, vssdResponse.ErrorSource, vssdResponse.Exception, vssdResponse.ErrorKind);
        if (!vssdResponse.HasData)
            return ApiResponse<VmPcieSettings>.Fail(Properties.Resources.VmPcie_SettingsNotFound, kind: ApiErrorKind.NotFound);

        string settingId = vssdResponse.Data!.InstanceId;
        string escapedSettingId = WmiApi.Escape(settingId);

        var systemResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_VirtualSystemPciExpressSettingData " +
            $"WHERE InstanceID LIKE '{escapedSettingId}\\\\%'",
            obj => new
            {
                Available = obj.HasProperty("EmulationMode") && obj.HasProperty("Topology"),
                Emulation = obj.TryGet<ushort>("EmulationMode") ?? 0,
                Topology = obj.TryGet<ushort>("Topology") ?? 0,
            });

        // 旧版主机缺少可选类属于正常情况；类存在但查询失败时才向上返回错误。
        if (!systemResponse.Success && systemResponse.Code != (int)ManagementStatus.InvalidClass)
            return ApiResponse<VmPcieSettings>.Fail(
                systemResponse.Error, systemResponse.Code, systemResponse.ErrorSource, systemResponse.Exception, systemResponse.ErrorKind);

        var deviceResponse = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_PciExpressSettingData " +
            $"WHERE InstanceID LIKE '{escapedSettingId}\\\\%'",
            obj =>
            {
                string[] resources = obj["HostResource"] as string[] ?? [];
                return new
                {
                    Path = obj.Path.Path,
                    InstanceId = WmiApi.PropStr(obj, "InstanceID"),
                    HostResource = resources.FirstOrDefault() ?? string.Empty,
                    ModeAvailable = obj.HasProperty("GuestPciExpressMode"),
                    Mode = obj.TryGetByte("GuestPciExpressMode") ?? 0,
                };
            });

        if (!deviceResponse.Success)
            return ApiResponse<VmPcieSettings>.Fail(
                deviceResponse.Error, deviceResponse.Code, deviceResponse.ErrorSource, deviceResponse.Exception, deviceResponse.ErrorKind);

        // 归属由当前 GUID 的配置资源确定，宿主清单只补充物理设备资料，不再按显示名称判断归属。
        var host = await HostPcieService.GetPCIeInfoAsync();
        if (!host.HasData) return ApiResponse<VmPcieSettings>.Fail(host.Error, host.Code, host.ErrorSource, host.Exception, host.ErrorKind);
        var assignedDevices = host.Data.Devices
            .Select(device => new
            {
                Key = NormalizePhysicalDeviceId(device.InstanceId),
                Device = device,
            })
            .Where(item => !string.IsNullOrEmpty(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Device,
                StringComparer.OrdinalIgnoreCase);

        var devices = new List<VmPcieDeviceSetting>();
        foreach (var device in deviceResponse.Data ?? [])
        {
            string resourceDeviceId = ExtractPhysicalDeviceId(device.HostResource);
            string key = NormalizePhysicalDeviceId(resourceDeviceId);
            assignedDevices.TryGetValue(key, out var mainDevice);

            string name = mainDevice?.FriendlyName ?? "PCIe 设备";
            string classType = string.IsNullOrWhiteSpace(mainDevice?.DisplayClassType)
                ? mainDevice?.ClassType ?? string.Empty
                : mainDevice.DisplayClassType;

            devices.Add(new VmPcieDeviceSetting
            {
                WmiPath = device.Path,
                WmiInstanceId = device.InstanceId,
                Name = name,
                HostResource = device.HostResource,
                GuestModeAvailable = device.ModeAvailable,
                ClassType = classType,
                DeviceInstanceId = mainDevice?.InstanceId ?? string.Empty,
                Path = mainDevice?.Path ?? string.Empty,
                Vendor = mainDevice?.Vendor ?? string.Empty,
                GuestMode = (VmPcieGuestMode)device.Mode,
                AppliedGuestMode = (VmPcieGuestMode)device.Mode,
            });
        }

        var system = systemResponse.HasData ? systemResponse.Data : null;
        return ApiResponse<VmPcieSettings>.Ok(new VmPcieSettings
        {
            SystemSettingsAvailable = system?.Available == true,
            EmulationEnabled = system?.Emulation == 1,
            Topology = system?.Topology ?? 0,
            BootPciExpressAvailable = vssdResponse.Data.BootAvailable,
            BootPciExpress = vssdResponse.Data.BootEnabled,
            Devices = devices,
        });
    }

    /// <summary>修改虚拟机的 PCIe 模拟与拓扑；开启模拟可能需要临时修改宿主功能开关。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enableEmulation">true 开启模拟模式；false 保留现有模式。两者均写入 topology。</param>
    /// <param name="topology">Hyper-V 原始 PCIe 拓扑编码，由宿主校验。</param>
    /// <returns>配置结果；模拟开启具有持久副作用，调用者应先完成必要确认。</returns>
    public static async Task<ApiResponse> SetSystemSettingsAsync(
        Guid vmId, bool enableEmulation, ushort topology)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        async Task<ApiResponse> ApplyAsync()
        {
            // VMMS 的呈现配置 setter 拒绝活动 worker 和保存状态；这不是 DDA 分配的限制。
            var condition = await CheckPoweredOffAsync(vmId);
            if (!condition.Success) return condition;
            var id = await GetVssdInstanceIdAsync(vmId);
            if (!id.Success || !id.HasData)
                return ApiResponse.Fail(
                    id.Error.Length > 0
                        ? id.Error
                        : Properties.Resources.VmPcie_SettingsNotFound, id.Code, id.ErrorSource, id.Exception,
                    id.Success ? ApiErrorKind.NotFound : id.ErrorKind);

            string escapedId = WmiApi.Escape(id.Data!);
            return await WmiApi.WithObjectAsync(
                $"SELECT * FROM Msvm_VirtualSystemPciExpressSettingData " +
                $"WHERE InstanceID LIKE '{escapedId}\\\\%'",
                obj =>
                {
                    if (enableEmulation) obj["EmulationMode"] = (ushort)1;
                    obj["Topology"] = topology;
                },
                submitMethod: "ModifySystemComponentSettings",
                submitParamName: "ComponentSettings",
                wrapInArray: true,
                serviceWql: VsmsWql);
        }

        return await HostAzureFeatureSetService.RunTemporarilyEnabledAsync(ApplyAsync);
    }

    /// <summary>修改指定虚拟机中已查询设备的来宾模式，执行前重新核对设备归属和能力。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="device">GetSettingsAsync 返回的设备；不接受自行构造的底层对象标识。</param>
    /// <param name="mode">要配置的来宾设备模式。</param>
    /// <returns>修改结果；设备已移除、归属不符或不支持此设置时失败。</returns>
    public static async Task<ApiResponse> SetDeviceModeAsync(
        Guid vmId, VmPcieDeviceSetting device, VmPcieGuestMode mode)
    {
        if (vmId == Guid.Empty || device == null || !Enum.IsDefined(mode)) return ApiResponse.Fail("虚拟机 GUID、设备或 PCIe 来宾模式无效。", kind: ApiErrorKind.InvalidArgument);
        if (string.IsNullOrEmpty(device.WmiInstanceId))
            return ApiResponse.Fail("请使用查询返回的 PCIe 设备。", kind: ApiErrorKind.InvalidArgument);

        var current = await GetSettingsAsync(vmId);
        if (!current.Success) return ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
        var target = current.Data?.Devices.FirstOrDefault(item =>
            string.Equals(item.WmiInstanceId, device.WmiInstanceId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return ApiResponse.Fail("设备已不属于此虚拟机，请重新查询。", kind: ApiErrorKind.Conflict);
        if (!target.GuestModeAvailable) return ApiResponse.Fail("此设备不支持修改来宾模式。", kind: ApiErrorKind.Unsupported);
        var state = await VmConfigurationState.ReadAsync(vmId);
        if (!state.HasData) return state.Success ? ApiResponse.Fail("未找到虚拟机。", kind: ApiErrorKind.NotFound)
            : ApiResponse.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);
        var condition = GetDeviceModeModificationCondition(target, state.Data.State);
        if (!condition.CanModify) return condition.ToResponse();
        return await SetDeviceModeAsync(target.WmiInstanceId, mode);
    }

    /// <summary>按内部设备实例标识修改 PCIe 设备的来宾模式。</summary>
    /// <param name="instanceId">目标设备的 WMI 实例标识。</param>
    /// <param name="mode">要配置的来宾设备模式，使用 VmPcieGuestMode 枚举值。</param>
    /// <returns>WMI 修改结果；无效实例、状态限制或底层拒绝时失败。</returns>
    internal static async Task<ApiResponse> SetDeviceModeAsync(
        string instanceId, VmPcieGuestMode mode)
    {
        async Task<ApiResponse> ApplyAsync()
        {
            string escapedId = WmiApi.Escape(instanceId);
            return await WmiApi.WithObjectAsync(
                $"SELECT * FROM Msvm_PciExpressSettingData WHERE InstanceID = '{escapedId}'",
                obj => obj["GuestPciExpressMode"] = (byte)mode,
                submitMethod: "ModifyResourceSettings",
                submitParamName: "ResourceSettings",
                wrapInArray: true,
                serviceWql: VsmsWql);
        }

        // 半虚拟化不依赖 AzureFeatureSet；只有标准 PCIe 仿真需要。
        return mode == VmPcieGuestMode.Emulated
            ? await HostAzureFeatureSetService.RunTemporarilyEnabledAsync(ApplyAsync)
            : await ApplyAsync();
    }

    /// <summary>在引导配置中设置指定虚拟机是否启用 PCI Express。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enabled">true 请求启用该功能，false 请求关闭；互锁限制由对应操作执行。</param>
    /// <returns>启动阶段 vPCI 开关的配置提交结果，不证明已发现可启动磁盘或已成功引导。</returns>
    /// <remarks>
    /// <para>已研究的 NVMe 引导路径使用 PCI 类码 01/08/02。</para>
    /// <para>ARM64 vmfirmware.dll 10.0.26100.4202、10.0.28000.1 的已核对样本会将非零且小于 4 KiB 的有效 64 位内存 BAR 算成零页，使设备初始化失败；10.0.28000.1450 样本修正了此算法，但仍拒绝 I/O BAR 和 32 位内存 BAR。按实际固件组件版本核对。</para>
    ///
    /// <para>证据：Hyper-V WMI 研究的 nvme-boot 组件对照；微软固件修复 <see href="https://github.com/microsoft/mu_msvm/commit/3fe19d067b3eacd5e3991e5e02e476a810c94f6b">3fe19d0</see>。首次修复版本、未采样维护分支和其他架构不据此推定。</para>
    /// </remarks>
    public static async Task<ApiResponse> SetBootPciExpressAsync(Guid vmId, bool enabled)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var condition = await CheckPoweredOffAsync(vmId);
        if (!condition.Success) return condition;
        return await WmiApi.WithObjectAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => obj["BootPciExpress"] = enabled,
            submitMethod: "ModifySystemSettings",
            submitParamName: "SystemSettings",
            serviceWql: VsmsWql);
    }

    private static Task<ApiResponse<string>> GetVssdInstanceIdAsync(Guid vmId)
        => WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData " +
            $"WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => WmiApi.PropStr(obj, "InstanceID"));

    internal static async Task<ApiResponse<VmPcieSettings>> GetSettingsAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        return identity.HasData ? await GetSettingsAsync(identity.Data)
            : ApiResponse<VmPcieSettings>.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
    }

    internal static async Task<ApiResponse> SetSystemSettingsAsync(string vmName, bool enabled, ushort topology)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        return identity.HasData ? await SetSystemSettingsAsync(identity.Data, enabled, topology)
            : ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
    }

    internal static async Task<ApiResponse> SetDeviceModeAsync(string vmName, VmPcieDeviceSetting device, VmPcieGuestMode mode)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        return identity.HasData ? await SetDeviceModeAsync(identity.Data, device, mode)
            : ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
    }

    internal static async Task<ApiResponse> SetBootPciExpressAsync(string vmName, bool enabled)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        return identity.HasData ? await SetBootPciExpressAsync(identity.Data, enabled)
            : ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
    }

    internal static string ExtractPhysicalDeviceId(string hostResource)
    {
        var match = Regex.Match(
            hostResource,
            "DeviceID=\"Microsoft:[^\\\\]+\\\\\\\\(.+?)\"",
            RegexOptions.IgnoreCase);
        string id = match.Success
            ? match.Groups[1].Value.Replace("\\\\", "\\")
            : hostResource;
        return id;
    }

    internal static string NormalizePhysicalDeviceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return string.Empty;
        int index = instanceId.IndexOf(@"\VEN_", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? instanceId[index..] : instanceId;
    }

}
