using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record HostCpuGroupBinding(VmCpuGroupBinding Binding, HostCpuGroupMember Member);
internal sealed record HostCpuGroupBindings(IReadOnlyList<HostCpuGroupBinding> Items, bool Complete, string? Error);

// 只查当前配置关联；历史检查点中的 CpuGroupId 不能充当活跃成员。
internal static class HostCpuGroupMemberReader
{
    private sealed record VmRow(Guid Id, string? Name, VmState State);

    internal static async Task<HostCpuGroupBindings> ReadAsync(CancellationToken token)
    {
        var before = await ReadInventoryAsync().ConfigureAwait(false);
        if (!before.HasData) return new([], false, before.Error);
        var bindings = new List<HostCpuGroupBinding>();
        var errors = new List<string>();
        foreach (var vm in before.Data)
        {
            token.ThrowIfCancellationRequested();
            var binding = await VmCpuGroupBindingStore.ReadAsync(vm.Id).ConfigureAwait(false);
            if (!binding.HasData)
            {
                errors.Add($"无法确认虚拟机 {vm.Id:D} 的当前 CPU 组绑定：{binding.Error}");
                continue;
            }
            bindings.Add(new(binding.Data, new(vm.Id, vm.Name, vm.State)));
        }
        token.ThrowIfCancellationRequested();
        var after = await ReadInventoryAsync().ConfigureAwait(false);
        if (!after.HasData) errors.Add(after.Error);
        else if (!before.Data.Select(vm => vm.Id).SequenceEqual(after.Data.Select(vm => vm.Id)))
            errors.Add("读取 CPU 组成员期间虚拟机清单发生变化。");
        return new(bindings.AsReadOnly(), errors.Count == 0, errors.Count == 0 ? null : string.Join("；", errors));
    }

    private static async Task<ApiResponse<List<VmRow>>> ReadInventoryAsync()
    {
        // 与本机 WMI 身份约定一致：宿主 Name 是计算机名，VM Name 是 GUID；不按本地化 Caption 过滤。
        var response = await WmiApi.QueryAsync(
            $"SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem WHERE Name <> '{WmiApi.Escape(Environment.MachineName)}'",
            row =>
            {
                // QueryAsync 会跳过抛异常的映射，所以必须把每行失败作为数据返回，禁止无声丢失成员。
                try
                {
                    if (!Guid.TryParse(row["Name"]?.ToString(), out Guid id) || id == Guid.Empty)
                        return ApiResponse<VmRow>.Fail("虚拟机清单包含无效 GUID。", kind: ApiErrorKind.InvalidState);
                    var state = row["EnabledState"] is ushort value ? VmMapper.MapState(value) : VmState.Unknown;
                    return ApiResponse<VmRow>.Ok(new(id, row["ElementName"]?.ToString(), state));
                }
                catch (Exception error)
                {
                    return ApiResponse<VmRow>.Fail("读取虚拟机身份失败：" + error.Message,
                        exception: error, kind: ApiErrorKind.OperationFailed);
                }
            }).ConfigureAwait(false);
        if (!response.HasData)
            return ApiResponse<List<VmRow>>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
        var rows = new List<VmRow>();
        var ids = new HashSet<Guid>();
        foreach (var row in response.Data)
        {
            if (!row.HasData) return ApiResponse<List<VmRow>>.Fail(row.Error, row.Code, row.ErrorSource, row.Exception, row.ErrorKind);
            if (!ids.Add(row.Data.Id)) return ApiResponse<List<VmRow>>.Fail("虚拟机清单包含重复 GUID。", kind: ApiErrorKind.AmbiguousTarget);
            rows.Add(row.Data);
        }
        return ApiResponse<List<VmRow>>.Ok(rows.OrderBy(vm => vm.Id).ToList());
    }
}