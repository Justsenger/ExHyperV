using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>修改虚拟机显示名称。</summary>
public static class VmEditService
{
    /// <summary>检查名称是否为空或全为空白。</summary>
    /// <param name="newName">待检查的名称。</param>
    /// <returns>通过检查时返回 null，否则返回错误说明。</returns>
    internal static string? GetNameError(string? newName) =>
        string.IsNullOrWhiteSpace(newName) ? Properties.Resources.VmPage_NameEmpty : null;

    /// <summary>修改虚拟机的显示名称。</summary>
    /// <param name="vmId">目标虚拟机的标识。</param>
    /// <param name="newName">新名称，不能全为空白。</param>
    /// <returns>返回重命名结果；失败时附带原因。</returns>
    /// <editentry label="重命名" category="基本信息"/>
    public static async Task<ApiResponse> RenameVmAsync(Guid vmId, string newName)
    {
        if (GetNameError(newName) is string error)
            return ApiResponse.Fail(error, kind: ApiErrorKind.InvalidArgument);
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);

        // Msvm_ComputerSystem 里 Name 字段存的是 GUID，不是显示名
        // 先找到 VM 对应的 VirtualSystemSettingData，改 ElementName，再提交
        string settingsWql = $@"SELECT * FROM Msvm_VirtualSystemSettingData
                                WHERE VirtualSystemIdentifier = '{vmId:D}'
                                AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

        var result = await WmiApi.WithObjectAsync(
            wql: settingsWql,
            modifier: obj => obj["ElementName"] = newName,
            submitMethod: "ModifySystemSettings",
            submitParamName: "SystemSettings",
            wrapInArray: false);

        return result;
    }

}
