using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 基本会话(合成显示控制器)显示设置。机制对应 PowerShell Set-VMVideo：
// 改 Msvm_SyntheticDisplayControllerSettingData 的 ResolutionType/Horizontal/VerticalResolution，
// 经 ModifyResourceSettings 提交（与删网卡/改连接同一套服务方法）。
public static class VmVideoService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    // ResolutionType：2=Maximum 3=Single(固定) 4=Default(自适应)
    public static async Task<(bool Success, int ResolutionType, int Width, int Height)> GetResolutionAsync(string vmName)
    {
        if (string.IsNullOrEmpty(vmName)) return (false, 0, 0, 0);

        var vmResp = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj["Name"]?.ToString());
        if (!vmResp.HasData) return (false, 0, 0, 0);

        var resp = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_SyntheticDisplayControllerSettingData WHERE InstanceID LIKE 'Microsoft:{vmResp.Data}%'",
            ctrl => (
                Type: Convert.ToInt32(ctrl["ResolutionType"] ?? (byte)4),
                W: Convert.ToInt32(ctrl["HorizontalResolution"] ?? (ushort)0),
                H: Convert.ToInt32(ctrl["VerticalResolution"] ?? (ushort)0)));
        return resp.HasData ? (true, resp.Data.Type, resp.Data.W, resp.Data.H) : (false, 0, 0, 0);
    }

    // type：2/3/4；Single(3) 时按 width×height 固定，其余忽略宽高
    public static async Task<(bool Success, string Message)> SetResolutionAsync(string vmName, int resolutionType, int width, int height)
    {
        if (string.IsNullOrEmpty(vmName)) return (false, "VM name is empty");

        var vmResp = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj["Name"]?.ToString());
        if (!vmResp.HasData) return (false, Properties.Resources.Error_Net_VmNotFound);

        var xmlResp = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_SyntheticDisplayControllerSettingData WHERE InstanceID LIKE 'Microsoft:{vmResp.Data}%'",
            ctrl =>
            {
                ctrl["ResolutionType"] = (byte)resolutionType;   // UInt8
                if (resolutionType == 3)
                {
                    // Set-VMVideo 要求宽高为偶数，向下取偶以免"必须是偶数"报错
                    ctrl["HorizontalResolution"] = (ushort)(width & ~1);   // UInt16
                    ctrl["VerticalResolution"] = (ushort)(height & ~1);    // UInt16
                }
                return ctrl.GetText(TextFormat.CimDtd20);
            });
        if (!xmlResp.HasData || string.IsNullOrEmpty(xmlResp.Data))
            return (false, "Synthetic display controller not found");

        var result = await WmiApi.InvokeAsync(
            ServiceWql, "ModifyResourceSettings",
            p => p["ResourceSettings"] = new string[] { xmlResp.Data! });
        return result.Success ? (true, string.Empty) : (false, result.Error);
    }
}
