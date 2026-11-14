using ExHyperV.Tools;

namespace ExHyperV.Services;

public partial class VmQueryService
{
    private readonly SemaphoreSlim _projectionGate = new(1, 1);

    // QueryAsync already holds the lifetime lease. A later DisposeAsync must drain this
    // admitted request, rather than reject its GPU stage as if it were a new public call.
    internal async Task<Dictionary<Guid, GpuUsageData>> SampleProjectionGpuAsync(IEnumerable<Models.VmInstance> machines)
    {
        await _gpuSampleGate.WaitAsync().ConfigureAwait(false);
        try { return await SampleGpuPerformanceAsync(machines).ConfigureAwait(false); }
        finally { _gpuSampleGate.Release(); }
    }

    /// <summary>读取指定虚拟机的所选字段。</summary>
    /// <param name="request">目标虚拟机和需要读取的字段。</param>
    /// <param name="cancellationToken">取消本次查询；已经开始的本地读取会先收尾。</param>
    /// <returns>返回所选字段的值和读取状态。个别字段读取失败时，原因记录在对应字段或集合中；虚拟机身份列表读取失败时，整个请求失败。</returns>
    /// <remarks>每次调用采样一次。同一实例的查询依次执行，各数据源分别取样。</remarks>
    /// <exception cref="OperationCanceledException">请求取消；已经发起的读取已收尾。</exception>
    /// <exception cref="ObjectDisposedException">本实例已开始释放。</exception>
    public async Task<ApiResponse<VmQueryResult>> QueryAsync(VmQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        BeginCounterSample();
        try
        {
            await _projectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await new VmQueryEngine(this, request, cancellationToken).ExecuteAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { return ApiResponse<VmQueryResult>.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
            finally { _projectionGate.Release(); }
        }
        finally { EndCounterSample(); }
    }
}
