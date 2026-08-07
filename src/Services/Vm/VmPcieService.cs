using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 单台虚拟机的 PCIe 设置。
/// 所有可选属性均从实际 WMI 对象探测，旧版宿主缺少属性时将对应设置标记为不可用。
/// </summary>
public static class VmPcieService
{
    private const string VsmsWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    public static async Task<ApiResponse<VmPcieSettings>> GetSettingsAsync(string vmName)
    {
        string escapedName = WmiApi.Escape(vmName);
        var vssdResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE ElementName = '{escapedName}' " +
            $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            obj => new
            {
                InstanceId = WmiApi.PropStr(obj, "InstanceID"),
                BootAvailable = obj.HasProperty("BootPciExpress"),
                BootEnabled = obj.TryGet<bool>("BootPciExpress") ?? false,
            });

        if (!vssdResponse.Success)
            return ApiResponse<VmPcieSettings>.Fail(
                vssdResponse.Error, vssdResponse.Code, vssdResponse.ErrorSource);
        if (!vssdResponse.HasData)
            return ApiResponse<VmPcieSettings>.Empty();

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

        // 旧版宿主缺少可选类属于正常情况；类存在但查询失败时才向上返回错误。
        if (!systemResponse.Success && systemResponse.Code != (int)ManagementStatus.InvalidClass)
            return ApiResponse<VmPcieSettings>.Fail(
                systemResponse.Error, systemResponse.Code, systemResponse.ErrorSource);

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
                deviceResponse.Error, deviceResponse.Code, deviceResponse.ErrorSource);

        // 设备是否实际属于此 VM，以及设备的名称、类别、路径和厂商，统一采用
        // 主 PCIe 页面已经完成归属判定后的结果。这里仅保留 VM 专属设置的读取。
        var (mainDevices, _) = await PCIeService.GetPCIeInfoAsync();
        var assignedDevices = mainDevices
            .Where(device => string.Equals(
                device.Status, vmName, StringComparison.OrdinalIgnoreCase))
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
            if (!assignedDevices.TryGetValue(key, out var mainDevice))
                continue;

            string name = mainDevice.FriendlyName;
            string classType = string.IsNullOrWhiteSpace(mainDevice.DisplayClassType)
                ? mainDevice.ClassType
                : mainDevice.DisplayClassType;

            devices.Add(new VmPcieDeviceSetting
            {
                WmiPath = device.Path,
                WmiInstanceId = device.InstanceId,
                Name = name,
                HostResource = device.HostResource,
                GuestModeAvailable = device.ModeAvailable,
                ClassType = classType,
                DeviceInstanceId = mainDevice.InstanceId,
                Path = mainDevice.Path,
                Vendor = mainDevice.Vendor,
                IconGlyph = DeviceIcons.GetGlyph(mainDevice.ClassType, name),
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

    public static async Task<ApiResponse> SetSystemSettingsAsync(
        string vmName, bool enableEmulation, ushort topology)
    {
        if (!HostAzureFeatureSetService.IsEnabled())
            return ApiResponse.Fail(Properties.Resources.Error_AzureFeatureSetRequired);

        string escapedName = WmiApi.Escape(vmName);
        var id = await GetVssdInstanceIdAsync(escapedName);
        if (!id.Success || !id.HasData)
            return ApiResponse.Fail(
                id.Error.Length > 0
                    ? id.Error
                    : PcieResource("VmPcie_SettingsNotFound"));

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

    public static async Task<ApiResponse> SetDeviceModeAsync(
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
        if (mode == VmPcieGuestMode.Emulated && !HostAzureFeatureSetService.IsEnabled())
            return ApiResponse.Fail(Properties.Resources.Error_AzureFeatureSetRequired);

        return await ApplyAsync();
    }

    public static async Task<ApiResponse> SetBootPciExpressAsync(string vmName, bool enabled)
    {
        string escapedName = WmiApi.Escape(vmName);
        return await WmiApi.WithObjectAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE ElementName = '{escapedName}' " +
            $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            obj => obj["BootPciExpress"] = enabled,
            submitMethod: "ModifySystemSettings",
            submitParamName: "SystemSettings",
            serviceWql: VsmsWql);
    }

    private static Task<ApiResponse<string>> GetVssdInstanceIdAsync(string escapedVmName)
        => WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData " +
            $"WHERE ElementName = '{escapedVmName}' " +
            $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            obj => WmiApi.PropStr(obj, "InstanceID"));

    private static string PcieResource(string name)
        => Properties.Resources.ResourceManager.GetString(name) ?? name;

    private static string ExtractPhysicalDeviceId(string hostResource)
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

    private static string NormalizePhysicalDeviceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return string.Empty;
        int index = instanceId.IndexOf(@"\VEN_", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? instanceId[index..] : instanceId;
    }

}
