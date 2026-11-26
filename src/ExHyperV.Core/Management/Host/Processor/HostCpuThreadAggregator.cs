using ExHyperV.Models;

namespace ExHyperV.Services;

internal sealed record VmProcessDescriptor(
    Guid VmId,
    string Name,
    bool IsRunning,
    int? WorkerProcessId,
    IReadOnlySet<int>? AllowedLogicalProcessors,
    float? TotalVpUsagePercent);

internal sealed record LogicalProcessorCounterSample(
    int LogicalProcessorIndex,
    float TotalUsagePercent,
    float? RootVpUsagePercent,
    PerformanceSampleInfo Sample);

internal static class HostCpuThreadAggregator
{
    private const float ConsumerDisplayEpsilon = 0.001f;

    internal static HostCpuThreadReport BuildFromKernelContextSwitch(
        HyperVSchedulerType schedulerType,
        IReadOnlyList<LogicalProcessorTopologyItem> topology,
        IReadOnlyList<VmProcessDescriptor> virtualMachines,
        IReadOnlyDictionary<int, ProcessSnapshotItem> processes,
        IReadOnlyDictionary<int, int> toolhelpThreadToPid,
        EtwHarvestWindow window,
        DateTimeOffset observedAtUtc,
        Func<int, Guid?>? tokenOwnerLookup = null)
    {
        long rawTicks = window.EndQpc - window.StartQpc;
        var windowDuration = TimeSpan.FromSeconds(Math.Max(0d, (double)rawTicks / Math.Max(1L, window.QpcFrequency)));

        if (rawTicks <= 0)
        {
            return BuildStatusReport(
                schedulerType,
                HostCpuAttributionSource.KernelContextSwitch,
                topology,
                observedAtUtc,
                windowDuration,
                new PerformanceSampleInfo(PerformanceSampleState.WarmingUp, observedAtUtc, "等待内核上下文切换窗口累计时间。"),
                attributionNote: null);
        }

        if (window.EventsLost > 0 || window.RealTimeBuffersLost > 0)
        {
            string issue = $"内核事件丢失（事件 {window.EventsLost}，缓冲区 {window.RealTimeBuffersLost}）。";
            return BuildStatusReport(schedulerType, HostCpuAttributionSource.KernelContextSwitch, topology,
                observedAtUtc, windowDuration, new(PerformanceSampleState.Unavailable, observedAtUtc, issue), issue);
        }

        var vmById = virtualMachines
            .Where(vm => vm.VmId != Guid.Empty)
            .GroupBy(vm => vm.VmId)
            .ToDictionary(g => g.Key, g => g.First());

        var pidToVm = ResolveVmProcessMap(virtualMachines, processes, tokenOwnerLookup);

        var lpCount = topology.Count;
        var unknownTicksByLp = new double[lpCount];
        var hostTicksByLp = new double[lpCount];
        var vmTicksByLp = new Dictionary<Guid, double>[lpCount];
        for (int i = 0; i < lpCount; i++) vmTicksByLp[i] = new Dictionary<Guid, double>();

        void AddSlice(int lp, int? pid, long ticks)
        {
            if ((uint)lp >= (uint)lpCount || ticks <= 0 || pid == 0) return;
            if (pid is int id && pidToVm.TryGetValue(id, out Guid vmId))
                vmTicksByLp[lp][vmId] = vmTicksByLp[lp].GetValueOrDefault(vmId) + ticks;
            else if (pid is int hostPid && processes.TryGetValue(hostPid, out var process)
                && !string.IsNullOrWhiteSpace(process.ImageName) && !IsVmHostProcessImage(process.ImageName))
                hostTicksByLp[lp] += ticks;
            else
                unknownTicksByLp[lp] += ticks;
        }

        foreach (var ((lp, pid), ticks) in window.PidTicksByLp)
            AddSlice(lp, pid <= int.MaxValue ? (int)pid : null, ticks);

        foreach (var ((lp, tid), ticks) in window.UnresolvedTidTicksByLp)
        {
            if (tid == 0) continue;
            int? pid = null;
            if (window.ObservedThreadToPid.TryGetValue(tid, out uint observedPid))
                pid = observedPid <= int.MaxValue ? (int)observedPid : null;
            else if (tid <= int.MaxValue && toolhelpThreadToPid.TryGetValue((int)tid, out int ownerPid))
                pid = ownerPid;
            AddSlice(lp, pid, ticks);
        }

        var threadSnapshots = new HostCpuThreadSnapshot[lpCount];
        var sampleInfo = new PerformanceSampleInfo(PerformanceSampleState.Valid, observedAtUtc);

        for (int i = 0; i < lpCount; i++)
        {
            var topo = topology[i];
            int lp = topo.LogicalProcessorIndex;
            double activeTicks = hostTicksByLp[lp] + unknownTicksByLp[lp];
            foreach (double vmTicks in vmTicksByLp[lp].Values) activeTicks += vmTicks;

            double scale = activeTicks > rawTicks ? rawTicks / activeTicks : 1d;
            double hostPctRaw = activeTicks <= 0 ? 0d : (hostTicksByLp[lp] * scale) / rawTicks * 100d;

            var consumers = new List<HostCpuThreadConsumerUsage>(vmTicksByLp[lp].Count + 1);
            double vmSumPct = 0d;

            foreach (var pair in vmTicksByLp[lp])
            {
                double pct = (pair.Value * scale) / rawTicks * 100d;
                if (pct <= ConsumerDisplayEpsilon) continue;
                float pctFloat = ClampPercent((float)pct);
                vmSumPct += pctFloat;
                string name = vmById.TryGetValue(pair.Key, out var vmInfo) && !string.IsNullOrWhiteSpace(vmInfo.Name)
                    ? vmInfo.Name
                    : pair.Key.ToString("D");
                consumers.Add(new HostCpuThreadConsumerUsage
                {
                    VmId = pair.Key,
                    IsHost = false,
                    Name = name,
                    UsagePercent = pctFloat
                });
            }

            float hostPct = ClampPercent((float)Math.Min(hostPctRaw, Math.Max(0d, 100d - vmSumPct)));
            if (hostPct > ConsumerDisplayEpsilon)
            {
                consumers.Add(new HostCpuThreadConsumerUsage
                {
                    VmId = null,
                    IsHost = true,
                    Name = "Host",
                    UsagePercent = hostPct
                });
            }

            consumers.Sort(CompareConsumers);
            float unknownPct = ClampPercent((float)(unknownTicksByLp[lp] * scale / rawTicks * 100d));
            float totalPct = ClampPercent((float)(hostPct + vmSumPct + unknownPct));

            threadSnapshots[i] = new HostCpuThreadSnapshot
            {
                LogicalProcessorIndex = topo.LogicalProcessorIndex,
                ProcessorGroup = topo.ProcessorGroup,
                GroupProcessorIndex = topo.GroupProcessorIndex,
                CoreType = topo.CoreType,
                TotalUsagePercent = totalPct,
                HostUsagePercent = unknownPct > 0 && hostPct == 0 ? null : hostPct,
                UnattributedUsagePercent = unknownPct,
                UnattributedGuestUsagePercent = 0f,
                Consumers = Array.AsReadOnly(consumers.ToArray()),
                Sample = activeTicks == 0 && !window.IdleTicksByLp.ContainsKey(lp)
                    ? new(PerformanceSampleState.WarmingUp, observedAtUtc) : sampleInfo
            };
        }


        return new HostCpuThreadReport
        {
            SchedulerType = schedulerType,
            AttributionSource = HostCpuAttributionSource.KernelContextSwitch,
            ObservedAtUtc = observedAtUtc,
            WindowDuration = windowDuration,
            Sample = threadSnapshots.Any(lp => lp.Sample.IsValid) ? sampleInfo : new(PerformanceSampleState.WarmingUp, observedAtUtc),
            Threads = Array.AsReadOnly(threadSnapshots)
        };
    }

    internal static HostCpuThreadReport BuildFromCounters(
        HyperVSchedulerType schedulerType,
        IReadOnlyList<LogicalProcessorTopologyItem> topology,
        IReadOnlyList<LogicalProcessorCounterSample> lpSamples,
        IReadOnlyList<VmProcessDescriptor> virtualMachines,
        DateTimeOffset observedAtUtc,
        TimeSpan windowDuration,
        PerformanceSampleInfo overallStatus,
        string? attributionNote)
    {
        var sampleByLp = lpSamples.ToDictionary(item => item.LogicalProcessorIndex);
        var runningVms = virtualMachines.Where(vm => vm.IsRunning && vm.VmId != Guid.Empty).ToArray();
        var snapshots = new HostCpuThreadSnapshot[topology.Count];

        bool anyValid = false;
        bool anySharedAmbiguousGuest = false;
        bool anyMissingRootVpWithActiveVms = false;
        PerformanceSampleInfo? firstInvalidSample = null;

        for (int i = 0; i < topology.Count; i++)
        {
            var topo = topology[i];
            if (!sampleByLp.TryGetValue(topo.LogicalProcessorIndex, out var lpSample) || !lpSample.Sample.IsValid)
            {
                var lpStatus = lpSample?.Sample ?? new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observedAtUtc, "未取得该逻辑处理器的性能计数器样本。");
                firstInvalidSample ??= lpStatus;
                snapshots[i] = new HostCpuThreadSnapshot
                {
                    LogicalProcessorIndex = topo.LogicalProcessorIndex,
                    ProcessorGroup = topo.ProcessorGroup,
                    GroupProcessorIndex = topo.GroupProcessorIndex,
                    CoreType = topo.CoreType,
                    TotalUsagePercent = 0f,
                    HostUsagePercent = null,
                    UnattributedGuestUsagePercent = 0f,
                    Consumers = [],
                    Sample = lpStatus
                };
                continue;
            }

            anyValid = true;
            float totalPct = ClampPercent(lpSample.TotalUsagePercent);
            var consumers = new List<HostCpuThreadConsumerUsage>();

            if (runningVms.Length == 0)
            {
                if (totalPct > ConsumerDisplayEpsilon)
                {
                    consumers.Add(new HostCpuThreadConsumerUsage { VmId = null, IsHost = true, Name = "Host", UsagePercent = totalPct });
                }
                snapshots[i] = new HostCpuThreadSnapshot
                {
                    LogicalProcessorIndex = topo.LogicalProcessorIndex,
                    ProcessorGroup = topo.ProcessorGroup,
                    GroupProcessorIndex = topo.GroupProcessorIndex,
                    CoreType = topo.CoreType,
                    TotalUsagePercent = totalPct,
                    HostUsagePercent = totalPct,
                    UnattributedGuestUsagePercent = 0f,
                    Consumers = Array.AsReadOnly(consumers.ToArray()),
                    Sample = lpSample.Sample
                };
                continue;
            }

            var eligible = runningVms
                .Where(vm => vm.AllowedLogicalProcessors is null || vm.AllowedLogicalProcessors.Contains(topo.LogicalProcessorIndex))
                .ToArray();

            if (eligible.Length == 0)
            {
                if (totalPct > ConsumerDisplayEpsilon)
                {
                    consumers.Add(new HostCpuThreadConsumerUsage { VmId = null, IsHost = true, Name = "Host", UsagePercent = totalPct });
                }
                snapshots[i] = new HostCpuThreadSnapshot
                {
                    LogicalProcessorIndex = topo.LogicalProcessorIndex,
                    ProcessorGroup = topo.ProcessorGroup,
                    GroupProcessorIndex = topo.GroupProcessorIndex,
                    CoreType = topo.CoreType,
                    TotalUsagePercent = totalPct,
                    HostUsagePercent = totalPct,
                    UnattributedGuestUsagePercent = 0f,
                    Consumers = Array.AsReadOnly(consumers.ToArray()),
                    Sample = lpSample.Sample
                };
                continue;
            }

            if (lpSample.RootVpUsagePercent is not float rawRoot)
            {
                anyMissingRootVpWithActiveVms = true;
                snapshots[i] = new HostCpuThreadSnapshot
                {
                    LogicalProcessorIndex = topo.LogicalProcessorIndex,
                    ProcessorGroup = topo.ProcessorGroup,
                    GroupProcessorIndex = topo.GroupProcessorIndex,
                    CoreType = topo.CoreType,
                    TotalUsagePercent = totalPct,
                    HostUsagePercent = null,
                    UnattributedUsagePercent = totalPct,
                    Consumers = [],
                    Sample = lpSample.Sample
                };
                continue;
            }

            float clampedHost = ClampPercent(Math.Min(rawRoot, totalPct));
            float guestResidual = ClampPercent(Math.Max(0f, totalPct - clampedHost));
            float unattributedGuest = 0f;

            if (guestResidual > ConsumerDisplayEpsilon)
            {
                if (eligible.Length == 1)
                {
                    var onlyVm = eligible[0];
                    consumers.Add(new HostCpuThreadConsumerUsage
                    {
                        VmId = onlyVm.VmId,
                        IsHost = false,
                        Name = string.IsNullOrWhiteSpace(onlyVm.Name) ? onlyVm.VmId.ToString("D") : onlyVm.Name,
                        UsagePercent = guestResidual
                    });
                }
                else
                {
                    unattributedGuest = guestResidual;
                    anySharedAmbiguousGuest = true;
                }
            }

            if (clampedHost > ConsumerDisplayEpsilon)
            {
                consumers.Add(new HostCpuThreadConsumerUsage
                {
                    VmId = null,
                    IsHost = true,
                    Name = "Host",
                    UsagePercent = clampedHost
                });
            }

            consumers.Sort(CompareConsumers);
            snapshots[i] = new HostCpuThreadSnapshot
            {
                LogicalProcessorIndex = topo.LogicalProcessorIndex,
                ProcessorGroup = topo.ProcessorGroup,
                GroupProcessorIndex = topo.GroupProcessorIndex,
                CoreType = topo.CoreType,
                TotalUsagePercent = totalPct,
                HostUsagePercent = clampedHost,
                UnattributedGuestUsagePercent = unattributedGuest,
                Consumers = Array.AsReadOnly(consumers.ToArray()),
                Sample = lpSample.Sample
            };
        }

        string? finalNote = attributionNote;
        if (anyMissingRootVpWithActiveVms)
        {
            const string missingRootNote = "宿主缺少 Root Virtual Processor 性能计数器，存在运行中虚拟机时无法分离宿主机与来宾占用，已计入未归属占比。";
            finalNote = string.IsNullOrWhiteSpace(finalNote) ? missingRootNote : $"{finalNote} {missingRootNote}";
        }
        else if (anySharedAmbiguousGuest)
        {
            string sharedNote = schedulerType is HyperVSchedulerType.Classic or HyperVSchedulerType.Core
                ? "Classic/Core 调度器下多台虚拟机共享相同逻辑处理器，性能计数器不包含单核级 vCPU 归属拆分，共享来宾时间保留在未细分来宾占比。"
                : "性能计数器回退模式下多台虚拟机并发运行，无法按逻辑处理器拆分各虚拟机占比，共享来宾时间保留在未细分来宾占比。";
            finalNote = string.IsNullOrWhiteSpace(finalNote) ? sharedNote : $"{finalNote} {sharedNote}";
        }

        var reportSample = anyValid
            ? (overallStatus.IsValid ? overallStatus : new PerformanceSampleInfo(PerformanceSampleState.Valid, observedAtUtc))
            : (firstInvalidSample ?? (overallStatus.IsValid
                ? new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observedAtUtc, "未取得有效的逻辑处理器性能计数器样本。")
                : overallStatus));

        var source = anyValid || reportSample.State == PerformanceSampleState.WarmingUp
            ? HostCpuAttributionSource.PerformanceCounter
            : HostCpuAttributionSource.Unavailable;

        return new HostCpuThreadReport
        {
            SchedulerType = schedulerType,
            AttributionSource = source,
            ObservedAtUtc = observedAtUtc,
            WindowDuration = windowDuration,
            AttributionNote = finalNote,
            Sample = reportSample,
            Threads = Array.AsReadOnly(snapshots)
        };
    }

    internal static HostCpuThreadReport BuildStatusReport(
        HyperVSchedulerType schedulerType,
        HostCpuAttributionSource source,
        IReadOnlyList<LogicalProcessorTopologyItem> topology,
        DateTimeOffset observedAtUtc,
        TimeSpan windowDuration,
        PerformanceSampleInfo status,
        string? attributionNote)
    {
        var snapshots = topology.Select(topo => new HostCpuThreadSnapshot
        {
            LogicalProcessorIndex = topo.LogicalProcessorIndex,
            ProcessorGroup = topo.ProcessorGroup,
            GroupProcessorIndex = topo.GroupProcessorIndex,
            CoreType = topo.CoreType,
            TotalUsagePercent = 0f,
            HostUsagePercent = null,
            UnattributedGuestUsagePercent = 0f,
            Consumers = [],
            Sample = status
        }).ToArray();

        return new HostCpuThreadReport
        {
            SchedulerType = schedulerType,
            AttributionSource = source,
            ObservedAtUtc = observedAtUtc,
            WindowDuration = windowDuration,
            AttributionNote = attributionNote,
            Sample = status,
            Threads = Array.AsReadOnly(snapshots)
        };
    }

    internal static Dictionary<int, Guid> ResolveVmProcessMap(
        IReadOnlyList<VmProcessDescriptor> virtualMachines,
        IReadOnlyDictionary<int, ProcessSnapshotItem> processes,
        Func<int, Guid?>? tokenOwnerLookup)
    {
        var map = new Dictionary<int, Guid>();
        var vmwpToVm = new Dictionary<int, Guid>();
        var activeVmIds = new HashSet<Guid>();

        foreach (var vm in virtualMachines)
        {
            if (vm.VmId == Guid.Empty) continue;
            activeVmIds.Add(vm.VmId);
            if (vm.WorkerProcessId is int pid && pid > 0)
            {
                map[pid] = vm.VmId;
                vmwpToVm[pid] = vm.VmId;
            }
        }

        // Fast parent-child match: Root scheduler vmmem processes are spawned by vmwp.exe.
        foreach (var proc in processes.Values)
        {
            if (proc.ProcessId <= 4) continue;
            if (IsVmHostProcessImage(proc.ImageName) && vmwpToVm.TryGetValue(proc.ParentProcessId, out Guid parentVmId))
            {
                map[proc.ProcessId] = parentVmId;
            }
        }

        // Token SID fallback for any unmatched vmmem*/vmwp* processes (e.g., race right as WMI updates).
        if (tokenOwnerLookup != null)
        {
            foreach (var proc in processes.Values)
            {
                if (proc.ProcessId <= 4 || map.ContainsKey(proc.ProcessId)) continue;
                if (!IsVmHostProcessImage(proc.ImageName)) continue;
                if (tokenOwnerLookup(proc.ProcessId) is Guid ownerVm && ownerVm != Guid.Empty && activeVmIds.Contains(ownerVm))
                {
                    map[proc.ProcessId] = ownerVm;
                }
            }
        }

        return map;
    }

    private static bool IsVmHostProcessImage(string? imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName)) return false;
        return imageName.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase)
            || imageName.StartsWith("vmwp", StringComparison.OrdinalIgnoreCase);
    }

    private static float ClampPercent(float value)
    {
        if (!float.IsFinite(value) || value <= 0f) return 0f;
        return value >= 100f ? 100f : value;
    }

    private static int CompareConsumers(HostCpuThreadConsumerUsage left, HostCpuThreadConsumerUsage right)
    {
        if (left.IsHost != right.IsHost) return left.IsHost ? 1 : -1;
        int usageOrder = right.UsagePercent.CompareTo(left.UsagePercent);
        if (usageOrder != 0) return usageOrder;
        int nameOrder = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (nameOrder != 0) return nameOrder;
        return Nullable.Compare(left.VmId, right.VmId);
    }
}
