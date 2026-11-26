using System.Diagnostics;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>按物理逻辑处理器实时采样并归属宿主机与各运行中虚拟机的 CPU 时间占比；支持 Root 调度器 ETW 精确拆分及 Classic/Core 调度器计数器采样。</summary>
/// <remarks>
/// <para>在 <see cref="HyperVSchedulerType.Root"/> 调度器下，优先建立独立系统级 ETW 会话采集内核 <c>CSwitch</c> 上下文切换与线程生命周期，将每颗逻辑处理器的时间切片精确归属到宿主机（Root/系统）或各台运行中虚拟机（<c>vmmem</c> 与 <c>vmwp</c>）。</para>
/// <para>若当前进程缺少内核 ETW 权限（需管理员或 Performance Log Users 权限）或宿主运行在 Classic/Core 调度器下，则回退至 Hyper-V 性能计数器（Logical Processor、Root Virtual Processor 与 Virtual Processor）及 CPU 组亲和性约束；仅当某逻辑处理器仅有唯一允许运行的活跃虚拟机时才拆分该虚拟机占比，多机共享同一逻辑处理器时不臆测拆分，而将其计入未细分来宾占比。</para>
/// <para>使用完毕请调用 <see cref="DisposeAsync"/> 或 <see cref="Dispose"/> 释放 ETW 会话与性能计数器。</para>
/// </remarks>
public sealed class HostCpuThreadMonitorService : IDisposable, IAsyncDisposable
{
    private const string LogicalProcessorCategory = "Hyper-V Hypervisor Logical Processor";
    private const string RootVirtualProcessorCategory = "Hyper-V Hypervisor Root Virtual Processor";
    private const string TotalRunTimeCounter = "% Total Run Time";

    internal sealed class CounterHandle(Func<float> sample, Action release) : IDisposable
    {
        public float NextValue() => sample();
        public void Dispose() => release();
    }

    internal sealed record Backend(
        Func<HyperVSchedulerType> ReadSchedulerType,
        Func<IReadOnlyList<LogicalProcessorTopologyItem>> ReadTopology,
        Func<int, (IKernelThreadTrace? Trace, int Win32Error, string? ErrorMessage)> StartKernelTrace,
        Func<ProcessThreadSnapshot> SnapshotProcesses,
        Func<int, Guid?> TryReadVmTokenOwner,
        Func<CancellationToken, Task<IReadOnlyList<VmProcessDescriptor>?>> ReadVirtualMachines,
        Func<string, string[]> EnumerateCounterInstances,
        Func<string, string, string, CounterHandle> CreateCounter,
        Func<TimeSpan, CancellationToken, Task> Delay,
        Func<DateTimeOffset> UtcNow);

    private readonly Backend _backend;
    private readonly IReadOnlyList<LogicalProcessorTopologyItem> _topology;
    private readonly HyperVSchedulerType _schedulerType;
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _initialVmReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _maintenanceTask;

    private IKernelThreadTrace? _trace;
    private string? _etwFallbackReason;

    private readonly Dictionary<int, CounterHandle> _lpCounters = new();
    private readonly Dictionary<int, CounterHandle> _rootVpCounters = new();
    private readonly HashSet<int> _warmingLp = [];
    private readonly HashSet<int> _warmingRootVp = [];
    private readonly Dictionary<int, PerformanceSampleInfo> _lpCounterFailures = new();
    private PerformanceSampleInfo? _lpCategoryIssue;
    private bool _hasRootVpCategory;

    private IReadOnlyList<VmProcessDescriptor> _virtualMachines = [];
    private bool _vmSnapshotValid;
    private string? _vmSnapshotError;
    private ProcessThreadSnapshot _processSnapshot = new(new Dictionary<int, ProcessSnapshotItem>(), new Dictionary<int, int>());
    private DateTimeOffset? _lastCounterSampleUtc;

    private Task? _disposeTask;
    private int _stopping;

    /// <summary>创建宿主逻辑处理器线程监控实例，并启动后台虚拟机映射及 ETW／性能计数器采样器。</summary>
    public HostCpuThreadMonitorService() : this(CreateDefaultBackend())
    {
    }

    internal HostCpuThreadMonitorService(Backend backend)
    {
        _backend = backend;
        _schedulerType = backend.ReadSchedulerType();
        _topology = backend.ReadTopology();

        if (_schedulerType == HyperVSchedulerType.Root)
        {
            var (trace, win32Err, errMsg) = backend.StartKernelTrace(_topology.Count);
            if (trace != null)
            {
                _trace = trace;
                try
                {
                    _processSnapshot = backend.SnapshotProcesses();
                    _trace.SeedThreads(_processSnapshot.ThreadToProcess);
                }
                catch (Exception ex)
                {
                    CoreDiagnostics.Report("InitialThreadSeedFailed", error: ex);
                }
            }
            else
            {
                _etwFallbackReason = win32Err == 5
                    ? "当前进程缺少内核 ETW 会话权限（错误码 5），Root 调度器已降级为性能计数器模式，以管理员身份运行可启用单线程精确虚拟机拆分。"
                    : $"内核 ETW 会话启动失败（{errMsg ?? $"错误码 {win32Err}"}），已降级为性能计数器模式。";
            }
        }
        else if (_schedulerType is HyperVSchedulerType.Classic or HyperVSchedulerType.Core)
        {
            _etwFallbackReason = null;
        }
        else
        {
            _etwFallbackReason = "未能确定当前 Hyper-V 调度器模式，正在使用性能计数器读取宿主逻辑处理器占用。";
        }

        var token = _cts.Token;
        _maintenanceTask = Task.Run(() => MaintainLoopAsync(token));
    }

    private static Backend CreateDefaultBackend() => new(
        HyperVSchedulerService.GetSchedulerType,
        HostCpuEtwNative.ReadProcessorTopology,
        lpCount =>
        {
            HostCpuEtwNative.TryStartKernelThreadTrace(lpCount, out var trace, out int code, out string? msg);
            return (trace, code, msg);
        },
        HostCpuEtwNative.SnapshotProcessesAndThreads,
        HostCpuEtwNative.TryReadVirtualMachineTokenOwner,
        ReadVirtualMachinesWithAffinityAsync,
        category => PerformanceCounterCategory.Exists(category)
            ? new PerformanceCounterCategory(category).GetInstanceNames()
            : throw new NotSupportedException($"性能计数器类别不可用：{category}"),
        (category, counterName, instance) =>
        {
            var native = new PerformanceCounter(category, counterName, instance, readOnly: true);
            return new CounterHandle(native.NextValue, native.Dispose);
        },
        Task.Delay,
        () => DateTimeOffset.UtcNow);

    /// <summary>在给定采样窗口内采集一帧完整的宿主逻辑处理器占用归属采样帧。</summary>
    /// <param name="duration">采样等待窗口时长，须大于零且不超过 1 分钟。</param>
    /// <param name="cancellationToken">可取消采样初始化与窗口等待；取消后自动释放内部会话与计数器资源。</param>
    /// <returns>经过严格归一的宿主全量逻辑处理器占用与虚拟机归属报告；若基线尚未就绪则按实际状态返回。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> 小于等于零或超过 1 分钟。</exception>
    /// <exception cref="OperationCanceledException">操作已被 <paramref name="cancellationToken"/> 取消。</exception>
    public static async Task<HostCpuThreadReport> SampleAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(duration), "采样窗口时长必须大于零且不超过 1 分钟。");

        await using var monitor = new HostCpuThreadMonitorService();
        await monitor.WaitForInitialSnapshotAsync(cancellationToken).ConfigureAwait(false);
        monitor.GetThreadUsage();
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        return monitor.GetThreadUsage();
    }

    /// <summary>读取自上次调用以来的宿主物理逻辑处理器占用及宿主机／虚拟机拆分结果。</summary>
    /// <remarks>
    /// 同一实例连续定时调用（例如每 1 秒）可无间断衔接各时间窗口。首次调用在无有效时间差时可能返回 <see cref="PerformanceSampleState.WarmingUp"/>；调用方应先检查 <see cref="HostCpuThreadReport.Sample"/> 的 <see cref="PerformanceSampleInfo.IsValid"/> 再更新显示。
    /// </remarks>
    /// <returns>包含调度器模式、数据来源、全体物理逻辑处理器使用率及归属明细的报告。</returns>
    /// <exception cref="ObjectDisposedException">当前监控实例已释放或正在停止。</exception>
    public HostCpuThreadReport GetThreadUsage()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
            var now = _backend.UtcNow();

            if (_trace != null)
            {
                return ReadKernelContextSwitchUsageLocked(now);
            }

            return ReadCounterUsageLocked(now);
        }
    }

    internal Task WaitForInitialSnapshotAsync(CancellationToken cancellationToken)
        => _initialVmReady.Task.WaitAsync(cancellationToken);

    private HostCpuThreadReport ReadKernelContextSwitchUsageLocked(DateTimeOffset now)
    {
        var trace = _trace!;
        EtwHarvestWindow window;
        try { window = trace.HarvestWindow(); }
        catch (Exception error)
        {
            return HostCpuThreadAggregator.BuildStatusReport(_schedulerType, HostCpuAttributionSource.KernelContextSwitch,
                _topology, now, TimeSpan.Zero, new(PerformanceSampleState.Failed, now, error.Message), null);
        }
        long ticks = window.EndQpc - window.StartQpc;

        if (ticks <= 0 || (!_vmSnapshotValid && _vmSnapshotError == null))
        {
            return HostCpuThreadAggregator.BuildStatusReport(
                _schedulerType,
                HostCpuAttributionSource.KernelContextSwitch,
                _topology,
                now,
                TimeSpan.Zero,
                new PerformanceSampleInfo(PerformanceSampleState.WarmingUp, now, "正在等待首批内核上下文切换或虚拟机身份映射。"),
                attributionNote: null);
        }

        if (!_vmSnapshotValid)
        {
            return HostCpuThreadAggregator.BuildStatusReport(
                _schedulerType,
                HostCpuAttributionSource.KernelContextSwitch,
                _topology,
                now,
                TimeSpan.FromSeconds((double)ticks / Math.Max(1L, window.QpcFrequency)),
                new PerformanceSampleInfo(PerformanceSampleState.Failed, now, _vmSnapshotError ?? "无法查询运行中的虚拟机列表。"),
                attributionNote: null);
        }

        if (trace.ProcessTopologyDirty || HasMissingRunningWorkerMappingLocked())
        {
            try
            {
                _processSnapshot = _backend.SnapshotProcesses();
            }
            catch (Exception ex)
            {
                CoreDiagnostics.Report("ThreadSnapshotFailed", error: ex);
            }
            trace.ClearProcessTopologyDirty();
        }

        return HostCpuThreadAggregator.BuildFromKernelContextSwitch(
            _schedulerType,
            _topology,
            _virtualMachines,
            _processSnapshot.Processes,
            _processSnapshot.ThreadToProcess,
            window,
            now,
            _backend.TryReadVmTokenOwner);
    }

    private HostCpuThreadReport ReadCounterUsageLocked(DateTimeOffset now)
    {
        TimeSpan duration = _lastCounterSampleUtc is { } prev && now > prev ? now - prev : TimeSpan.Zero;
        _lastCounterSampleUtc = now;

        if (_lpCategoryIssue != null)
        {
            return HostCpuThreadAggregator.BuildStatusReport(
                _schedulerType,
                HostCpuAttributionSource.Unavailable,
                _topology,
                now,
                duration,
                _lpCategoryIssue with { ObservedAtUtc = now },
                _etwFallbackReason);
        }

        if (!_vmSnapshotValid && _vmSnapshotError == null)
        {
            // Consume any pending warming counters so the next tick transitions promptly.
            WarmCountersLocked();
            return HostCpuThreadAggregator.BuildStatusReport(
                _schedulerType,
                HostCpuAttributionSource.PerformanceCounter,
                _topology,
                now,
                duration,
                new PerformanceSampleInfo(PerformanceSampleState.WarmingUp, now, "正在建立性能计数器与虚拟机清单基线。"),
                _etwFallbackReason);
        }

        var lpSamples = new List<LogicalProcessorCounterSample>(_topology.Count);
        foreach (var topo in _topology)
        {
            int lp = topo.LogicalProcessorIndex;
            if (!_lpCounters.TryGetValue(lp, out var lpCounter))
            {
                var failure = _lpCounterFailures.TryGetValue(lp, out var recorded)
                    ? recorded with { ObservedAtUtc = now }
                    : new PerformanceSampleInfo(PerformanceSampleState.Unavailable, now, $"未找到 Hv LP {lp} 性能计数器实例。");
                lpSamples.Add(new LogicalProcessorCounterSample(lp, 0f, null, failure));
                continue;
            }

            if (_warmingLp.Remove(lp))
            {
                try { _ = lpCounter.NextValue(); } catch { }
                TrySampleRootVpLocked(lp, out _);
                lpSamples.Add(new LogicalProcessorCounterSample(lp, 0f, null, new PerformanceSampleInfo(PerformanceSampleState.WarmingUp, now)));
                continue;
            }

            try
            {
                float total = lpCounter.NextValue();
                if (!float.IsFinite(total) || total < 0f)
                    throw new InvalidDataException($"Hv LP {lp} 返回无效的占用百分比。");

                float? rootVp = TrySampleRootVpLocked(lp, out bool rootWarming);
                if (rootWarming && _virtualMachines.Any(vm => vm.IsRunning))
                {
                    lpSamples.Add(new LogicalProcessorCounterSample(lp, total, null, new PerformanceSampleInfo(PerformanceSampleState.WarmingUp, now)));
                    continue;
                }

                lpSamples.Add(new LogicalProcessorCounterSample(lp, total, rootVp, new PerformanceSampleInfo(PerformanceSampleState.Valid, now)));
            }
            catch (Exception ex)
            {
                var failure = new PerformanceSampleInfo(PerformanceSampleState.Failed, now, ex.Message);
                _lpCounterFailures[lp] = failure;
                _lpCounters.Remove(lp);
                lpCounter.Dispose();
                lpSamples.Add(new LogicalProcessorCounterSample(lp, 0f, null, failure));
            }
        }

        if (!_vmSnapshotValid)
        {
            return HostCpuThreadAggregator.BuildStatusReport(
                _schedulerType,
                HostCpuAttributionSource.PerformanceCounter,
                _topology,
                now,
                duration,
                new PerformanceSampleInfo(PerformanceSampleState.Failed, now, _vmSnapshotError ?? "无法读取虚拟机配置。"),
                _etwFallbackReason);
        }

        return HostCpuThreadAggregator.BuildFromCounters(
            _schedulerType,
            _topology,
            lpSamples,
            _virtualMachines,
            now,
            duration,
            new PerformanceSampleInfo(PerformanceSampleState.Valid, now),
            _etwFallbackReason);
    }

    private float? TrySampleRootVpLocked(int lp, out bool warming)
    {
        warming = false;
        if (!_hasRootVpCategory || !_rootVpCounters.TryGetValue(lp, out var rootCounter))
            return null;

        if (_warmingRootVp.Remove(lp))
        {
            warming = true;
            try { _ = rootCounter.NextValue(); } catch { }
            return null;
        }

        try
        {
            float val = rootCounter.NextValue();
            return float.IsFinite(val) && val >= 0f ? val : null;
        }
        catch
        {
            _rootVpCounters.Remove(lp);
            rootCounter.Dispose();
            return null;
        }
    }

    private void WarmCountersLocked()
    {
        foreach (int lp in _warmingLp.ToArray())
        {
            if (_lpCounters.TryGetValue(lp, out var c))
            {
                _warmingLp.Remove(lp);
                try { _ = c.NextValue(); } catch { }
            }
        }
        foreach (int lp in _warmingRootVp.ToArray())
        {
            if (_rootVpCounters.TryGetValue(lp, out var c))
            {
                _warmingRootVp.Remove(lp);
                try { _ = c.NextValue(); } catch { }
            }
        }
    }

    private bool HasMissingRunningWorkerMappingLocked()
    {
        foreach (var vm in _virtualMachines)
        {
            if (!vm.IsRunning || vm.WorkerProcessId is not int pid || pid <= 0) continue;
            if (!_processSnapshot.Processes.ContainsKey(pid)) return true;
        }
        return false;
    }

    private async Task MaintainLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RefreshVirtualMachinesAsync(token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;

                if (_trace == null)
                {
                    RefreshCounters();
                }
                else if (_trace.ProcessTopologyDirty || HasMissingRunningWorkerMappingSafe())
                {
                    var snapshot = _backend.SnapshotProcesses();
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _stopping) == 0)
                        {
                            _processSnapshot = snapshot;
                            _trace?.ClearProcessTopologyDirty();
                        }
                    }
                }

                _initialVmReady.TrySetResult();

                try { await _backend.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            _initialVmReady.TrySetCanceled(token);
        }
    }

    private bool HasMissingRunningWorkerMappingSafe()
    {
        lock (_gate) return HasMissingRunningWorkerMappingLocked();
    }

    private async Task RefreshVirtualMachinesAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var list = await _backend.ReadVirtualMachines(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (list == null) throw new InvalidOperationException("虚拟机清单查询返回空。");
            var snapshot = list.ToArray();
            if (_trace != null)
            {
                var procSnap = _backend.SnapshotProcesses();
                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0) return;
                    _virtualMachines = snapshot;
                    _processSnapshot = procSnap;
                    _trace?.ClearProcessTopologyDirty();
                    _vmSnapshotValid = true;
                    _vmSnapshotError = null;
                }
            }
            else
            {
                lock (_gate)
                {
                    if (Volatile.Read(ref _stopping) != 0) return;
                    _virtualMachines = snapshot;
                    _vmSnapshotValid = true;
                    _vmSnapshotError = null;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _vmSnapshotValid = false;
                _vmSnapshotError = ex.Message;
            }
        }
    }

    private void RefreshCounters()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            _lpCategoryIssue = RefreshCategoryLocked(LogicalProcessorCategory, _lpCounters, _warmingLp, _lpCounterFailures, isRootVp: false);
            var rootIssue = RefreshCategoryLocked(RootVirtualProcessorCategory, _rootVpCounters, _warmingRootVp, null, isRootVp: true);
            _hasRootVpCategory = rootIssue == null && _rootVpCounters.Count > 0;
        }
    }

    private PerformanceSampleInfo? RefreshCategoryLocked(
        string category,
        Dictionary<int, CounterHandle> store,
        HashSet<int> warming,
        Dictionary<int, PerformanceSampleInfo>? failures,
        bool isRootVp)
    {
        var now = _backend.UtcNow();
        try
        {
            var instances = _backend.EnumerateCounterInstances(category);
            var seen = new HashSet<int>();
            var regex = isRootVp ? @"^Root VP (\d+)$" : @"^(?:Hv )?LP (\d+)$";

            foreach (var instance in instances)
            {
                var match = Regex.Match(instance, regex, RegexOptions.CultureInvariant);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int lp)) continue;
                seen.Add(lp);
                if (store.ContainsKey(lp)) continue;

                CounterHandle? handle = null;
                try
                {
                    handle = _backend.CreateCounter(category, TotalRunTimeCounter, instance);
                    _ = handle.NextValue();
                    if (Volatile.Read(ref _stopping) != 0) return null;
                    store[lp] = handle;
                    warming.Add(lp);
                    failures?.Remove(lp);
                    handle = null;
                }
                catch (Exception ex)
                {
                    if (failures != null)
                        failures[lp] = new PerformanceSampleInfo(PerformanceSampleState.Failed, now, ex.Message);
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            foreach (int stale in store.Keys.Where(lp => !seen.Contains(lp)).ToArray())
            {
                if (store.Remove(stale, out var dead)) dead.Dispose();
                warming.Remove(stale);
                failures?.Remove(stale);
            }

            return null;
        }
        catch (Exception ex)
        {
            return new PerformanceSampleInfo(
                ex is NotSupportedException ? PerformanceSampleState.Unsupported : PerformanceSampleState.Failed,
                now,
                ex.Message);
        }
    }

    private static async Task<IReadOnlyList<VmProcessDescriptor>?> ReadVirtualMachinesWithAffinityAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var vmResponse = await WmiApi.QueryAsync(
            "SELECT Name, ElementName, ProcessID, EnabledState FROM Msvm_ComputerSystem",
            obj =>
            {
                string? rawName = obj["Name"]?.ToString();
                if (!Guid.TryParse(rawName, out var id) || id == Guid.Empty) return null;
                ushort state = Convert.ToUInt16(obj["EnabledState"] ?? 0);
                int? pid = obj["ProcessID"] is null ? null : checked((int)Convert.ToUInt32(obj["ProcessID"]));
                return new
                {
                    Id = id,
                    Name = obj["ElementName"]?.ToString() ?? string.Empty,
                    IsRunning = VmMapper.IsActiveState(state),
                    Pid = pid
                };
            },
            WmiScope.HyperV).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        if (!vmResponse.Success)
            throw new InvalidOperationException(vmResponse.Error, vmResponse.Exception);

        var items = vmResponse.Data?.Where(item => item != null).ToArray() ?? [];
        var allowedMap = new Dictionary<Guid, IReadOnlySet<int>>();

        var scheduler = HyperVSchedulerService.GetSchedulerType();
        if (scheduler is HyperVSchedulerType.Classic or HyperVSchedulerType.Core && items.Any(vm => vm!.IsRunning))
        {
            try
            {
                var groups = await HostCpuGroupService.GetAllAsync(token).ConfigureAwait(false);
                if (groups.HasData)
                {
                    foreach (var group in groups.Data!)
                    {
                        if (!group.MembersComplete || group.LogicalProcessors is not { Count: > 0 } logicalProcessors) continue;
                        var lps = new HashSet<int>(logicalProcessors.Select(lp => checked((int)lp)));
                        foreach (var member in group.Members)
                        {
                            allowedMap[member.VmId] = lps;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                CoreDiagnostics.Report("CpuGroupQueryFailed", error: ex);
            }
        }

        return items.Select(vm => new VmProcessDescriptor(
            vm!.Id,
            vm.Name,
            vm.IsRunning,
            vm.Pid,
            allowedMap.TryGetValue(vm.Id, out var allowed) ? allowed : null,
            TotalVpUsagePercent: null)).ToArray();
    }

    /// <summary>同步永久停止监控实例，并等待 ETW 会话、后台任务及性能计数器全部释放。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>异步永久停止监控实例，并等待 ETW 会话、后台任务及性能计数器全部释放；重复调用等待同一次释放。</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask == null)
            {
                Volatile.Write(ref _stopping, 1);
                _cts.Cancel();
                _disposeTask = Task.Run(CompleteDisposalAsync);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CompleteDisposalAsync()
    {
        try
        {
            await _maintenanceTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                var trace = Interlocked.Exchange(ref _trace, null);

                foreach (var c in _lpCounters.Values) c.Dispose();
                _lpCounters.Clear();
                foreach (var c in _rootVpCounters.Values) c.Dispose();
                _rootVpCounters.Clear();
                _warmingLp.Clear();
                _warmingRootVp.Clear();
                _lpCounterFailures.Clear();
                try { trace?.Dispose(); }
                finally { _cts.Dispose(); }
            }
        }
    }
}
