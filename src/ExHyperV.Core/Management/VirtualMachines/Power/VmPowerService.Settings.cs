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
    /// <remarks>本方法只保存 EnableHibernation 许可，不发起休眠，也不证明来宾具备 S4 和完整型休眠文件。研究规则 released-domain-153:1 在 17763、28000 来宾组件中确认：即使来宾接受休眠请求，也会先应答再调用休眠 API，因此肯定应答仍不能当作已休眠。实际状态需另行观察；当前 Core 电源动作未提供休眠请求。</remarks>
    public static Task<ApiResponse> SetHibernationEnabledAsync(Guid vmId, bool enabled) =>
        VmConfigurationSwitch.SetAsync(vmId, "EnableHibernation", enabled, requiresStopped: true);
    /// <summary>根据属性可用性和当前运行状态判断休眠设置能否修改。</summary>
    /// <param name="available">当前宿主是否提供此属性。</param>
    /// <param name="isRunning">虚拟机当前是否属于活动状态。</param>
    /// <returns>属性可用且虚拟机未运行时为 true；提交时仍重新检查状态。</returns>
    public static bool CanEditHibernation(bool available, bool isRunning) => available && !isRunning;
}
