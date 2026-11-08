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
}
