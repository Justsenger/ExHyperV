using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>在操作入口唯一解析显示名称；执行阶段只使用稳定身份。</summary>
internal static class VmIdentity
{
    internal static async Task<ApiResponse<Guid>> ResolveAsync(string vmName)
    {
        if (string.IsNullOrWhiteSpace(vmName))
            return ApiResponse<Guid>.Fail("虚拟机名称不能为空。", kind: ApiErrorKind.InvalidArgument);

        var response = await WmiApi.QueryAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(vmName)}",
            obj => obj["Name"]?.ToString());
        if (!response.Success)
            return ApiResponse<Guid>.Fail(response.Error, response.Code, response.ErrorSource,
                response.Exception, response.ErrorKind);
        return SelectUnique(response.Data ?? []);
    }

    internal static ApiResponse<Guid> SelectUnique(IEnumerable<string?> identifiers)
    {
        var ids = new HashSet<Guid>();
        foreach (var value in identifiers)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
                return ApiResponse<Guid>.Fail("虚拟机查询返回了无效身份。", kind: ApiErrorKind.OperationFailed);
            ids.Add(id);
        }
        return ids.Count switch
        {
            0 => ApiResponse<Guid>.Fail("找不到指定虚拟机。", kind: ApiErrorKind.NotFound),
            1 => ApiResponse<Guid>.Ok(ids.Single()),
            _ => ApiResponse<Guid>.Fail("存在多个同名虚拟机，请使用 GUID 指定目标。", kind: ApiErrorKind.AmbiguousTarget)
        };
    }

    internal static string ComputerSystemPredicate(Guid vmId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(vmId, Guid.Empty);
        return $"Name = '{vmId:D}'";
    }

    internal static string SettingsPredicate(Guid vmId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(vmId, Guid.Empty);
        return $"VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
    }
}
