using System.Diagnostics;

namespace ExHyperV.Services;

/// <summary>在确认原生操作结束前保留导入对象和工作文件。</summary>
internal sealed class VmImportCleanupGuard
{
    private Func<Task<bool>>? _isTerminal;
    private Func<Task>? _captureCompletedResult;
    private bool _submissionCompleted;
    private bool _terminalConfirmed;

    public bool IsBlocked { get; private set; }
    public string? JobPath { get; private set; }

    public void BlockUnknown()
    {
        IsBlocked = true;
        JobPath = null;
        _isTerminal = null;
        _captureCompletedResult = null;
        _submissionCompleted = false;
        _terminalConfirmed = false;
    }

    public async Task ObserveAsync(Func<Task<string?>> submit, Func<Task<bool>> isTerminal,
        CancellationToken cancellationToken, TimeSpan timeout, TimeSpan pollInterval,
        Func<Task>? captureCompletedResult = null)
    {
        if (IsBlocked) throw new InvalidOperationException("存在尚未确认终态的导入操作，暂不能清理或重复提交。");
        cancellationToken.ThrowIfCancellationRequested();
        // A failed submission response can be ambiguous too. Only an observed completion releases this guard.
        IsBlocked = true;
        _isTerminal = isTerminal;
        _captureCompletedResult = captureCompletedResult;
        _submissionCompleted = false;
        _terminalConfirmed = false;
        JobPath = await submit();
        _submissionCompleted = true;
        if (JobPath != null)
        {
            var elapsed = Stopwatch.StartNew();
            while (!await isTerminal())
            {
                if (elapsed.Elapsed >= timeout) throw new TimeoutException("未能在限定时间内确认导入原生任务结束。");
                // Cancellation does not cancel a native job. Observe it before allowing cleanup.
                await Task.Delay(pollInterval);
            }
        }
        _terminalConfirmed = true;
        // 取消只能在取得已结束任务的结果身份后生效，否则无法安全清理已创建的计划系统。
        if (captureCompletedResult != null) await captureCompletedResult();
        Release();
        cancellationToken.ThrowIfCancellationRequested();
    }

    // 仅重读本次提交的作业及其结果，不重复提交、不凭对象消失猜测任务已经结束。
    // 回调必须只捕获可重新打开的身份，不能依赖原调用中已释放的原生对象。
    public async Task<bool> TryResolveAsync()
    {
        if (!IsBlocked) return true;
        if (!_submissionCompleted) return false;
        try
        {
            if (!_terminalConfirmed)
            {
                if (_isTerminal == null || !await _isTerminal()) return false;
                _terminalConfirmed = true;
            }
            if (_captureCompletedResult != null) await _captureCompletedResult();
            Release();
            return true;
        }
        catch { return false; }
    }

    private void Release()
    {
        IsBlocked = false;
        JobPath = null;
        _isTerminal = null;
        _captureCompletedResult = null;
        _submissionCompleted = false;
        _terminalConfirmed = false;
    }
}
