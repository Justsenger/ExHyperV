using ExHyperV.Models;
using ExHyperV.Services;

internal static class ConfigurationInteractionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (bool secureBoot in new[] { false, true })
        foreach (bool tpm in new[] { false, true })
        foreach (bool shielded in new[] { false, true })
        {
            var interlocks = VmSecurityService.GetInterlocks(secureBoot, tpm, shielded);
            var running = VmSecurityService.GetCapabilities(2, VmState.Running, secureBoot, tpm, shielded);
            var stopped = VmSecurityService.GetCapabilities(2, VmState.PoweredOff, secureBoot, tpm, shielded);
            check(interlocks.SecureBoot == !shielded && interlocks.Tpm == !shielded &&
                interlocks.SecureBootTemplate == (secureBoot && !shielded) &&
                interlocks.Encryption == (tpm && !shielded) && interlocks.Shielding &&
                stopped == interlocks && !running.CanEdit && !running.Shielding && !running.SecureBoot,
                $"Security interaction: VM-state parent gating preserves child interlocks across stop/start ({secureBoot}/{tpm}/{shielded})");
        }

        var context = new ObservedContext();
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext? applyContext = context;
        Task<CpuAffinityService.StartupApplyStatus> pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            pending = CpuAffinityService.ApplySavedAffinityAfterStartAsync("[1]", HyperVSchedulerType.Root,
                () => Task.FromResult(true), _ =>
                {
                    applyContext = SynchronizationContext.Current;
                    return Task.FromResult(true);
                }, (_, _) => delay.Task);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        delay.SetResult();
        var result = await pending;
        check(result == CpuAffinityService.StartupApplyStatus.ReselectionRequired && context.Posts == 0 && applyContext == context,
            "Affinity startup: legacy preferences require reselection before any process work or UI dispatch");
    }

    private sealed class ObservedContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { callback(state); }
                finally { SetSynchronizationContext(previous); }
            });
        }
    }
}
