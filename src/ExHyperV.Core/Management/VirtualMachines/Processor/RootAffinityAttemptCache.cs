using ExHyperV.Tools;
using System.Collections.Concurrent;

namespace ExHyperV.Services;

// 只协调当前 Core 进程内的重复启动恢复，不保留句柄，也不是跨进程状态存储。
// 调用方仍须持有该 VM 的跨进程锁，覆盖检查、应用和偏好保存的整个流程。
internal sealed class RootAffinityAttemptCache
{
    private sealed record Attempt(int ProcessId, long CreationTime, ApiResponse<RootProcessAffinity> Result);
    private readonly ConcurrentDictionary<Guid, Attempt> attempts = new();

    internal async Task<ApiResponse<RootProcessAffinity>> ApplyAsync(
        Guid vmId, IReadOnlyList<int> indices, RootAffinityWorkflowOperations operations)
    {
        if (vmId == Guid.Empty || indices is null || operations is null)
            return ApiResponse<RootProcessAffinity>.Fail("虚拟机身份、处理器列表或平台操作无效。", kind: ApiErrorKind.InvalidArgument);

        RootProcessAffinity before;
        int[] selection;
        try
        {
            selection = indices.ToArray();
            var read = await operations.ReadProcess(vmId).ConfigureAwait(false);
            if (!read.HasData) return EmptyAsFailure(read, "无法确认本次应用前的虚拟机进程。");
            if (!ValidIdentity(read.Data))
                return ApiResponse<RootProcessAffinity>.Fail("虚拟机进程身份或创建时间无效。", kind: ApiErrorKind.InvalidState);
            before = read.Data;
        }
        catch (Exception error)
        {
            // 尚未尝试写入，不能把这次查询失败登记成已应用或已提交。
            return ApiResponse<RootProcessAffinity>.Fail(error.Message, error.HResult, exception: error,
                kind: error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
        }

        ApiResponse<RootProcessAffinity> result;
        try
        {
            // 明确保存不检查 ShouldRestore：调用方可再次应用，并覆盖本实例的旧记录。
            result = await operations.ApplyProcess(vmId, selection).ConfigureAwait(false);
            if (result.Success && (!result.HasData || !ValidIdentity(result.Data)))
                result = ApiResponse<RootProcessAffinity>.Fail("亲和性应用返回成功，但未提供有效的进程身份。",
                    kind: ApiErrorKind.OutcomeUnknown);
        }
        catch (Exception error)
        {
            result = ApiResponse<RootProcessAffinity>.Fail(error.Message, error.HResult, exception: error,
                kind: ApiErrorKind.OutcomeUnknown);
        }

        var identity = result.HasData ? result.Data : before;
        attempts[vmId] = new(identity.ProcessId, identity.CreationTime, result);
        return result;
    }

    internal async Task<ApiResponse<bool>> ShouldRestoreAsync(Guid vmId, RootAffinityWorkflowOperations operations)
    {
        if (vmId == Guid.Empty || operations is null)
            return ApiResponse<bool>.Fail("虚拟机身份或平台操作无效。", kind: ApiErrorKind.InvalidArgument);
        if (!attempts.TryGetValue(vmId, out var prior)) return ApiResponse<bool>.Ok(true);
        // 失败响应没有携带实际写入句柄的身份；前置读取与写入之间 VM 可能已重启。
        // 因此 OutcomeUnknown 不可仅凭看见新 PID 就自动解除，明确 Save 才能更新本进程记录。
        if (prior.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) return CopyFailure(prior.Result);

        ApiResponse<RootProcessAffinity> current;
        try
        {
            current = await operations.ReadProcess(vmId).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ApiResponse<bool>.Fail(error.Message, error.HResult, exception: error,
                kind: error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
        }
        if (current is null)
            return ApiResponse<bool>.Fail("进程查询没有返回结果。", kind: ApiErrorKind.OperationFailed);
        if (!current.HasData)
        {
            var failure = EmptyAsFailure(current, "无法确认启动恢复对应的虚拟机进程。");
            return CopyFailure(failure);
        }
        if (!ValidIdentity(current.Data))
            return ApiResponse<bool>.Fail("虚拟机进程身份或创建时间无效。", kind: ApiErrorKind.InvalidState);
        if (current.Data.ProcessId != prior.ProcessId || current.Data.CreationTime != prior.CreationTime)
        {
            // 只删除刚才读取的记录；不覆盖并发的其他实例记录。
            ((ICollection<KeyValuePair<Guid, Attempt>>)attempts).Remove(new(vmId, prior));
            return ApiResponse<bool>.Ok(true);
        }
        return prior.Result.Success ? ApiResponse<bool>.Ok(false) : CopyFailure(prior.Result);
    }

    private static bool ValidIdentity(RootProcessAffinity value)
        => value.ProcessId > 0 && value.CreationTime > 0;

    private static ApiResponse<RootProcessAffinity> EmptyAsFailure(
        ApiResponse<RootProcessAffinity> response, string message)
        => response.Success ? ApiResponse<RootProcessAffinity>.Fail(message, kind: ApiErrorKind.InvalidState) : response;

    private static ApiResponse<bool> CopyFailure(ApiResponse<RootProcessAffinity> response)
        => ApiResponse<bool>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
}
