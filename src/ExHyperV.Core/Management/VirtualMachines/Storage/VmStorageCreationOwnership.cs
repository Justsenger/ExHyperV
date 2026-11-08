using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record VmCreatedStorageResource(
    string InstanceId, string Path, int ResourceType, string Parent,
    string ResourceSubType = "", string AddressOnParent = "", string[]? HostResources = null);

// 仅登记本次原生调用明确返回、且属于目标 VM 的资源；清单用于核验，不能用于推断新增归属。
internal sealed class VmStorageCreationOwnership
{
    private readonly List<VmCreatedStorageResource> resources = [];
    internal bool IsUncertain { get; set; }
    internal string? VhdPath { get; set; }

    internal VmCreatedStorageResource? RecordCreation(
        WmiCreationResult attempt, IReadOnlyList<VmCreatedStorageResource>? inventory, int resourceType)
    {
        IsUncertain |= attempt.OutcomeUnknown;
        if (attempt.CreatedPaths.Length == 0 && !attempt.Response.Success) return null;

        var candidates = inventory?.Where(item => item.ResourceType == resourceType &&
            !string.IsNullOrWhiteSpace(item.InstanceId) &&
            attempt.CreatedPaths.Any(path => SamePath(path, item.Path))).ToArray() ?? [];
        if (attempt.CreatedPaths.Length != 1 || candidates.Length != 1)
        {
            IsUncertain = true;
            return null;
        }

        var created = candidates[0];
        resources.Add(created);
        return created;
    }

    internal async Task<ApiResponse> RollbackAsync(
        Func<Task<ApiResponse<List<VmCreatedStorageResource>>>> readCurrent,
        Func<string, Task<ApiResponse>> remove,
        Func<string, Task> deleteVhd)
    {
        if (IsUncertain)
            return ApiResponse.Fail("存储创建结果或资源归属尚未确认，已保留相关资源和磁盘文件。", kind: ApiErrorKind.OutcomeUnknown);

        foreach (var owned in resources.AsEnumerable().Reverse())
        {
            try
            {
                var snapshot = await readCurrent();
                if (!snapshot.Success || snapshot.Data == null)
                    return CleanupFailure(owned.InstanceId, snapshot.Error);
                var current = snapshot.Data.SingleOrDefault(item => SameIdentity(item, owned));
                if (current == null) continue; // 原对象已不在，不能按槽位寻找替代品。
                // 槽位标识可能被复用；相同 ID 的配置变化不能被当作原创建对象。
                // 这仍不是原子比较删除，无法识别外部按完全相同配置重建的 ABA。
                if (!MatchesConfiguration(current, owned))
                    return CleanupFailure(owned.InstanceId, "资源配置已变化，可能已被其他操作接管，已保留。");
                if (snapshot.Data.Any(item => SamePath(item.Parent, current.Path)))
                    return CleanupFailure(owned.InstanceId, "资源仍有其他子设备，已保留。");

                var result = await remove(current.Path);
                if (!result.Success) return result.ErrorKind == ApiErrorKind.OutcomeUnknown
                    ? result : CleanupFailure(owned.InstanceId, result.Error);
                var after = await readCurrent();
                if (!after.Success || after.Data == null || after.Data.Any(item => SameIdentity(item, owned)))
                    return CleanupFailure(owned.InstanceId, "尚未确认资源已移除。");
            }
            catch (Exception ex) { return CleanupFailure(owned.InstanceId, ex.Message); }
        }

        if (VhdPath != null)
        {
            try { await deleteVhd(VhdPath); VhdPath = null; }
            catch (Exception ex) { return CleanupFailure(VhdPath, ex.Message); }
        }
        return ApiResponse.Ok();
    }

    private static bool SameIdentity(VmCreatedStorageResource left, VmCreatedStorageResource right) =>
        left.ResourceType == right.ResourceType && string.Equals(left.InstanceId, right.InstanceId, StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesConfiguration(VmCreatedStorageResource current, VmCreatedStorageResource created) =>
        string.Equals(current.ResourceSubType, created.ResourceSubType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(current.AddressOnParent, created.AddressOnParent, StringComparison.OrdinalIgnoreCase) &&
        ((string.IsNullOrWhiteSpace(current.Parent) && string.IsNullOrWhiteSpace(created.Parent)) || SamePath(current.Parent, created.Parent)) &&
        (current.HostResources ?? []).SequenceEqual(created.HostResources ?? [], StringComparer.OrdinalIgnoreCase);

    private static ApiResponse CleanupFailure(string resource, string error) =>
        ApiResponse.Fail($"无法确认存储资源已安全清理：{resource}。{error}", kind: ApiErrorKind.CleanupRequired);

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        // 查询所得规范路径可能附带服务器与命名空间；实例键才是同一命名空间内的身份。
        try
        {
            return string.Equals(new ManagementPath(left).RelativePath, new ManagementPath(right).RelativePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (ManagementException) { return false; }
    }
}
