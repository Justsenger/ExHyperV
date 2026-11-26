using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmConsoleSupportService
{
    /// <summary>设置控制台断开时是否锁定来宾。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enabled">true 启用，false 关闭。</param>
    /// <returns>配置提交结果；属性不受支持或执行条件不满足时返回失败。</returns>
    public static Task<ApiResponse> SetLockOnDisconnectAsync(Guid vmId, bool enabled) =>
        VmConfigurationSwitch.SetAsync(vmId, "LockOnDisconnect", enabled);

    /// <summary>从虚拟机配置文件（.vmcx）读取增强会话资源重定向偏好。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功返回剪贴板、声音输出与麦克风开关；尚未单独配置时三项均返回 true。</returns>
    /// <queryentry label="读取增强会话偏好" category="控制台"/>
    public static async Task<ApiResponse<VmConsoleFeatures>> GetEnhancedFeaturesAsync(Guid vmId)
    {
        var metadata = await VmMetadataService.ReadAsync(vmId).ConfigureAwait(false);
        if (!metadata.HasData)
            return ApiResponse<VmConsoleFeatures>.Fail(metadata.Error, metadata.Code, metadata.ErrorSource, metadata.Exception, metadata.ErrorKind);
        try
        {
            return ApiResponse<VmConsoleFeatures>.Ok(VmMetadataService.ParseConsoleFeatures(metadata.Data));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return ApiResponse<VmConsoleFeatures>.Fail(ex.Message, exception: ex);
        }
    }

    /// <summary>将增强会话资源重定向偏好保存到虚拟机配置文件（.vmcx）的 HostOnlyItems 中。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="clipboard">是否启用增强会话剪贴板共享。</param>
    /// <param name="audioOutput">是否启用增强会话声音输出。</param>
    /// <param name="audioInput">是否启用增强会话麦克风输入。</param>
    /// <returns>提交结果；修改将在下次连接或重连增强会话时生效。</returns>
    /// <remarks>通过 VmMetadataService 保存为 HostOnlyItems（Source=4），不暴露给来宾系统，关机和运行状态下均可修改并随虚拟机导出携带。</remarks>
    /// <editentry label="设置增强会话偏好" category="控制台"/>
    public static Task<ApiResponse> SetEnhancedFeaturesAsync(Guid vmId, bool clipboard, bool audioOutput, bool audioInput) =>
        VmMetadataService.WriteConsoleFeaturesAsync(vmId, clipboard, audioOutput, audioInput);
}
