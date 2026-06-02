using ExHyperV.Api;

namespace ExHyperV.Services;

public static class VmInputService
{
    /// <summary>
    /// 发送真正的硬件级 Ctrl+Alt+Del。
    /// </summary>
    public static async Task<bool> SendCtrlAltDelAsync(string vmId)
    {
        var result = await WmiApi.InvokeAsync(
            $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId}'",
            "TypeCtrlAltDel",
            scope: WmiScope.HyperV);
        return result.Success;
    }
}
