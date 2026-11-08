using ExHyperV.Services;
using ExHyperV.Tools;

internal static class CacheOwnershipChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckConnectionIdentity(check);
        CheckProcessSnapshots(check);
        CheckSwitchSnapshots(check);
        await CheckDiskMetadataAsync(check);
    }

    private static void CheckConnectionIdentity(Action<bool, string> check)
    {
        var first = WmiContext.Remote("host", "user-a", "test-secret-one");
        var second = WmiContext.Remote("host", "user-b", "test-secret-two");
        var changedPassword = WmiContext.Remote("host", "user-a", "test-secret-three");
        var entry = WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, first);
        var localEntry = WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, WmiContext.Local);
        check(ReferenceEquals(entry, WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, first)) &&
            ReferenceEquals(localEntry, WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, WmiContext.Local)),
            "WMI the same context and default local calls reuse their actual scope entries");
        check(!ReferenceEquals(entry, WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, second)) &&
            !ReferenceEquals(entry, WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, changedPassword)),
            "WMI different accounts or credential contexts on one host cannot reuse the first account's connection");
        check(!ReferenceEquals(entry, WmiConnectionCache.GetScopeEntry(WmiScope.CimV2, first)),
            "WMI separate namespaces have separate entries inside one context");
        entry.LastChecked = DateTime.UtcNow;
        WmiConnectionCache.Clear();
        var afterClear = WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, first);
        check(!ReferenceEquals(entry, afterClear) && afterClear.LastChecked == DateTime.MinValue &&
            !ReferenceEquals(localEntry, WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, WmiContext.Local)),
            "WMI clearing the cache replaces both live remote-context and local-context entries");

        var abandoned = CreateAbandonedConnectionEntry();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        check(!abandoned.Context.IsAlive && !abandoned.Entry.IsAlive,
            "WMI dropping a temporary remote context also makes its cached entries collectible");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Context, WeakReference Entry) CreateAbandonedConnectionEntry()
    {
        var context = WmiContext.Remote("temporary-host", "temporary-user", "test-secret");
        var entry = WmiConnectionCache.GetScopeEntry(WmiScope.HyperV, context);
        return (new(context), new(entry));
    }

    private static void CheckProcessSnapshots(Action<bool, string> check)
    {
        var vmA = Guid.NewGuid();
        var vmB = Guid.NewGuid();
        var first = new VmQueryService();
        var second = new VmQueryService();
        var now = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc);
        VmQueryService.GpuProcessInfo[] processes =
        [
            new(10, 1, "vmwp.exe", vmA.ToString()), new(11, 10, "vmmem", ""),
            new(20, 1, "vmwp.exe", vmB.ToString()), new(21, 20, "vmmem", ""),
            new(99, 98, "vmmem", "")
        ];
        first.PublishGpuProcessSnapshot([vmA], true, processes, now);
        second.PublishGpuProcessSnapshot([vmB], true, processes, now);
        var original = first.ProcessSnapshot;
        check(original.Processes.Count == 1 && original.Processes[vmA].SequenceEqual([10, 11]) &&
            second.ProcessSnapshot.Processes.Count == 1 && second.ProcessSnapshot.Processes[vmB].SequenceEqual([20, 21]),
            "GPU independent query objects keep their VM subsets and vmwp/vmmem associations separate");
        check(!original.NeedsRefresh([vmA, vmA], now.AddSeconds(1)) && original.NeedsRefresh([vmB], now.AddSeconds(1)),
            "GPU a changed requested VM set refreshes even inside the existing five-second cache interval");
        check(original.NeedsRefresh([vmA], now.AddSeconds(6)),
            "GPU an unchanged request keeps the original five-second refresh interval");
        first.PublishGpuProcessSnapshot([vmB], true, processes, now.AddSeconds(1));
        check(first.ProcessSnapshot.Processes.ContainsKey(vmB) && !first.ProcessSnapshot.Processes.ContainsKey(vmA) &&
            original.Processes.ContainsKey(vmA) && !original.Processes.ContainsKey(vmB),
            "GPU publishing a complete replacement cannot mutate a previously captured snapshot");
        var beforeFailure = first.ProcessSnapshot;
        check(!first.PublishGpuProcessSnapshot([vmA], false, [], now.AddSeconds(2)) &&
            ReferenceEquals(first.ProcessSnapshot, beforeFailure),
            "GPU a failed process query does not erase or mark the old snapshot as newly checked");
        first.PublishGpuProcessSnapshot([vmB], true, [], now.AddSeconds(3));
        check(first.ProcessSnapshot.Processes.Count == 0 && second.ProcessSnapshot.Processes.ContainsKey(vmB),
            "GPU successful empty enumeration replaces only that query object's snapshot");
    }

    private static void CheckSwitchSnapshots(Action<bool, string> check)
    {
        var first = new VmQueryService();
        var second = new VmQueryService();
        var original = first.UpdateSwitchNameCache(true, [new("one", "before"), new("gone", "removed")]);
        second.UpdateSwitchNameCache(true, [new("one", "other caller")]);
        var failed = first.UpdateSwitchNameCache(false, []);
        check(ReferenceEquals(original, failed) && failed["ONE"] == "before",
            "switch query failure retains the previous complete names without mixing another caller's data");
        var fresh = first.UpdateSwitchNameCache(true, [new("one", "renamed")]);
        check(fresh.Count == 1 && fresh["one"] == "renamed" && !fresh.ContainsKey("gone"),
            "successful switch refresh replaces renamed and removed entries");
        check(original.Count == 2 && original["one"] == "before",
            "a published switch snapshot remains stable while a newer refresh completes");
        check(first.UpdateSwitchNameCache(true, []).Count == 0,
            "successful empty switch inventory clears stale names");
    }

    private static async Task CheckDiskMetadataAsync(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV-CacheChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "owned.bin");
        try
        {
            File.WriteAllBytes(path, new byte[10]);
            int queries = 0;
            (long Max, string Type) Read(string _) { queries++; return (100, "Dynamic"); }
            var initial = VmQueryService.GetDiskSizes(path, Read);
            var cached = VmQueryService.GetDiskSizes(path, Read);
            check(initial == (10, 100, "Dynamic") && cached == initial && queries == 1,
                "unchanged disk file metadata reuses its successful query");

            File.WriteAllBytes(path, new byte[20]);
            var grown = VmQueryService.GetDiskSizes(path, Read);
            check(grown == (20, 100, "Dynamic") && queries == 2,
                "a growing disk reports its current size and invalidates old file metadata");

            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
            var resized = VmQueryService.GetDiskSizes(path, _ => { queries++; return (200, "Fixed"); });
            check(resized == (20, 200, "Fixed") && queries == 3,
                "same-length changed disk files refresh virtual capacity and disk type");

            File.Delete(path);
            var missing = VmQueryService.GetDiskSizes(path, _ => (0, "Unknown"));
            check(missing.Current == 0 && missing.Max == 0,
                "missing files cannot return a cached disk size");
            File.WriteAllBytes(path, new byte[30]);
            var failed = VmQueryService.GetDiskSizes(path, _ => throw new IOException("expected metadata failure"));
            var retried = VmQueryService.GetDiskSizes(path, _ => (300, "Differencing"));
            check(failed == (30, 0, "Unknown") && retried == (30, 300, "Differencing"),
                "failed metadata lookup reports unknown capacity and permits recovery on the next read");

            // A slow query for the old file must not replace metadata published for a changed file.
            File.WriteAllBytes(path, new byte[40]);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var delayed = Task.Run(() => VmQueryService.GetDiskSizes(path, _ =>
            {
                entered.SetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return (400, "Fixed");
            }));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                File.WriteAllBytes(path, new byte[50]);
                var latest = VmQueryService.GetDiskSizes(path, _ => (500, "Dynamic"));
                release.Set();
                await delayed;
                var retained = VmQueryService.GetDiskSizes(path, _ => throw new InvalidOperationException("cache should be current"));
                check(latest == (50, 500, "Dynamic") && retained == latest,
                    "a delayed metadata read cannot overwrite a newer file's cached metadata");
            }
            finally
            {
                release.Set();
                await delayed;
            }
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
