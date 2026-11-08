using ExHyperV.Tools;


namespace ExHyperV.Services;

/// <summary>宿主 Hyper-V 管理服务的配置快照。</summary>
/// <param name="EnhancedSessionModeEnabled">宿主是否允许虚拟机增强会话。true 允许；false 不允许或系统未提供此项。</param>
/// <param name="DefaultVirtualMachinePath">创建虚拟机时使用的默认配置目录，位于宿主文件系统；未取得时为空字符串。</param>
/// <param name="DefaultVirtualHardDiskPath">创建虚拟硬盘时使用的宿主默认目录；未取得时为空字符串。</param>
/// <param name="MinimumMacAddress">宿主分配动态 MAC 地址的范围下界；未取得时为空字符串。</param>
/// <param name="MaximumMacAddress">宿主分配动态 MAC 地址的范围上界；未取得时为空字符串。</param>
public sealed record HyperVHostSettings(
    bool EnhancedSessionModeEnabled,
    string DefaultVirtualMachinePath,
    string DefaultVirtualHardDiskPath,
    string MinimumMacAddress,
    string MaximumMacAddress);

/// <summary>读取宿主 Hyper-V 配置快照，修改新建虚拟机的默认配置目录。</summary>
public static class HyperVHostSettingsService
{
    private const string SettingWql = "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    /// <summary>读取宿主 Hyper-V 默认目录、增强会话及动态 MAC 地址池。</summary>
    /// <returns>配置快照；未查到配置或查询失败时为 null。</returns>
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


    /// <summary>修改新建虚拟机的宿主默认配置目录，不移动已有虚拟机。</summary>
    /// <returns>Hyper-V 配置提交响应。</returns>
    /// <param name="path">宿主上的默认目录路径。</param>
    public static Task<ApiResponse> SetDefaultVirtualMachinePathAsync(string path) =>
        ModifyAsync(obj =>
        {
            string propertyName = obj.HasProperty("DefaultExternalDataRoot")
                ? "DefaultExternalDataRoot"
                : "DefaultVirtualMachinePath";
            obj[propertyName] = path;
        });


    internal static Task<ApiResponse> ModifyAsync(Action<System.Management.ManagementObject> modifier) =>
        WmiApi.WithObjectAsync(
            wql: SettingWql,
            modifier: modifier,
            submitMethod: "ModifyServiceSettings",
            submitParamName: "SettingData",
            wrapInArray: false,
            serviceWql: ServiceWql);
}
