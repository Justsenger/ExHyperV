using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 读写虚拟机级的高级行为设置。
/// 可用性只依据当前 WMI 对象是否实际包含属性，不按宿主版本号推断。
/// </summary>
public static class VmAdvancedBehaviorService
{
    private const string RealizedType = "Microsoft:Hyper-V:System:Realized";

    public static Task<ApiResponse<VmAdvancedBehaviorSettings>> GetSettingsAsync(string vmName)
    {
        string escapedName = WmiApi.Escape(vmName);
        return WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE ElementName = '{escapedName}' AND VirtualSystemType = '{RealizedType}'",
            obj => new VmAdvancedBehaviorSettings
            {
                AllowFullScsiCommandSetAvailable = obj.HasProperty("AllowFullSCSICommandSet"),
                AllowFullScsiCommandSet = obj.TryGet<bool>("AllowFullSCSICommandSet") ?? false,
                LockOnDisconnectAvailable = obj.HasProperty("LockOnDisconnect"),
                LockOnDisconnect = obj.TryGet<bool>("LockOnDisconnect") ?? false,
                TurnOffOnGuestRestartAvailable = obj.HasProperty("TurnOffOnGuestRestart"),
                TurnOffOnGuestRestart = obj.TryGet<bool>("TurnOffOnGuestRestart") ?? false,
                EnableHibernationAvailable = obj.HasProperty("EnableHibernation"),
                EnableHibernation = obj.TryGet<bool>("EnableHibernation") ?? false,
            });
    }

    public static Task<ApiResponse> SetSettingAsync(
        string vmName, VmAdvancedBehavior behavior, bool enabled)
    {
        string propertyName = behavior switch
        {
            VmAdvancedBehavior.AllowFullScsiCommandSet => "AllowFullSCSICommandSet",
            VmAdvancedBehavior.LockOnDisconnect => "LockOnDisconnect",
            VmAdvancedBehavior.TurnOffOnGuestRestart => "TurnOffOnGuestRestart",
            VmAdvancedBehavior.EnableHibernation => "EnableHibernation",
            _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
        };

        string escapedName = WmiApi.Escape(vmName);
        return WmiApi.WithObjectAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE ElementName = '{escapedName}' AND VirtualSystemType = '{RealizedType}'",
            obj =>
            {
                if (!obj.HasProperty(propertyName))
                    throw new InvalidOperationException(Properties.Resources.Error_VerNotSupport);

                obj[propertyName] = enabled;
            });
    }
}
