using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>直接通过 Hyper-V 向虚拟机发送 Ctrl+Alt+Del。</summary>
/// <remarks>这是独立于控制台连接的合成键盘操作，适合登录或打开来宾安全界面。
/// 日常键盘、鼠标输入使用控制台连接的 IConsoleInput。</remarks>
public static class VmInputService
{
    /// <summary>向指定虚拟机发送 Ctrl+Alt+Del 组合键。</summary>
    /// <param name="vmId">目标虚拟机的 GUID。</param>
    /// <returns>按键请求的执行结果，失败时包含 Hyper-V 返回的原因。</returns>
    /// <remarks>通过 Hyper-V 直接发送按键，无需 RDP 连接或窗口焦点。</remarks>
    public static async Task<ApiResponse> SendCtrlAltDelAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        return await WmiApi.InvokeAsync(
            $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId:D}'",
            "TypeCtrlAltDel",
            scope: WmiScope.HyperV);
    }

    internal static async Task<bool> SendCtrlAltDelAsync(string vmId)
        => Guid.TryParse(vmId, out Guid id) && (await SendCtrlAltDelAsync(id)).Success;
}
