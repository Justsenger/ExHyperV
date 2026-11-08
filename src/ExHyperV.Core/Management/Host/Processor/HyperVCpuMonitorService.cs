using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    /// <summary>维护宿主及虚拟机 CPU 性能计数器；每个实例拥有自己的后台维护任务和释放生命周期。</summary>
    /// <remarks>
    /// <para>创建 HyperVCpuMonitorService 实例即启动后台计数器维护。按需要调用 <see cref="HyperVCpuMonitorService.GetCpuUsage"/> 采样；结果用 IsHost 区分宿主，用 VmId 区分虚拟机。宿主行对应逻辑处理器，虚拟机行对应虚拟处理器。</para>
    /// <para>首次样本可能处于预热阶段；检查每项 Sample.IsValid 后再更新显示或参与计算。虚拟机身份读取情况可通过 <see cref="HyperVCpuMonitorService.VirtualMachineIdentitySample"/> 查看。</para>
    /// <para>用完调用 <see cref="HyperVCpuMonitorService.DisposeAsync"/>，或用 await using 管理实例。释放会停止后台维护并释放计数器，之后不能继续采样。</para>
    /// </remarks>
    public class HyperVCpuMonitorService : IDisposable, IAsyncDisposable
    {
        private static readonly Dictionary<int, CpuCoreType> _coreTypeCache = new Dictionary<int, CpuCoreType>();
        private static bool _isHybrid = false;

        static HyperVCpuMonitorService()
        {
            InitializeCoreTypes();
        }

        /// <summary>读取首次探测宿主拓扑时缓存的核心性能分类。</summary>
        /// <returns>存在不同 EfficiencyClass 时，最高类别为 Performance，其余为 Efficient；同类核心、编号未找到或探测失败时为 Unknown。</returns>
        /// <param name="coreId">Windows 处理器组编号乘以 64 后加组内位号；不是 HCS LpIndex 或虚拟 CPU 编号。</param>
        /// <remarks>根据 Windows EfficiencyClass 分为性能和能效两类；首次探测结果在进程内缓存。</remarks>
        public static CpuCoreType GetCoreType(int coreId)
        {
            if (!_isHybrid) return CpuCoreType.Unknown;
            return _coreTypeCache.TryGetValue(coreId, out var type) ? type : CpuCoreType.Unknown;
        }

        private static void InitializeCoreTypes()
        {
            try
            {
                uint returnLength = 0;
                GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref returnLength);
                if (returnLength == 0) return;
                IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
                try
                {
                    if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref returnLength))
                    {
                        var ptr = buffer;
                        long offset = 0;
                        byte maxClass = 0;
                        byte minClass = 255;
                        var tempInfo = new List<(int Id, byte Class)>();
                        while (offset < returnLength)
                        {
                            var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(ptr);
                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                byte efficiencyClass = info.Processor.EfficiencyClass;
                                if (efficiencyClass > maxClass) maxClass = efficiencyClass;
                                if (efficiencyClass < minClass) minClass = efficiencyClass;
                                for (int i = 0; i < info.Processor.GroupCount; i++)
                                {
                                    IntPtr groupMaskPtr = ptr + (int)Marshal.OffsetOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>("Processor")
                                                          + (int)Marshal.OffsetOf<PROCESSOR_RELATIONSHIP>("GroupMask")
                                                          + i * Marshal.SizeOf<GROUP_AFFINITY>();
                                    var groupInfo = Marshal.PtrToStructure<GROUP_AFFINITY>(groupMaskPtr);
                                    ulong mask = (ulong)groupInfo.Mask;
                                    for (int bit = 0; bit < 64; bit++)
                                    {
                                        if ((mask & (1UL << bit)) != 0)
                                        {
                                            tempInfo.Add((bit + groupInfo.Group * 64, efficiencyClass));
                                        }
                                    }
                                }
                            }
                            offset += info.Size;
                            ptr = IntPtr.Add(ptr, (int)info.Size);
                        }
                        if (maxClass > minClass)
                        {
                            _isHybrid = true;
                            foreach (var item in tempInfo)
                            {
                                _coreTypeCache[item.Id] = (item.Class == maxClass) ? CpuCoreType.Performance : CpuCoreType.Efficient;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { _isHybrid = false; }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType, IntPtr Buffer, ref uint ReturnedLength);
        private enum LOGICAL_PROCESSOR_RELATIONSHIP { RelationProcessorCore = 0 }
        [StructLayout(LayoutKind.Sequential)] private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX { public LOGICAL_PROCESSOR_RELATIONSHIP Relationship; public uint Size; public PROCESSOR_RELATIONSHIP Processor; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESSOR_RELATIONSHIP { public byte Flags; public byte EfficiencyClass; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Reserved; public ushort GroupCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public GROUP_AFFINITY[] GroupMask; }
        [StructLayout(LayoutKind.Sequential)] private struct GROUP_AFFINITY { public UIntPtr Mask; public ushort Group; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public ushort[] Reserved; }

        internal sealed class Counter(Func<float> sample, Action release) : IDisposable
        {
            public float NextValue() => sample();
            public void Dispose() => release();
        }

        internal sealed record VmCpuInfo(Guid Id, string Name, int CoreCount, bool? IsRunning = true);
        internal sealed record Backend(
            Func<string, string[]> EnumerateInstances,
            Func<string, string, Counter> CreateCounter,
            Func<CancellationToken, Task<IReadOnlyList<VmCpuInfo>?>> ReadVirtualMachines,
            Func<TimeSpan, CancellationToken, Task> Delay);

        private readonly Dictionary<string, Counter> _counters = new();
        private IReadOnlyList<VmCpuInfo> _virtualMachines = [];
        private PerformanceSampleInfo _virtualMachineIdentitySample = new(PerformanceSampleState.NotSampled, default);
        private bool _vmSnapshotValid;
        private string? _vmSnapshotError;
        private readonly HashSet<string> _warmingCounters = [];
        private readonly Dictionary<string, PerformanceSampleInfo> _counterFailures = [];
        private PerformanceSampleInfo? _hostCounterIssue;
        private PerformanceSampleInfo? _vmCounterIssue;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _counterGate = new();
        private readonly object _disposeGate = new();
        private readonly Backend _backend;
        private readonly Task _maintenanceTask;
        private Task? _disposeTask;
        private int _stopping;

        /// <summary>创建 CPU 采样器并立即启动后台计数器维护；使用结束后应释放实例。</summary>
        public HyperVCpuMonitorService() : this(new Backend(
            category => PerformanceCounterCategory.Exists(category)
                ? new PerformanceCounterCategory(category).GetInstanceNames()
                : throw new NotSupportedException($"性能计数器类别不可用：{category}"),
            (category, instance) =>
            {
                var native = new PerformanceCounter(category, "% Total Run Time", instance);
                return new Counter(native.NextValue, native.Dispose);
            },
            ReadVirtualMachinesAsync,
            Task.Delay))
        {
        }

        internal HyperVCpuMonitorService(Backend backend)
        {
            _backend = backend;
            var token = _cts.Token;
            _maintenanceTask = Task.Run(() => MaintainCountersLoop(token));
        }

        /// <summary>永久停止本实例并同步等待计数器及后台任务释放。</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        /// <summary>永久停止本实例并异步等待计数器及后台任务释放；重复调用等待同一次清理。</summary>
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
            try { await _maintenanceTask.ConfigureAwait(false); }
            finally
            {
                lock (_counterGate)
                {
                    foreach (var counter in _counters.Values) ReleaseCounter(counter);
                    _counters.Clear();
                    _virtualMachines = [];
                    _warmingCounters.Clear();
                    _counterFailures.Clear();
                }
                _cts.Dispose();
            }
        }

        private static void ReleaseCounter(Counter counter)
        {
            try { counter.Dispose(); }
            catch (Exception error) { Debug.WriteLine($"[CpuMonitor] Counter cleanup failed: {error.Message}"); }
        }

        private const string HostCategory = "Hyper-V Hypervisor Logical Processor";
        private const string VmCategory = "Hyper-V Hypervisor Virtual Processor";

        private async Task MaintainCountersLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 一次批量查询同时取得 GUID、显示名和处理器数量，避免逐台关联查询。
                await UpdateVmInfoFromWmiAsync(token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;
                UpdateCounterInstances();
                try { await _backend.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }

        private void UpdateCounterInstances()
        {
            lock (_counterGate)
            {
                if (Volatile.Read(ref _stopping) != 0) return;
                _hostCounterIssue = RefreshCategory(HostCategory, isHost: true);
                _vmCounterIssue = RefreshCategory(VmCategory, isHost: false);
            }
        }

        private PerformanceSampleInfo? RefreshCategory(string category, bool isHost)
        {
            var now = DateTimeOffset.UtcNow;
            try
            {
                var detected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var instance in _backend.EnumerateInstances(category))
                {
                    string key;
                    if (isHost)
                    {
                        var match = Regex.Match(instance, @"^(?:Hv )?LP (\d+)$", RegexOptions.CultureInvariant);
                        if (!match.Success) continue;
                        key = "Host_" + match.Groups[1].Value;
                    }
                    else
                    {
                        if (!TryParseVmCounter(instance, out _, out _)) continue;
                        key = instance;
                    }
                    if (!detected.Add(key))
                    {
                        _counterFailures[key] = new(PerformanceSampleState.Ambiguous, now, "同一核心存在多个同名计数器，无法唯一确定采样来源。");
                        RemoveCounter(key);
                        continue;
                    }
                    if (Volatile.Read(ref _stopping) != 0) return null;
                    AddCounter(key, category, instance);
                }
                foreach (var key in _counters.Keys.Concat(_counterFailures.Keys).Distinct()
                    .Where(key => IsHostKey(key) == isHost && !detected.Contains(key)).ToArray())
                {
                    RemoveCounter(key);
                    _counterFailures.Remove(key);
                }
                return null;
            }
            catch (Exception ex)
            {
                return new(ex is NotSupportedException ? PerformanceSampleState.Unsupported : PerformanceSampleState.Failed,
                    now, ex.Message);
            }
        }

        private static bool IsHostKey(string key) => key.StartsWith("Host_", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(5), out _);

        private static bool TryParseVmCounter(string instance, out string name, out int coreId)
        {
            name = string.Empty;
            coreId = -1;
            int colon = instance.LastIndexOf(':');
            if (colon <= 0) return false;
            var match = Regex.Match(instance[(colon + 1)..], @"^Hv VP (\d+)$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out coreId)) return false;
            name = instance[..colon];
            return true;
        }

        private void RemoveCounter(string key)
        {
            if (_counters.Remove(key, out var counter)) ReleaseCounter(counter);
            _warmingCounters.Remove(key);
        }

        private void AddCounter(string key, string category, string instance)
        {
            if (_counters.ContainsKey(key)) return;
            Counter? counter = null;
            try
            {
                counter = _backend.CreateCounter(category, instance);
                counter.NextValue();
                if (Volatile.Read(ref _stopping) != 0) return;
                _counters.Add(key, counter);
                _warmingCounters.Add(key);
                _counterFailures.Remove(key);
                counter = null;
            }
            catch (Exception ex)
            {
                _counterFailures[key] = new(PerformanceSampleState.Failed, DateTimeOffset.UtcNow, ex.Message);
            }
            finally { if (counter != null) ReleaseCounter(counter); }
        }

        private async Task UpdateVmInfoFromWmiAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var virtualMachines = await _backend.ReadVirtualMachines(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (virtualMachines == null) throw new InvalidOperationException("虚拟机身份与处理器配置查询失败。");
                var snapshot = virtualMachines.ToArray();
                if (snapshot.Any(vm => vm.Id == Guid.Empty) || snapshot.Select(vm => vm.Id).Distinct().Count() != snapshot.Length)
                    throw new InvalidDataException("虚拟机身份缺失或重复，不能归属 CPU 样本。");
                lock (_counterGate)
                {
                    if (Volatile.Read(ref _stopping) != 0) return;
                    bool changed = !_vmSnapshotValid || !_virtualMachines.OrderBy(vm => vm.Id)
                        .SequenceEqual(snapshot.OrderBy(vm => vm.Id));
                    // 同名对象重建、改名或核心数改变后，不能把旧计数器的差值交给新身份。
                    if (changed)
                    {
                        foreach (var key in _counters.Keys.Where(key => !IsHostKey(key)).ToArray()) RemoveCounter(key);
                        foreach (var key in _counterFailures.Keys.Where(key => !IsHostKey(key)).ToArray()) _counterFailures.Remove(key);
                    }
                    _virtualMachines = snapshot;
                    _vmSnapshotValid = true;
                    _vmSnapshotError = null;
                    _virtualMachineIdentitySample = new(PerformanceSampleState.Valid, DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_counterGate)
                {
                    _vmSnapshotValid = false;
                    _vmSnapshotError = ex.Message;
                    _virtualMachineIdentitySample = new(PerformanceSampleState.Failed, DateTimeOffset.UtcNow, ex.Message);
                }
            }
        }

        private static async Task<IReadOnlyList<VmCpuInfo>?> ReadVirtualMachinesAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = await WmiApi.QueryAsync(
                "SELECT Name, ElementName, NumberOfProcessors, EnabledState FROM Msvm_SummaryInformation",
                obj =>
                {
                    ushort state = Convert.ToUInt16(obj["EnabledState"] ?? 0);
                    bool? active = VmMapper.IsActiveState(state) ? true
                        : VmMapper.MapState(state) is VmState.PoweredOff or VmState.Saved or VmState.Suspended
                            or VmState.FastSaved or VmState.Hibernated ? false : null;
                    return new VmCpuInfo(
                        Guid.TryParse(obj["Name"]?.ToString(), out var id) ? id : Guid.Empty,
                        obj["ElementName"]?.ToString() ?? string.Empty,
                        Convert.ToInt32(obj["NumberOfProcessors"] ?? 0),
                        active);
                },
                WmiScope.HyperV).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!response.Success) throw new InvalidOperationException(response.Error, response.Exception);
            return response.Data ?? [];
        }

        /// <summary>最近一次虚拟机身份与处理器配置查询的采样状态。</summary>
        /// <remarks>首次查询前为 NotSampled。未能取得身份的虚拟机不会出现在 GetCpuUsage 中；本状态用于说明身份查询是否完成或失败。</remarks>
        public PerformanceSampleInfo VirtualMachineIdentitySample
        {
            get { lock (_counterGate) return _virtualMachineIdentitySample; }
        }

        /// <summary>读取每个宿主逻辑处理器及虚拟 CPU 的当前性能计数器样本。</summary>
        /// <remarks>使用率按相邻两次调用之间的处理器运行时间计算，同一实例的调用者共享采样间隔。后台每两秒维护计数器清单。预热、身份查询失败或采样失败时 Sample.IsValid 为 false，此次数值不参与显示更新或计算。</remarks>
        /// <returns>按核心列出的数值及 Sample 状态；仅 IsValid 样本可用于更新显示或计算平均值。宿主由 IsHost 区分，虚拟机由 VmId 区分。尚未取得任何虚拟机身份时，不返回无法归属的虚拟机样本。</returns>
        /// <exception cref="ObjectDisposedException">本实例已经开始最终释放。</exception>
        public List<VmCpuCoreMetric> GetCpuUsage()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
            lock (_counterGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
                return ReadCpuUsage();
            }
        }

        private List<VmCpuCoreMetric> ReadCpuUsage()
        {
            var now = DateTimeOffset.UtcNow;
            var results = new List<VmCpuCoreMetric>();
            var counterValues = new Dictionary<string, (float Value, PerformanceSampleInfo Sample)>(StringComparer.Ordinal);
            foreach (var pair in _counters.ToArray())
            {
                var issue = IsHostKey(pair.Key) ? _hostCounterIssue : _vmCounterIssue;
                if (issue != null) { counterValues[pair.Key] = (0, issue with { ObservedAtUtc = now }); continue; }
                if (_warmingCounters.Remove(pair.Key))
                {
                    counterValues[pair.Key] = (0, new(PerformanceSampleState.WarmingUp, now));
                    continue;
                }
                try
                {
                    float value = pair.Value.NextValue();
                    if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("CPU 计数器没有返回有效使用率。");
                    counterValues[pair.Key] = (value, new(PerformanceSampleState.Valid, now));
                }
                catch (Exception ex)
                {
                    var failure = new PerformanceSampleInfo(PerformanceSampleState.Failed, now, ex.Message);
                    counterValues[pair.Key] = (0, failure);
                    _counterFailures[pair.Key] = failure;
                    RemoveCounter(pair.Key);
                }
            }

            var hostIds = _counters.Keys.Concat(_counterFailures.Keys).Where(IsHostKey)
                .Select(key => int.Parse(key.AsSpan(5))).Distinct().ToArray();
            if (hostIds.Length == 0) hostIds = Enumerable.Range(0, Environment.ProcessorCount).ToArray();
            foreach (int coreId in hostIds)
            {
                var sample = GetCounterSample("Host_" + coreId, counterValues, _hostCounterIssue, now);
                results.Add(new() { IsHost = true, VmName = "Host", CoreId = coreId, Usage = sample.Value, Sample = sample.Sample });
            }

            var names = _virtualMachines.GroupBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var vm in _virtualMachines)
            {
                PerformanceSampleInfo? issue = !_vmSnapshotValid
                    ? new(PerformanceSampleState.Failed, now, _vmSnapshotError)
                    : vm.IsRunning == null ? new(PerformanceSampleState.Unavailable, now, "尚未取得虚拟机运行状态。")
                    : vm.IsRunning == false ? new(PerformanceSampleState.NotApplicable, now)
                    : vm.CoreCount <= 0 || string.IsNullOrEmpty(vm.Name)
                        ? new(PerformanceSampleState.Unavailable, now, "虚拟机的名称或处理器数量尚未取得。")
                    : names[vm.Name] != 1 ? new(PerformanceSampleState.Ambiguous, now, "存在同名虚拟机，无法将名称计数器唯一映射到 GUID。")
                    : _vmCounterIssue;
                int count = Math.Max(1, vm.CoreCount);
                for (int core = 0; core < count; core++)
                {
                    var sample = issue != null ? (Value: 0f, Sample: issue with { ObservedAtUtc = now })
                        : GetCounterSample(vm.Name + ":Hv VP " + core, counterValues, null, now);
                    results.Add(new() { VmId = vm.Id, VmName = vm.Name, CoreId = vm.CoreCount > 0 ? core : -1,
                        Usage = sample.Value, Sample = sample.Sample });
                }
            }
            return results;
        }

        private (float Value, PerformanceSampleInfo Sample) GetCounterSample(string key,
            IReadOnlyDictionary<string, (float Value, PerformanceSampleInfo Sample)> values,
            PerformanceSampleInfo? categoryIssue, DateTimeOffset now)
        {
            if (categoryIssue != null) return (0, categoryIssue with { ObservedAtUtc = now });
            if (values.TryGetValue(key, out var value)) return value;
            if (_counterFailures.TryGetValue(key, out var failure)) return (0, failure with { ObservedAtUtc = now });
            return (0, new(PerformanceSampleState.Unavailable, now, "没有取得对应核心的有效计数器。"));
        }
    }
}
