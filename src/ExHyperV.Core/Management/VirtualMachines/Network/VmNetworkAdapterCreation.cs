using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services;

internal sealed record VmCreatedNetworkResource(
    string Path, string InstanceId, string ClassName, string Configuration, string[] Children);

// 原生对象只在适配器内部使用。工作流仅接受本次创建收据与当前配置快照。
internal static class VmNetworkAdapterCreation
{
    internal const string PortClass = "Msvm_SyntheticEthernetPortSettingData";
    internal const string AllocationClass = "Msvm_EthernetPortAllocationSettingData";

    internal static async Task<VmNetworkAdapterCreationResult> ExecuteAsync(
        Guid vmId,
        Func<Task<WmiCreationResult>> createPort,
        Func<string, Task<WmiCreationResult>> initialize,
        Func<Task<ApiResponse<List<VmCreatedNetworkResource>>>> readCurrent,
        Func<string, Task<ApiResponse>> remove)
    {
        var owned = new List<VmCreatedNetworkResource>();
        var retained = new HashSet<Guid>();
        Guid? deviceId = null;
        var initialization = VmNetworkAdapterInitializationState.NotStarted;
        bool uncertain = false;
        bool nativeCallPending = false;

        VmNetworkAdapterCreationResult Result(ApiResponse? failure, string? cleanup = null,
            VmNetworkAdapterCreationState? state = null) => new(failure is null, deviceId,
                state ?? (uncertain ? VmNetworkAdapterCreationState.Unknown :
                    retained.Count > 0 ? VmNetworkAdapterCreationState.Present :
                    deviceId.HasValue ? VmNetworkAdapterCreationState.Removed : VmNetworkAdapterCreationState.NotCreated),
                initialization, failure is null ? Array.Empty<Guid>() : retained.Order().ToArray(),
                failure?.Error, cleanup, failure?.ErrorKind ?? ApiErrorKind.None);

        async Task<bool> RecordAsync(WmiCreationResult attempt, string expectedClass, string? expectedParent = null)
        {
            if (attempt.OutcomeUnknown || attempt.Response.ErrorKind == ApiErrorKind.OutcomeUnknown)
            {
                uncertain = true;
                return false;
            }
            if (attempt.CreatedPaths.Length == 0 && !attempt.Response.Success) return true;
            uncertain = true; // 查询或映射异常也不能把已提交的创建当成没有发生。
            var snapshot = await readCurrent();
            if (!snapshot.HasData) { uncertain = true; return false; }
            var matches = snapshot.Data.Where(item => attempt.CreatedPaths.Any(path => SamePath(path, item.Path))).ToArray();
            if (attempt.CreatedPaths.Length != 1 || matches.Length != 1 || matches[0].ClassName != expectedClass ||
                !matches[0].InstanceId.StartsWith($"Microsoft:{vmId:D}\\", StringComparison.OrdinalIgnoreCase))
            {
                uncertain = true;
                return false;
            }
            var item = matches[0];
            if (expectedParent is not null && !snapshot.Data.Any(parent => SamePath(parent.Path, expectedParent) &&
                parent.Children.Any(child => SamePath(child, item.Path))))
            {
                uncertain = true;
                return false;
            }
            owned.Add(item);
            if (expectedClass == PortClass)
            {
                var parts = item.InstanceId.Split('\\');
                if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id) || id == Guid.Empty)
                { uncertain = true; return false; }
                deviceId = id;
                retained.Add(id);
            }
            uncertain = false;
            return true;
        }

        async Task<VmNetworkAdapterCreationResult> FailAsync(ApiResponse failure)
        {
            var cleanupErrors = new List<string>();
            VmNetworkAdapterCreationResult CleanupFailure(string error)
            {
                cleanupErrors.Add(error);
                return Result(failure, string.Join(" ", cleanupErrors));
            }
            if (uncertain)
                return CleanupFailure("创建结果或资源归属尚未确认，已保留相关资源，未执行自动撤销。");
            foreach (var resource in owned.AsEnumerable().Reverse())
            {
                try
                {
                    var before = await readCurrent();
                    if (!before.HasData)
                    {
                        uncertain = true;
                        return CleanupFailure("无法核对自动撤销前的资源状态：" + before.Error);
                    }
                    var matches = before.Data.Where(item => SamePath(item.Path, resource.Path)).ToArray();
                    if (matches.Length == 0)
                    {
                        if (resource.ClassName == PortClass && deviceId.HasValue) retained.Remove(deviceId.Value);
                        continue;
                    }
                    if (matches.Length != 1 || !SameConfiguration(matches[0], resource))
                    {
                        uncertain = true;
                        return CleanupFailure("本次资源配置已变化或身份不唯一，已保留，未自动撤销。");
                    }
                    if (matches[0].Children.Length != 0)
                        return CleanupFailure("本次资源仍有未撤销的附属设置，已保留，避免连带删除其他操作的资源。");

                    var removed = await remove(resource.Path);
                    if (removed.ErrorKind == ApiErrorKind.OutcomeUnknown)
                    {
                        uncertain = true;
                        return CleanupFailure("自动撤销结果尚未确认：" + removed.Error);
                    }
                    var after = await readCurrent();
                    if (!after.HasData)
                    {
                        uncertain = true;
                        return CleanupFailure("无法确认自动撤销后的资源状态：" + after.Error);
                    }
                    if (after.Data.Any(item => SamePath(item.Path, resource.Path)))
                        return CleanupFailure("自动撤销后资源仍存在：" + (removed.Success ? "底层未完成移除。" : removed.Error));
                    if (resource.ClassName == PortClass && deviceId.HasValue) retained.Remove(deviceId.Value);
                    // 移除报告失败但重查已消失，仍保留清理错误，不假称完全成功。
                    if (!removed.Success) cleanupErrors.Add("自动撤销报告失败，但已确认目标资源不再存在：" + removed.Error);
                }
                catch (Exception ex)
                {
                    uncertain = true;
                    return CleanupFailure("自动撤销状态无法确认：" + ex.Message);
                }
            }
            return Result(failure, cleanupErrors.Count == 0 ? null : string.Join(" ", cleanupErrors));
        }

        try
        {
            nativeCallPending = true;
            var port = await createPort();
            nativeCallPending = false;
            if (!await RecordAsync(port, PortClass))
                return await FailAsync(port.Response.Success ? ApiResponse.Fail("无法确认本次新网卡的身份。", kind: ApiErrorKind.OutcomeUnknown) : port.Response);
            if (!port.Response.Success) return await FailAsync(port.Response);
            // 创建收据只证明归属；第二步提交前还需确认该网卡仍是原配置且没有被其他操作使用。
            uncertain = true;
            var beforeInitialization = await readCurrent();
            if (!beforeInitialization.HasData)
            {
                uncertain = true;
                return await FailAsync(ApiResponse.Fail("无法核对初始化前的网卡状态：" + beforeInitialization.Error, kind: ApiErrorKind.OutcomeUnknown));
            }
            var currentPorts = beforeInitialization.Data.Where(item => SamePath(item.Path, owned[0].Path)).ToArray();
            if (currentPorts.Length != 1 || !SameConfiguration(currentPorts[0], owned[0]))
            {
                uncertain = true;
                return await FailAsync(ApiResponse.Fail("初始化前网卡已不存在或配置已变化，未继续创建连接资源。", kind: ApiErrorKind.Conflict));
            }
            uncertain = false;
            if (currentPorts[0].Children.Length != 0)
                return await FailAsync(ApiResponse.Fail("初始化前网卡已存在其他附属资源，未继续创建连接资源。", kind: ApiErrorKind.Conflict));
            nativeCallPending = true;
            initialization = VmNetworkAdapterInitializationState.Unknown;
            var connection = await initialize(owned[0].Path);
            nativeCallPending = false;
            initialization = connection.OutcomeUnknown || connection.Response.ErrorKind == ApiErrorKind.OutcomeUnknown
                ? VmNetworkAdapterInitializationState.Unknown : connection.Response.Success
                ? VmNetworkAdapterInitializationState.Completed : VmNetworkAdapterInitializationState.Failed;
            if (!await RecordAsync(connection, AllocationClass, owned[0].Path))
                return await FailAsync(connection.Response.Success ? ApiResponse.Fail("连接资源已经提交，但无法确认本次资源的身份。", kind: ApiErrorKind.OutcomeUnknown) : connection.Response);
            if (!connection.Response.Success) return await FailAsync(connection.Response);
            return Result(null, state: VmNetworkAdapterCreationState.Present);
        }
        catch (Exception ex)
        {
            uncertain |= nativeCallPending;
            return await FailAsync(ApiResponse.Fail(ex.Message, exception: ex,
                kind: uncertain ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed));
        }
    }

    private static bool SameConfiguration(VmCreatedNetworkResource current, VmCreatedNetworkResource original) =>
        string.Equals(current.InstanceId, original.InstanceId, StringComparison.OrdinalIgnoreCase) &&
        current.ClassName == original.ClassName && current.Configuration == original.Configuration;

    internal static bool SamePath(string left, string right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
                string.Equals(new ManagementPath(left).RelativePath, new ManagementPath(right).RelativePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (ManagementException) { return false; }
    }
}
