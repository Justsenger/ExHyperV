using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmPowerService
{
    /// <summary>设置是否将来宾重启请求转换为关机。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enabled">true 启用，false 关闭。</param>
    /// <returns>配置提交结果；属性不受支持或执行条件不满足时返回失败。</returns>
    public static Task<ApiResponse> SetTurnOffOnGuestRestartAsync(Guid vmId, bool enabled) =>
        VmConfigurationSwitch.SetAsync(vmId, "TurnOffOnGuestRestart", enabled);
    /// <summary>设置来宾休眠支持；执行时核对虚拟机未运行。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enabled">true 启用，false 关闭。</param>
    /// <returns>配置提交结果；属性不受支持或执行条件不满足时返回失败。</returns>
    /// <remarks>设置来宾休眠许可。Windows 来宾使用休眠需要支持 S4，并具有完整型休眠文件。</remarks>
    public static Task<ApiResponse> SetHibernationEnabledAsync(Guid vmId, bool enabled) =>
        VmConfigurationSwitch.SetAsync(vmId, "EnableHibernation", enabled, requiresStopped: true);
    /// <summary>根据属性可用性和当前运行状态判断休眠设置能否修改。</summary>
    /// <param name="available">当前宿主是否提供此属性。</param>
    /// <param name="isRunning">虚拟机当前是否属于活动状态。</param>
    /// <returns>属性可用且虚拟机未运行时为 true；提交时仍重新检查状态。</returns>
    internal static bool CanEditHibernation(bool available, bool isRunning) => available && !isRunning;

    /// <summary>评估来宾休眠开关的修改条件。</summary>
    /// <param name="available">查询是否取得此设置。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改、原因及需要的电源状态。</returns>
    public static VmModificationCondition GetHibernationModificationCondition(bool available, VmState state) => available
        ? VmModificationCondition.PoweredOff(state) : VmModificationCondition.Unsupported();
}
