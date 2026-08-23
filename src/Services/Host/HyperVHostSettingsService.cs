using ExHyperV.Tools;

namespace ExHyperV.Services;

public sealed record HyperVHostSettings(
    bool EnhancedSessionModeEnabled,
    string DefaultVirtualMachinePath,
    string DefaultVirtualHardDiskPath,
    string MinimumMacAddress,
    string MaximumMacAddress);

public static class HyperVHostSettingsService
{
    private const string SettingWql = "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    public static async Task<HyperVHostSettings?> GetAsync()
    {
        var response = await WmiApi.QueryFirstAsync(
            SettingWql,
            obj => new HyperVHostSettings(
                obj.TryGet<bool>("EnhancedSessionModeEnabled") ?? false,
                obj.TryGetString("DefaultExternalDataRoot")
                    ?? obj.TryGetString("DefaultVirtualMachinePath")
                    ?? string.Empty,
                obj.TryGetString("DefaultVirtualHardDiskPath") ?? string.Empty,
                obj.TryGetString("MinimumMacAddress") ?? string.Empty,
                obj.TryGetString("MaximumMacAddress") ?? string.Empty));

        return response.Success && !response.IsEmpty ? response.Data : null;
    }

    public static Task<ApiResponse> SetEnhancedSessionModeEnabledAsync(bool enabled) =>
        ModifyAsync(obj => obj["EnhancedSessionModeEnabled"] = enabled);

    public static Task<ApiResponse> SetDefaultVirtualMachinePathAsync(string path) =>
        ModifyAsync(obj =>
        {
            string propertyName = obj.HasProperty("DefaultExternalDataRoot")
                ? "DefaultExternalDataRoot"
                : "DefaultVirtualMachinePath";
            obj[propertyName] = path;
        });

    public static Task<ApiResponse> SetDefaultVirtualHardDiskPathAsync(string path) =>
        ModifyAsync(obj => obj["DefaultVirtualHardDiskPath"] = path);

    public static Task<ApiResponse> SetDynamicMacAddressRangeAsync(string minimum, string maximum) =>
        ModifyAsync(obj =>
        {
            obj["MinimumMacAddress"] = minimum;
            obj["MaximumMacAddress"] = maximum;
        });

    private static Task<ApiResponse> ModifyAsync(Action<System.Management.ManagementObject> modifier) =>
        WmiApi.WithObjectAsync(
            wql: SettingWql,
            modifier: modifier,
            submitMethod: "ModifyServiceSettings",
            submitParamName: "SettingData",
            wrapInArray: false,
            serviceWql: ServiceWql);
}
