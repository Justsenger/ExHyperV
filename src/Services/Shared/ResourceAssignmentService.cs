using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 虚拟机资源设置的统一提交通道：定位目标 VM 配置 → AddResourceSettings / RemoveResourceSettings。
/// DDA（PCIe 直通）与 GPU-PV 共用同一套流程，页面与业务服务不得各自拼装这两步 WMI 调用。
/// 调用方只负责生成资源设置 XML（CIM DTD 2.0），提交、目标配置解析与错误包装集中在这里。
/// </summary>
public static class ResourceAssignmentService
{
    private const string VsmsWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    /// <summary>
    /// 取虚拟机的 Realized 配置对象路径，即 AddResourceSettings 的 AffectedConfiguration。
    /// </summary>
    public static Task<ApiResponse<string>> GetAffectedConfigurationAsync(string vmName)
        => WmiApi.QueryFirstAsync(
            "SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE ElementName = '{WmiApi.Escape(vmName)}' " +
            "AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            obj => obj.Path.Path);

    /// <summary>
    /// 向指定虚拟机追加一条资源设置。失败时原样返回 WMI 错误，由调用方决定回滚或提示。
    /// </summary>
    public static async Task<ApiResponse> AddForVmAsync(
        string vmName,
        string settingXml,
        CancellationToken cancellationToken = default)
    {
        var configuration = await GetAffectedConfigurationAsync(vmName);
        if (!configuration.HasData)
        {
            return ApiResponse.Fail(
                configuration.Error.Length > 0
                    ? configuration.Error
                    : $"Cannot find VM setting: {vmName}",
                configuration.Code,
                configuration.ErrorSource);
        }

        return await WmiApi.InvokeAsync(
            VsmsWql,
            "AddResourceSettings",
            p =>
            {
                p["AffectedConfiguration"] = configuration.Data;
                p["ResourceSettings"] = new[] { settingXml };
            },
            WmiScope.HyperV,
            null,
            cancellationToken);
    }

    /// <summary>
    /// 按资源设置对象的 WMI 路径移除。
    /// </summary>
    public static Task<ApiResponse> RemoveAsync(
        string settingPath,
        CancellationToken cancellationToken = default)
        => WmiApi.InvokeAsync(
            VsmsWql,
            "RemoveResourceSettings",
            p => p["ResourceSettings"] = new[] { settingPath },
            WmiScope.HyperV,
            null,
            cancellationToken);

    /// <summary>
    /// 按 InstanceID 查找资源设置对象路径（删除/回读前的定位）。
    /// <paramref name="className"/> 为具体设置类，如 Msvm_GpuPartitionSettingData。
    /// </summary>
    public static Task<ApiResponse<string>> FindSettingPathAsync(string className, string instanceId)
        => WmiApi.QueryFirstAsync(
            $"SELECT * FROM {className} WHERE InstanceID = '{WmiApi.Escape(instanceId)}'",
            obj => obj.Path.Path);
}
