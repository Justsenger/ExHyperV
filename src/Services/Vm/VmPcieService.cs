using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>
/// 单台虚拟机的 PCIe 设置。
/// 所有可选属性均从实际 WMI 对象探测，旧版宿主缺少属性时将对应设置标记为不可用。
/// </summary>
public static class VmPcieService
{
    private const string VsmsWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string VirtualizationRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
    private const string AzureFeatureSetValueName = "AzureFeatureSet";
    private static readonly SemaphoreSlim AzureFeatureSetLock = new(1, 1);

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
                    ElementName = WmiApi.PropStr(obj, "ElementName"),
                };
            });

        if (!deviceResponse.Success)
            return ApiResponse<VmPcieSettings>.Fail(
                deviceResponse.Error, deviceResponse.Code, deviceResponse.ErrorSource);

        var hostDevices = await Task.Run(BuildHostDeviceMap);
        var pciIds = new PciIds();
        if ((deviceResponse.Data?.Count ?? 0) > 0)
        {
            try { await pciIds.EnsureInitializedAsync(); }
            catch { }
        }

        var devices = new List<VmPcieDeviceSetting>();
        foreach (var device in deviceResponse.Data ?? [])
        {
            string resourceDeviceId = ExtractPhysicalDeviceId(device.HostResource);
            string key = NormalizePhysicalDeviceId(resourceDeviceId);
            hostDevices.TryGetValue(key, out var hostDevice);

            string name = hostDevice != null
                ? hostDevice.FriendlyName
                : !string.IsNullOrWhiteSpace(device.ElementName)
                    ? device.ElementName
                    : key;
            string physicalInstanceId = hostDevice?.InstanceId ?? resourceDeviceId;
            string classType = hostDevice?.Class ?? string.Empty;

            devices.Add(new VmPcieDeviceSetting
            {
                WmiPath = device.Path,
                WmiInstanceId = device.InstanceId,
                Name = name,
                HostResource = device.HostResource,
                GuestModeAvailable = device.ModeAvailable,
                ClassType = classType,
                DeviceInstanceId = physicalInstanceId,
                Path = hostDevice?.FirstLocationPath ?? string.Empty,
                Vendor = pciIds.GetVendorFromInstanceId(physicalInstanceId, classType),
                IconGlyph = DeviceIcons.GetGlyph(classType, name),
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
        return await WithTemporaryAzureFeatureSetAsync(async () =>
        {
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
        });
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
        return mode == VmPcieGuestMode.Emulated
            ? await WithTemporaryAzureFeatureSetAsync(ApplyAsync)
            : await ApplyAsync();
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

    private static async Task<ApiResponse> WithTemporaryAzureFeatureSetAsync(
        Func<Task<ApiResponse>> operation)
    {
        await AzureFeatureSetLock.WaitAsync();
        try
        {
            var enableResult = EnableAzureFeatureSetTemporarily();
            if (!enableResult.Success || !enableResult.HasData)
                return ApiResponse.Fail(
                    enableResult.Error, enableResult.Code, enableResult.ErrorSource);

            var state = enableResult.Data!;
            ApiResponse operationResult;
            try
            {
                operationResult = await operation();
            }
            catch
            {
                _ = RestoreAzureFeatureSet(state);
                throw;
            }

            var restoreResult = RestoreAzureFeatureSet(state);
            if (restoreResult.Success) return operationResult;
            if (operationResult.Success) return restoreResult;

            return ApiResponse.Fail(
                $"{operationResult.Error}{Environment.NewLine}{restoreResult.Error}",
                operationResult.Code,
                operationResult.ErrorSource);
        }
        finally
        {
            AzureFeatureSetLock.Release();
        }
    }

    private static ApiResponse<AzureFeatureSetState> EnableAzureFeatureSetTemporarily()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                VirtualizationRegistryPath, writable: true);
            if (key == null)
                return ApiResponse<AzureFeatureSetState>.Fail(
                    PcieResource("VmPcie_AzureFeatureSetOpenFailed"));

            object? originalValue = key.GetValue(
                AzureFeatureSetValueName, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            bool existed = originalValue != null;
            RegistryValueKind? originalKind =
                existed ? key.GetValueKind(AzureFeatureSetValueName) : null;
            bool changed = originalValue is not int value || value != 1;
            if (!changed)
                return ApiResponse<AzureFeatureSetState>.Ok(
                    new AzureFeatureSetState(false, true, originalValue, originalKind));

            key.SetValue(AzureFeatureSetValueName, 1, RegistryValueKind.DWord);
            return ApiResponse<AzureFeatureSetState>.Ok(
                new AzureFeatureSetState(true, existed, originalValue, originalKind));
        }
        catch (Exception ex)
        {
            return ApiResponse<AzureFeatureSetState>.Fail(
                string.Format(
                    PcieResource("VmPcie_AzureFeatureSetEnableFailed"),
                    ex.Message));
        }
    }

    private static ApiResponse RestoreAzureFeatureSet(AzureFeatureSetState state)
    {
        if (!state.Changed) return ApiResponse.Ok();

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                VirtualizationRegistryPath, writable: true);
            if (key == null)
                return ApiResponse.Fail(
                    PcieResource("VmPcie_AzureFeatureSetOpenFailed"));

            if (state.Existed)
                key.SetValue(
                    AzureFeatureSetValueName,
                    state.OriginalValue!,
                    state.OriginalKind!.Value);
            else
                key.DeleteValue(
                    AzureFeatureSetValueName,
                    throwOnMissingValue: false);

            return ApiResponse.Ok();
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(
                string.Format(
                    PcieResource("VmPcie_AzureFeatureSetRestoreFailed"),
                    ex.Message));
        }
    }

    private static string PcieResource(string name)
        => Properties.Resources.ResourceManager.GetString(name) ?? name;

    private sealed record AzureFeatureSetState(
        bool Changed,
        bool Existed,
        object? OriginalValue,
        RegistryValueKind? OriginalKind);

    private static Dictionary<string, PciDeviceInfo> BuildHostDeviceMap()
    {
        var candidates = new Dictionary<string, (PciDeviceInfo Device, int Score)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var device in Win32Api.GetAllDevices())
        {
            string key = NormalizePhysicalDeviceId(device.InstanceId);
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(device.FriendlyName))
                continue;

            // DDA 设备通常同时保留 PCI\ 原设备记录和 PCIP\ 可分配设备记录。
            // 两者归一化后的硬件 ID 相同；优先采用 PCI\ 记录获取原始名称和设备类别。
            // 此处只依据稳定的实例 ID 前缀，不使用随系统语言或驱动变化的显示名称。
            int score = device.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
                ? 2
                : device.InstanceId.StartsWith(@"PCIP\", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;

            if (!candidates.TryGetValue(key, out var current) || score > current.Score)
                candidates[key] = (device, score);
        }
        return candidates.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Device,
            StringComparer.OrdinalIgnoreCase);
    }

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
