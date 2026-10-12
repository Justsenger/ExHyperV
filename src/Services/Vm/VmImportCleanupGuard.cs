using System.Diagnostics;

namespace ExHyperV.Services;

/// <summary>Keep import objects and files until the native operation is known to have stopped.</summary>
internal sealed class VmImportCleanupGuard
{
    public bool IsBlocked { get; private set; }
    public string? JobPath { get; private set; }

    public async Task ObserveAsync(Func<Task<string?>> submit, Func<Task<bool>> isTerminal,
        CancellationToken cancellationToken, TimeSpan timeout, TimeSpan pollInterval)
    {
        if (IsBlocked) throw new InvalidOperationException("Import cleanup is blocked by an unconfirmed operation.");
        cancellationToken.ThrowIfCancellationRequested();
        // A failed submission response can be ambiguous too. Only an observed completion releases this guard.
        IsBlocked = true;
        JobPath = await submit();
        if (JobPath != null)
        {
            var elapsed = Stopwatch.StartNew();
            while (!await isTerminal())
            {
                if (elapsed.Elapsed >= timeout) throw new TimeoutException("ValidatePlannedSystem");
                // Cancellation does not cancel a native job. Observe it before allowing cleanup.
                await Task.Delay(pollInterval);
            }
        }
        IsBlocked = false;
        JobPath = null;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<bool> TryCleanupAsync(Func<Task<bool>> destroyAndConfirm)
    {
        if (IsBlocked) return false;
        IsBlocked = true;
        try
        {
            if (!await destroyAndConfirm()) return false;
            IsBlocked = false;
            return true;
        }
        catch { return false; }
    }
}
