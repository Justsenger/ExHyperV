using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>一次宿主映像挂载的独占使用期。释放前，同一映像的其他 Core 挂载、卸载和临时使用请求等待。</summary>
/// <remarks>先结束文件访问，再用 await using 或 ReleaseAsync 释放。持有期间重复挂载或卸载同一映像会等待自己；不同映像可并行。</remarks>
public sealed class HostDiskMountLease : IAsyncDisposable
{
    private readonly TaskCompletionSource _release;
    private readonly Task<ApiResponse> _completion;

    /// <summary>取得使用权后的挂载快照，包含实际访问模式和是否由本次建立。失败结果也需释放使用期。</summary>
    public HostDiskMountResult Result { get; }

    /// <summary>清理和资源锁释放完成后的结果；尚未完成时为 null。失败时可结合 Result.ImagePath 核对剩余挂载。</summary>
    public ApiResponse? CleanupResult => _completion.IsCompletedSuccessfully ? _completion.Result : null;

    private HostDiskMountLease(HostDiskMountResult result, TaskCompletionSource release,
        Task<ApiResponse> completion)
    {
        Result = result;
        _release = release;
        _completion = completion;
    }

    /// <summary>结束使用，清理本次建立的挂载并归还使用权；原先已有的挂载保留。重复调用等待同一次清理，不重复卸载。</summary>
    /// <returns>清理与资源锁释放结果；无法确认挂载归属或卸载失败时返回错误。</returns>
    /// <remarks>调用前需等待使用此映像的任务结束。此方法不接受取消令牌，避免业务取消中断必要的收尾；失败后也会释放锁，后续调用必须重新读取实际状态。</remarks>
    public Task<ApiResponse> ReleaseAsync()
    {
        _release.TrySetResult();
        return _completion;
    }

    /// <summary>等待 ReleaseAsync 完成；适用于 await using。清理失败时抛出异常，结构化结果仍保存在 CleanupResult。</summary>
    /// <returns>清理完成后的异步操作。</returns>
    /// <exception cref="IOException">挂载清理或资源锁释放失败。</exception>
    public async ValueTask DisposeAsync()
    {
        var result = await ReleaseAsync().ConfigureAwait(false);
        if (!result.Success) throw new IOException(result.Error, result.Exception);
    }

    internal static async Task<HostDiskMountLease> AcquireAsync(
        Func<Func<Task<ApiResponse>>, Task<ApiResponse>> runLocked,
        Func<Task<HostDiskMountResult>> mount,
        Func<HostDiskMountResult, Task<ApiResponse>> cleanup)
    {
        var ready = new TaskCompletionSource<HostDiskMountResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // RunAsync 的专用线程持有原生 mutex；调用方及清理逻辑可以正常异步换线程。
        Task<ApiResponse> completion = RunAsync();
        var result = await ready.Task.ConfigureAwait(false);
        return new(result, release, completion);

        async Task<ApiResponse> RunAsync()
        {
            try
            {
                return await runLocked(async () =>
                {
                    var mounted = await mount().ConfigureAwait(false);
                    ready.TrySetResult(mounted);
                    await release.Task.ConfigureAwait(false);
                    return await cleanup(mounted).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                ready.TrySetCanceled(error.CancellationToken);
                return HostDiskService.LockFailure(error);
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                return HostDiskService.LockFailure(error);
            }
        }
    }
}
