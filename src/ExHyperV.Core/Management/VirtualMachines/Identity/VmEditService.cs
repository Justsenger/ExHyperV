using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>校验和修改虚拟机显示名称。</summary>
public static class VmEditService
{
    /// <summary>检查新虚拟机名称是否为空白。</summary>
    /// <param name="newName">要设置的新显示名称；不得为空白。</param>
    /// <returns>空白名称的错误说明，其他名称返回 null；不检查重名或主机约束。</returns>
    public static string? GetNameError(string? newName) =>
        string.IsNullOrWhiteSpace(newName) ? Properties.Resources.VmPage_NameEmpty : null;

    /// <summary>按稳定 GUID 重命名虚拟机。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="newName">要设置的新显示名称；不得为空白。</param>
    /// <returns>修改结果；错误包含分类与底层诊断。</returns>
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
