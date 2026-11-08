using ExHyperV.Tools;
using System.ComponentModel;

namespace ExHyperV.Services;

internal sealed record RootProcessorTopology(ushort Group, ulong ActiveMask, IReadOnlyList<int> Indices);
internal sealed record RootProcessAffinity(int ProcessId, long CreationTime, ulong Mask, ulong AllowedMask);

// 内部平台边界用于验证流程，不向调用方暴露进程句柄或任意进程操作。
internal interface IRootAffinityProcess : IDisposable
{
    int ProcessId { get; }
    long CreationTime { get; }
    ApiResponse VerifyIdentity();
    ApiResponse<RootProcessAffinity> Read();
    ApiResponse Set(ulong mask);
}

internal sealed record RootAffinityOperations(
    Func<ApiResponse<RootProcessorTopology>> GetTopology,
    Func<Guid, bool, ApiResponse<IRootAffinityProcess>> OpenProcess);

/// <summary>使用同一个已核验的 vmmem 进程句柄读取和设置单处理器组硬亲和性。</summary>
internal static class ProcessAffinityService
{
    private static readonly RootAffinityOperations WindowsOperations = new(
        WindowsRootAffinityPlatform.GetTopology, WindowsRootAffinityPlatform.OpenProcess);

    internal static ApiResponse<RootProcessorTopology> GetTopology()
        => WindowsRootAffinityPlatform.GetTopology();

    internal static ApiResponse<RootProcessAffinity> Read(Guid vmId)
        => Read(vmId, WindowsOperations);

    internal static ApiResponse<RootProcessAffinity> Apply(Guid vmId, IReadOnlyList<int> indices)
        => Apply(vmId, indices, WindowsOperations);

    internal static ApiResponse<RootProcessAffinity> Read(Guid vmId, RootAffinityOperations operations)
        => Execute(vmId, null, operations);

    internal static ApiResponse<RootProcessAffinity> Apply(
        Guid vmId, IReadOnlyList<int> indices, RootAffinityOperations operations)
    {
        if (indices is null)
            return Failure("处理器列表不能为空引用。", ApiErrorKind.InvalidArgument);
        int[] selection = indices.ToArray();
        if (selection.Any(index => index is < 0 or > 63))
            return Failure("单处理器组的位号必须在 0 至 63 之间。", ApiErrorKind.InvalidArgument);
        return Execute(vmId, selection, operations);
    }

    private static ApiResponse<RootProcessAffinity> Execute(
        Guid vmId, int[]? indices, RootAffinityOperations operations)
    {
        if (vmId == Guid.Empty || operations is null)
            return Failure("虚拟机 GUID 或平台操作无效。", ApiErrorKind.InvalidArgument);

        // vmmem 属于虚拟机账户。管理员令牌持有 SeDebugPrivilege，并不表示已启用。
        // 打开、身份核验（包括进程令牌）、读写及句柄释放必须在同一个临时范围内完成。
        return ReferenceEquals(operations, WindowsOperations)
            ? RunWindows(() => ExecuteCore(vmId, indices, operations), indices is not null)
            : ExecuteCore(vmId, indices, operations);
    }

    private static ApiResponse<RootProcessAffinity> RunWindows(
        Func<ApiResponse<RootProcessAffinity>> action, bool write)
    {
        bool entered = false;
        ApiResponse<RootProcessAffinity>? completed = null;
        try
        {
            return TemporaryPrivileges.Run(["SeDebugPrivilege"], () =>
            {
                entered = true;
                return completed = action();
            });
        }
        catch (Exception error)
        {
            if (completed is { Success: false })
            {
                // 业务结果已经明确时，收尾失败不能覆盖它的错误类别与原始错误码。
                var combined = completed.Exception is { } original
                    ? new AggregateException(original, error) : error;
                return ApiResponse<RootProcessAffinity>.Fail(
                    $"{completed.Error} 临时特权收尾失败：{error.Message}",
                    completed.Code, completed.ErrorSource, combined, completed.ErrorKind);
            }

            int code = error is Win32Exception native ? native.NativeErrorCode : error.HResult;
            var source = error is Win32Exception ? ApiErrorSource.Win32 : ApiErrorSource.None;
            var kind = write && entered && completed is null ? ApiErrorKind.OutcomeUnknown
                : completed is not null ? ApiErrorKind.OperationFailed
                : error is Win32Exception { NativeErrorCode: 5 or 1300 } || error is UnauthorizedAccessException
                    ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed;
            string message = completed is not null
                ? $"亲和性操作结果已确认，但临时特权收尾失败：{error.Message}"
                : $"亲和性临时特权操作失败：{error.Message}";
            return ApiResponse<RootProcessAffinity>.Fail(message, code, source, error, kind);
        }
    }

    private static ApiResponse<RootProcessAffinity> ExecuteCore(
        Guid vmId, int[]? indices, RootAffinityOperations operations)
    {
        bool submitted = false;
        try
        {
            var topology = operations.GetTopology();
            var validTopology = ValidateTopology(topology);
            if (!validTopology.Success) return CopyFailure(validTopology);
            var opened = operations.OpenProcess(vmId, indices is not null);
            if (!opened.HasData) return CopyFailure(opened, "未返回虚拟机的 vmmem 进程。");
            using var process = opened.Data;
            var before = ReadVerified(process, topology.Data!);
            if (!before.HasData) return before;

            ulong requested = before.Data.Mask;
            if (indices is not null)
            {
                var selection = BuildMask(indices, before.Data.AllowedMask);
                if (!selection.HasData) return CopyFailure(selection, "处理器选择没有产生有效掩码。");
                requested = selection.Data;
            }

            // WMI 定位过程中可能发生 CPU 热添加；不能继续使用过期的单组编号。
            var currentTopology = operations.GetTopology();
            validTopology = ValidateTopology(currentTopology);
            if (!validTopology.Success) return CopyFailure(validTopology);
            if (!SameTopology(topology.Data!, currentTopology.Data!))
                return Failure("读取期间宿主处理器拓扑已改变。", ApiErrorKind.Conflict);

            if (indices is not null && requested != before.Data.Mask)
            {
                var identity = process.VerifyIdentity();
                if (!identity.Success) return CopyFailure(identity);
                submitted = true;
                var changed = process.Set(requested);
                if (!changed.Success) return CopyFailure(changed);
            }

            // 包括无操作请求在内，都读取实际结果；保存偏好不能替代运行状态确认。
            var after = ReadVerified(process, topology.Data!);
            if (!after.HasData)
                return submitted ? Unknown(after, "亲和性写入后无法确认进程与实际掩码。") : after;
            var finalTopology = operations.GetTopology();
            validTopology = ValidateTopology(finalTopology);
            if (!validTopology.Success)
                return submitted ? Unknown(validTopology, "亲和性写入后无法确认宿主拓扑。") : CopyFailure(validTopology);
            if (!SameTopology(topology.Data!, finalTopology.Data!) || after.Data.AllowedMask != before.Data.AllowedMask)
                return Failure("操作期间宿主或进程允许的处理器集合已改变。", ApiErrorKind.Conflict);
            if (indices is not null && after.Data.Mask != requested)
                return Failure("读取到的实际亲和性与请求不一致，可能被其他操作改变。", ApiErrorKind.Conflict);
            return after;
        }
        catch (Exception error)
        {
            return ApiResponse<RootProcessAffinity>.Fail(error.Message, error.HResult,
                ApiErrorSource.None, error, submitted ? ApiErrorKind.OutcomeUnknown
                    : error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
        }
    }

    private static ApiResponse<RootProcessAffinity> ReadVerified(
        IRootAffinityProcess process, RootProcessorTopology topology)
    {
        var identity = process.VerifyIdentity();
        if (!identity.Success) return CopyFailure(identity);
        var result = process.Read();
        if (!result.HasData) return CopyFailure(result, "未返回进程亲和性。");
        var actual = result.Data;
        if (actual.ProcessId <= 0 || actual.CreationTime <= 0
            || actual.ProcessId != process.ProcessId || actual.CreationTime != process.CreationTime)
            return Failure("进程身份或创建时间发生变化。", ApiErrorKind.Conflict);
        if (actual.Mask == 0 || actual.AllowedMask == 0
            || (actual.AllowedMask & ~topology.ActiveMask) != 0 || (actual.Mask & ~actual.AllowedMask) != 0)
            return Failure("系统返回的进程亲和性与当前单组拓扑不一致。", ApiErrorKind.InvalidState);
        return result;
    }

    internal static ApiResponse<ulong> BuildMask(IReadOnlyList<int> indices, ulong allowedMask)
    {
        if (indices is null)
            return ApiResponse<ulong>.Fail("处理器列表不能为空引用。", kind: ApiErrorKind.InvalidArgument);
        if (allowedMask == 0)
            return ApiResponse<ulong>.Fail("系统未提供可用的处理器掩码。", kind: ApiErrorKind.InvalidState);
        if (indices.Count == 0) return ApiResponse<ulong>.Ok(allowedMask);
        ulong mask = 0;
        foreach (int index in indices)
        {
            if (index is < 0 or > 63)
                return ApiResponse<ulong>.Fail("单处理器组的位号必须在 0 至 63 之间。", kind: ApiErrorKind.InvalidArgument);
            ulong bit = 1UL << index;
            if ((allowedMask & bit) == 0)
                return ApiResponse<ulong>.Fail("所选处理器不在系统允许的集合中。", kind: ApiErrorKind.InvalidArgument);
            mask |= bit;
        }
        return ApiResponse<ulong>.Ok(mask);
    }

    internal static ApiResponse<RootProcessorTopology> ParseTopology(ReadOnlySpan<byte> bytes, int pointerSize)
        => WindowsRootAffinityPlatform.ParseTopology(bytes, pointerSize);

    private static ApiResponse ValidateTopology(ApiResponse<RootProcessorTopology> response)
    {
        if (!response.HasData)
            return ApiResponse.Fail(response.Success ? "系统没有返回处理器拓扑。" : response.Error,
                response.Code, response.ErrorSource, response.Exception,
                response.Success ? ApiErrorKind.InvalidState : response.ErrorKind);
        var topology = response.Data;
        if (topology.Group != 0)
            return ApiResponse.Fail("Root 亲和性目前仅支持宿主实际只有一个 Windows 处理器组。", kind: ApiErrorKind.Unsupported);
        if (topology.ActiveMask == 0 || topology.Indices is null
            || !topology.Indices.SequenceEqual(Enumerable.Range(0, 64).Where(index => (topology.ActiveMask & (1UL << index)) != 0)))
            return ApiResponse.Fail("处理器索引与系统活动掩码不一致。", kind: ApiErrorKind.InvalidState);
        return ApiResponse.Ok();
    }

    private static bool SameTopology(RootProcessorTopology first, RootProcessorTopology second)
        => first.Group == second.Group && first.ActiveMask == second.ActiveMask;

    private static ApiResponse<RootProcessAffinity> Failure(string message, ApiErrorKind kind)
        => ApiResponse<RootProcessAffinity>.Fail(message, kind: kind);

    private static ApiResponse<RootProcessAffinity> CopyFailure(ApiResponse response)
        => ApiResponse<RootProcessAffinity>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

    private static ApiResponse<RootProcessAffinity> CopyFailure<T>(ApiResponse<T> response, string emptyMessage)
        => ApiResponse<RootProcessAffinity>.Fail(response.Success ? emptyMessage : response.Error,
            response.Code, response.ErrorSource, response.Exception,
            response.Success ? ApiErrorKind.InvalidState : response.ErrorKind);

    private static ApiResponse<RootProcessAffinity> Unknown<T>(ApiResponse<T> response, string message)
        => ApiResponse<RootProcessAffinity>.Fail(message + response.Error, response.Code,
            response.ErrorSource, response.Exception, ApiErrorKind.OutcomeUnknown);

    private static ApiResponse<RootProcessAffinity> Unknown(ApiResponse response, string message)
        => ApiResponse<RootProcessAffinity>.Fail(message + response.Error, response.Code,
            response.ErrorSource, response.Exception, ApiErrorKind.OutcomeUnknown);
}
