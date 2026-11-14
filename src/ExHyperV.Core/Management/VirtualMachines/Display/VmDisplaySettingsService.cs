using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>虚拟显示设备的分辨率策略与物理像素尺寸。</summary>
/// <param name="ResolutionType">2 为最大分辨率，3 为固定分辨率，4 为默认策略。</param>
/// <param name="Width">图像宽度，单位为像素。</param>
/// <param name="Height">图像高度，单位为像素。</param>
public sealed record VmDisplayResolution(int ResolutionType, int Width, int Height);

// 基本会话(合成显示控制器)显示设置。机制对应 PowerShell Set-VMVideo：
// 改 Msvm_SyntheticDisplayControllerSettingData 的 ResolutionType/Horizontal/VerticalResolution，
// 经 ModifyResourceSettings 提交（与删网卡/改连接同一套服务方法）。
/// <summary>读取和写入虚拟显示设备的分辨率策略。</summary>
public static class VmDisplaySettingsService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    /// <summary>判断虚拟机显示设备分辨率配置是否可修改。</summary>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <returns>虚拟机未运行时为 true。</returns>
    internal static bool CanEdit(bool isRunning) => !isRunning;

    /// <summary>评估显示配置的修改条件。</summary>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改、原因及需要的电源状态。</returns>
    public static VmModificationCondition GetModificationCondition(VmState state) => VmModificationCondition.PoweredOff(state);

    // ResolutionType：2=Maximum 3=Single(固定) 4=Default(自适应)
    /// <summary>读取虚拟显示设备的分辨率策略和像素尺寸。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功时返回策略和尺寸，失败不返回默认分辨率。</returns>
    public static async Task<ApiResponse<VmDisplayResolution>> GetResolutionAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<VmDisplayResolution>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);

        var resp = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_SyntheticDisplayControllerSettingData WHERE InstanceID LIKE 'Microsoft:{vmId:D}%'",
            ctrl => new VmDisplayResolution(Convert.ToInt32(ctrl["ResolutionType"] ?? (byte)4),
                Convert.ToInt32(ctrl["HorizontalResolution"] ?? (ushort)0), Convert.ToInt32(ctrl["VerticalResolution"] ?? (ushort)0)));
        return resp.HasData ? resp : ApiResponse<VmDisplayResolution>.Fail(resp.Success ? Properties.Resources.Error_Video_NoController : resp.Error,
            resp.Code, resp.ErrorSource, resp.Exception, resp.Success ? ApiErrorKind.NotFound : resp.ErrorKind);
    }

    // type：2/3/4；Single(3) 时按 width×height 固定，其余忽略宽高
    /// <summary>固定分辨率模式下检查宽度 200 至 7680、高度 200 至 4320 像素的范围。</summary>
    /// <param name="resolutionType">Hyper-V 分辨率类型：2 为最大分辨率，3 为固定分辨率，4 为默认策略。</param>
    /// <param name="width">固定模式下要求 200 至 7680 像素宽。</param>
    /// <param name="height">固定模式下要求 200 至 4320 像素高。</param>
    /// <returns>策略无效或固定模式尺寸超出范围时的说明；有效时为 null。</returns>
    public static string? GetResolutionError(int resolutionType, int width, int height) =>
        resolutionType is not (2 or 3 or 4) || resolutionType == 3 && (width < 200 || width > 7680 || height < 200 || height > 4320)
            ? Properties.Resources.VmAdvanced_ResolutionInvalid
            : null;

    /// <summary>在虚拟机未运行时写入分辨率策略；固定分辨率的宽高会向下取偶数。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="resolutionType">Hyper-V 分辨率类型：2 为最大分辨率，3 为固定分辨率，4 为默认策略。</param>
    /// <param name="width">固定模式下为 200 至 7680 像素宽，写入前向下取偶数。</param>
    /// <param name="height">固定模式下为 200 至 4320 像素高，写入前向下取偶数。</param>
    /// <returns>结构化写入结果；仅固定分辨率模式写入宽高。</returns>
    public static async Task<ApiResponse> SetResolutionAsync(Guid vmId, int resolutionType, int width, int height)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        if (GetResolutionError(resolutionType, width, height) is string error) return ApiResponse.Fail(error, kind: ApiErrorKind.InvalidArgument);

        var state = await VmConfigurationState.ReadAsync(vmId);
        if (!state.HasData) return ApiResponse.Fail(state.Success ? Properties.Resources.Error_Net_VmNotFound : state.Error,
            state.Code, state.ErrorSource, state.Exception, state.Success ? ApiErrorKind.NotFound : state.ErrorKind);
        var condition = GetModificationCondition(state.Data!.State);
            if (!condition.CanModify) return condition.ToResponse();

        var xmlResp = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_SyntheticDisplayControllerSettingData WHERE InstanceID LIKE 'Microsoft:{vmId:D}%'",
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
            return ApiResponse.Fail(xmlResp.Success ? Properties.Resources.Error_Video_NoController : xmlResp.Error,
                xmlResp.Code, xmlResp.ErrorSource, xmlResp.Exception, xmlResp.Success ? ApiErrorKind.NotFound : xmlResp.ErrorKind);

        var result = await WmiApi.InvokeAsync(
            ServiceWql, "ModifyResourceSettings",
            p => p["ResourceSettings"] = new string[] { xmlResp.Data! });
        return result;
    }

    internal static async Task<(bool Success, int ResolutionType, int Width, int Height)> GetResolutionAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, 0, 0, 0);
        var result = await GetResolutionAsync(identity.Data);
        return result.HasData ? (true, result.Data.ResolutionType, result.Data.Width, result.Data.Height) : (false, 0, 0, 0);
    }

    internal static async Task<(bool Success, string Message)> SetResolutionAsync(string vmName, int resolutionType, int width, int height)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var result = await SetResolutionAsync(identity.Data, resolutionType, width, height);
        return (result.Success, result.Error);
    }
}
