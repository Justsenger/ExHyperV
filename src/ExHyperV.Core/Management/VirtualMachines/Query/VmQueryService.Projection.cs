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

    /// <summary>按一次请求中的独立字段组合读取虚拟机；同源读取、设备身份关联和宿主数据复用由 Core 完成。</summary>
    /// <param name="request">要返回的字段及目标 GUID；不接收任意 WQL，不包含页面预设。</param>
    /// <param name="cancellationToken">取消尚未开始的读取；已进入不可中断的本地调用会安全结束后再返回取消。</param>
    /// <returns>身份枚举失败返回失败响应；成功结果只返回请求字段，部分字段失败保留在对应字段或集合的状态中。</returns>
    /// <remarks>同一实例的组合查询串行执行，每次请求内批量 WMI 读取最多并行四个，逐虚拟机的启动顺序与亲和性读取另外限制为四个。不会启动自动刷新、启动 ARP、修改配置或读取缩略图。多个来源并非原子快照。DisposeAsync 等待已开始的组合查询；新调用在释放后被拒绝。旧 GetVmListAsync 暂时保留，尚未将下游页面自动迁移到新接口。</remarks>
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
