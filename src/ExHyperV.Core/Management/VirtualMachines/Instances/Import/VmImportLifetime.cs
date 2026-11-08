namespace ExHyperV.Services;

// 只协调一次导入对象的使用与清理，不管理宿主或 Core 的进程生命周期。
internal sealed class VmImportLifetime
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _active;
    private Task? _cleanup;
    private bool _closed;

    internal Operation Enter(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_closed) throw new ObjectDisposedException("导入会话", "导入会话已请求释放，不能再次开始导入或准备。");
            if (_active != null) throw new InvalidOperationException("同一导入会话已有正在进行的操作。");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(this, linked);
        }
    }

    internal void RequestStop()
    {
        lock (_sync) _closed = true;
        // 不在锁内执行取消回调；异常回调也不能阻止后续等待及资源清理。
        try { _stop.Cancel(); }
        catch (AggregateException) { }
    }

    internal Task CleanupAsync(Func<Task> cleanup, Action? stopChildren = null)
    {
        TaskCompletionSource completion;
        Task idle;
        lock (_sync)
        {
            _closed = true;
            if (_cleanup is { IsCompleted: false }) return _cleanup;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _cleanup = completion.Task;
            idle = _active?.Task ?? Task.CompletedTask;
        }
        RequestStop();
        stopChildren?.Invoke();
        _ = CompleteCleanupAsync(idle, cleanup, completion);
        return completion.Task;
    }

    private static async Task CompleteCleanupAsync(Task idle, Func<Task> cleanup, TaskCompletionSource completion)
    {
        try
        {
            await idle.ConfigureAwait(false);
            await cleanup().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private void Leave()
    {
        TaskCompletionSource? active;
        lock (_sync)
        {
            active = _active;
            _active = null;
        }
        active?.TrySetResult();
    }

    internal sealed class Operation(VmImportLifetime owner, CancellationTokenSource linked) : IDisposable
    {
        private VmImportLifetime? _owner = owner;
        internal CancellationToken Token { get; } = linked.Token;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current == null) return;
            linked.Dispose();
            current.Leave();
        }
    }
}

// 批次修改开始前先保留全部子会话。失败时只释放保留，不进入任何业务步骤。
internal sealed class VmImportBatchOperation : IDisposable
{
    private readonly VmImportLifetime.Operation _batch;
    private readonly Dictionary<VmImportSession, VmImportLifetime.Operation> _sessions = [];

    internal VmImportBatchOperation(VmImportBatchSession batch, CancellationToken cancellationToken)
    {
        _batch = batch.Lifetime.Enter(cancellationToken);
        try
        {
            foreach (var session in batch.VirtualMachines)
                _sessions.Add(session, session.Lifetime.Enter(_batch.Token));
        }
        catch { Dispose(); throw; }
    }

    internal CancellationToken Token => _batch.Token;
    internal CancellationToken GetToken(VmImportSession session) => _sessions[session].Token;

    public void Dispose()
    {
        foreach (var operation in _sessions.Values) operation.Dispose();
        _batch.Dispose();
    }
}
