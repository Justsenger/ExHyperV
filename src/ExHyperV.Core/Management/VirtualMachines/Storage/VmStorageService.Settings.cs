using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmStorageService
{
    /// <summary>设置整台虚拟机是否允许完整 SCSI 命令集；此开关不属于单个存储设备。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enabled">true 启用，false 关闭。</param>
    /// <returns>配置提交结果；属性不受支持或执行条件不满足时返回失败。</returns>
    public static Task<ApiResponse> SetFullScsiCommandSetEnabledAsync(Guid vmId, bool enabled) =>
        VmConfigurationSwitch.SetAsync(vmId, "AllowFullSCSICommandSet", enabled);
}
