using ExHyperV.Tools;

namespace ExHyperV.Services;

// 覆盖读取、联动修改和最终提交；按 VM 协调不同 Core 进程，避免整份 WMI 配置交错写回。
internal static class VmSettingsWriteLock
{
    internal static string Resource(Guid vmId) => "vm:settings:" + vmId.ToString("D");

    internal static async Task<ApiResponse> RunAsync(Guid vmId, Func<Task<ApiResponse>> action)
    {
        try { return await HostResourceLock.RunAsync(Resource(vmId), action).ConfigureAwait(false); }
        catch (CoreOperationException error) { return error.Result; }
        catch (AbandonedMutexException error)
        {
            return ApiResponse.Fail("前一次虚拟机设置操作异常结束，请重新查询实际配置后再修改。",
                exception: error, kind: ApiErrorKind.Conflict);
        }
        catch (Exception error)
        {
            var failure = ApiFailure.From(error);
            return ApiResponse.Fail(failure.Message, failure.Code, failure.Source, error, failure.Kind);
        }
    }
}
