using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>配置宿主的虚拟机控制台连接策略。</summary>
public static class HostConsoleService
{

    /// <summary>修改宿主允许增强会话的策略。</summary>
    /// <returns>Hyper-V 配置提交响应。</returns>
    /// <param name="enabled">是否允许增强会话；实际连接仍取决于来宾支持。</param>
    /// <remarks>当前配置见 HyperVHostSettingsService.GetAsync 返回的 EnhancedSessionModeEnabled；连接还需要来宾支持增强会话。</remarks>
    public static Task<ApiResponse> SetEnhancedSessionModeEnabledAsync(bool enabled) =>
        HyperVHostSettingsService.ModifyAsync(obj => obj["EnhancedSessionModeEnabled"] = enabled);

}
