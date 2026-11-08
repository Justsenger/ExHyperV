using ExHyperV.Models;
using ExHyperV.Tools;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace ExHyperV.Services
{
    /// <summary>虚拟机列表中一项可选信息的读取问题；不代表已经确认该信息为空。</summary>
    /// <param name="Component">受影响的信息类别，例如 GpuAssignments、PartitionableGpus、VideoControllerNames、SwitchNames 或 GuestIpAddresses。</param>
    /// <param name="Error">面向人的失败说明。</param>
    /// <param name="ErrorKind">业务失败类别；已确认对应可选类不存在时为 Unsupported。</param>
    /// <param name="ErrorSource">原生错误码所属来源。</param>
    /// <param name="Code">原生错误码；未提供时通常为 -1。</param>
    public sealed record VmQueryIssue(string Component, string Error, ApiErrorKind ErrorKind, ApiErrorSource ErrorSource, int Code);

    /// <summary>一次虚拟机清单快照，包含虚拟机及未完整取得的可选信息。</summary>
    /// <param name="VirtualMachines">按活动状态和名称排序的虚拟机列表。</param>
    /// <param name="Issues">可选读取失败或不支持的明细；必需读取失败不会产生成功快照。</param>
    public sealed record VmInventorySnapshot(IReadOnlyList<VmInstance> VirtualMachines, IReadOnlyList<VmQueryIssue> Issues)
    {
        /// <summary>本次列表所需的必需和可选读取均未报告问题。</summary>
        public bool IsComplete => Issues.Count == 0;
    }

    /// <summary>读取虚拟机列表并持有按需创建的性能计数器；调用者负责释放实例。</summary>
    /// <remarks>列表读取是一次快照，性能更新是另行调用。计数器首次建立基线时标记 WarmingUp；仅有效样本用于更新数值。更新方法原地修改传入模型，缺测通过 PerformanceSample 等状态报告。DisposeAsync 会等待本实例进行中的计数器工作，释放后不能再开始性能采样；部分普通 WMI 查询不依赖计数器。</remarks>
    public partial class VmQueryService : IDisposable, IAsyncDisposable
    {
        /// <summary>虚拟机当前内存占用与可用百分比。</summary>
        public struct VmDynamicMemoryData {
            /// <summary>当前分配内存，单位 MiB；仅 AssignedSample.IsValid 时表示本次有效值。</summary>
            public long AssignedMiB;
            /// <summary>来宾可用内存百分比；仅 AvailableSample.IsValid 时有效。负数表示内存需求超过分配量，保留原值，不把缺失值截断成零。</summary>
            public int AvailablePercent;
            /// <summary>已分配内存的本次读取状态及时间。</summary>
            public PerformanceSampleInfo? AssignedSample { get; init; }
            /// <summary>来宾可用百分比的本次读取状态及时间；可能独立于已分配内存缺测。</summary>
            public PerformanceSampleInfo? AvailableSample { get; init; }
        }

        /// <summary>按 GPU 引擎类别聚合的一次虚拟机采样结果。</summary>
        /// <remarks>同类别多个 GPU 引擎百分比直接累加，结果没有统一截断到 100；不应把它当作单一物理 GPU 的严格总利用率。</remarks>
        public struct GpuUsageData
        {
            /// <summary>3D 引擎占用百分比。</summary>
            public double Gpu3d;
            /// <summary>复制引擎占用百分比。</summary>
            public double GpuCopy;
            /// <summary>视频编码引擎占用百分比。</summary>
            public double GpuEncode;
            /// <summary>视频解码引擎占用百分比。</summary>
            public double GpuDecode;
            /// <summary>本次是否检测到该虚拟机进程的 GPU 引擎实例；使用率为零也可为 true，不证明来宾驱动状态或正在执行负载。仅 DriverSample.IsValid 时有本次结论。</summary>
            public bool IsDriverBound;
            /// <summary>本次引擎占用采样状态；无效时不能将各引擎的默认值视为零占用。</summary>
            public PerformanceSampleInfo? Sample { get; set; }
            /// <summary>本次 GPU 引擎实例检测的有效性；无效时 IsDriverBound 不代表确定结论。</summary>
            public PerformanceSampleInfo? DriverSample { get; set; }
        }

        private readonly object _switchCacheLock = new();
        private IReadOnlyDictionary<string, string> _switchNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DiskSizeCacheEntry> _diskSizeCache = new(StringComparer.OrdinalIgnoreCase);
        // VM → 一组 PID：vmwp + 其子 vmmem。GPU-PV 的 GPU 占用 Win11 记在 vmwp、Win10 记在 vmmem
        // （vmmem 是 vmwp 的子进程、同 VM 账户 SID），故一 VM 需对应多个 PID 才能跨版本命中。
        private readonly SemaphoreSlim _gpuSampleGate = new(1, 1);
        private readonly SemaphoreSlim _networkSampleGate = new(1, 1);
        private readonly object _counterLifetimeLock = new();
        private int _activeCounterSamples;
        private bool _closingCounters;
        private TaskCompletionSource<bool>? _counterSamplesDrained;
        private Task? _disposeTask;
        private readonly Func<string, bool> _counterCategoryExists;
        private readonly Func<string, string[]> _getCounterInstances;
        private readonly Func<string, string, string, IUsageCounter> _createCounter;
        private readonly Func<Task<ApiResponse<List<GpuProcessInfo>>>> _queryGpuProcesses;
        internal GpuPidSnapshot ProcessSnapshot { get; private set; } = new([], new(), DateTime.MinValue);
        private readonly List<IUsageCounter> _gpuCounters = new();
        private readonly Dictionary<string, string> _gpuCounterOwners = new(StringComparer.OrdinalIgnoreCase);
        private const string NetworkPerfCategory = "Hyper-V Virtual Network Adapter";
        private const string NetworkSentCounter = "Bytes Sent/sec";
        private const string NetworkReceivedCounter = "Bytes Received/sec";
        private readonly List<NetworkCounterSet> _networkCounters = new();
        private static readonly Regex GpuInstanceRegex = new Regex(@"pid_(\d+).*engtype_([a-zA-Z0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DiskDeviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

        /// <summary>建立独立的查询与性能计数器拥有者；不启动自动刷新循环。</summary>
        public VmQueryService() : this(PerformanceCounterCategory.Exists,
            category => new PerformanceCounterCategory(category).GetInstanceNames(),
            (category, counter, instance) => new UsageCounter(category, counter, instance)) { }

        internal VmQueryService(Func<string, bool> categoryExists, Func<string, string[]> getInstances,
            Func<string, string, string, IUsageCounter> createCounter,
            Func<Task<ApiResponse<List<GpuProcessInfo>>>>? queryGpuProcesses = null)
        {
            _counterCategoryExists = categoryExists;
            _getCounterInstances = getInstances;
            _createCounter = createCounter;
            _queryGpuProcesses = queryGpuProcesses ?? QueryGpuProcessesAsync;
        }

        internal interface IUsageCounter : IDisposable
        {
            string InstanceName { get; }
            float NextValue();
        }

        private sealed class UsageCounter(string category, string counter, string instance) : IUsageCounter
        {
            private readonly PerformanceCounter _counter = new(category, counter, instance, true);
            public string InstanceName => _counter.InstanceName;
            public float NextValue() => _counter.NextValue();
            public void Dispose() => _counter.Dispose();
        }

        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QuerySettings = "SELECT ConfigurationID, InstanceID, VirtualSystemSubType, Version FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        private const string QueryGpuPvSettings = "SELECT InstanceID, HostResource FROM Msvm_GpuPartitionSettingData";
        private const string QueryDiskPerf = "SELECT Name, ReadBytesPersec, WriteBytesPersec FROM Win32_PerfFormattedData_Counters_HyperVVirtualStorageDevice";
        private const string QuerySwitches = "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch";
        private const string QueryGuestNetwork = "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration";

        private sealed record DiskAlloc(string InstanceID, string Parent, string[] Paths, int ResourceType);
        private sealed record HvDisk(string DeviceID, int DriveNumber);
        private sealed record HostDisk(int Index, string Model, long Size, string PnpId);
        private sealed record SummaryItem(string Id, string Name, ushort State, int Cpu, VmDynamicMemoryData Memory, ulong Uptime);
        private sealed record MemItem(string FullId, double StartupRam);
        private sealed record ConfigItem(string VmGuid, string InstanceId, int Gen, string Ver);
        private sealed record GpuPvItem(string InstanceID, string[] HostResources);
        private sealed record PortItem(string InstanceID, string ElementName, string Address);
        private sealed record AllocItem(string InstanceID, string EnabledState, string[] HostResource);
        private sealed record SwitchItem(string Guid, string Name);
        private sealed record GuestNetItem(string InstanceID, string[] IPAddresses);
        internal sealed record PerfItem(string WmiName, ulong? Read, ulong? Write);

        internal sealed record GpuProcessInfo(int Pid, int ParentPid, string Name, string CommandLine, string? CreationDate = null);
        internal sealed record GpuPidSnapshot(Guid[] RequestedIds, Dictionary<Guid, int[]> Processes, DateTime CheckedAtUtc)
        {
            internal bool NeedsRefresh(IEnumerable<Guid> requestedIds, DateTime nowUtc) =>
                nowUtc - CheckedAtUtc > TimeSpan.FromSeconds(5) || !RequestedIds.ToHashSet().SetEquals(requestedIds);
        }

        private readonly record struct DiskFileStamp(long Length, long CreationTicks, long WriteTicks);
        private sealed record DiskSizeCacheEntry(DiskFileStamp Stamp, long Max, string Type);

        private sealed class NetworkCounterSet : IDisposable
        {
            public string AdapterId { get; }
            public IUsageCounter Sent { get; }
            public IUsageCounter Received { get; }

            public NetworkCounterSet(string adapterId, string instanceName,
                Func<string, string, string, IUsageCounter> createCounter)
            {
                AdapterId = adapterId;
                Sent = createCounter(NetworkPerfCategory, NetworkSentCounter, instanceName);
                try { Received = createCounter(NetworkPerfCategory, NetworkReceivedCounter, instanceName); }
                catch { Sent.Dispose(); throw; }
            }

            public void Dispose()
            {
                try { Sent.Dispose(); }
                finally { Received.Dispose(); }
            }
        }

        /// <summary>汇集虚拟机及设备配置生成列表快照，不启动持续刷新。</summary>
        /// <returns>成功时返回虚拟机列表及可选读取问题；必需摘要、配置、元数据、内存、存储或网卡基础读取失败时返回失败，不伪装为空列表。</returns>
        /// <remarks>可选 GPU 能力、来宾 IP 和友好名称读取失败时保留可用列表及既有显示回退，并在 Issues 中报告原生原因；使用 IsComplete 区分完整与部分结果。</remarks>
        public async Task<ApiResponse<VmInventorySnapshot>> GetVmListAsync()
        {
            try { return ApiResponse<VmInventorySnapshot>.Ok(await GetVmListCoreAsync().ConfigureAwait(false)); }
            catch (InventoryReadException ex)
            {
                var failure = ex.Result;
                return ApiResponse<VmInventorySnapshot>.Fail(failure.Error, failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
            }
            catch (Exception ex) { return ApiResponse<VmInventorySnapshot>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
        }

        private async Task<VmInventorySnapshot> GetVmListCoreAsync()
        {
            const string QueryVirtualDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 OR ResourceType = 16";
            const string QueryPhysicalDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 17";

            var vDiskTask = WmiApi.QueryAsync(QueryVirtualDiskAllocations, obj => new DiskAlloc(
                obj["InstanceID"]?.ToString() ?? "",
                obj["Parent"]?.ToString() ?? "",
                obj["HostResource"] as string[] ?? (obj["HostResource"] is string s1 ? new[] { s1 } : Array.Empty<string>()),
                Convert.ToInt32(obj["ResourceType"] ?? 0)
            ), WmiScope.HyperV);

            var pDiskTask = WmiApi.QueryAsync(QueryPhysicalDiskAllocations, obj => new DiskAlloc(
                obj["InstanceID"]?.ToString() ?? "",
                obj["Parent"]?.ToString() ?? "",
                obj["HostResource"] as string[] ?? (obj["HostResource"] is string s2 ? new[] { s2 } : Array.Empty<string>()),
                Convert.ToInt32(obj["ResourceType"] ?? 0)
            ), WmiScope.HyperV);

            var hvDiskTask = WmiApi.QueryAsync(
                "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber IS NOT NULL",
                obj => new HvDisk(
                    obj["DeviceID"]?.ToString() ?? "",
                    Convert.ToInt32(obj["DriveNumber"] ?? -1)
                ), WmiScope.HyperV);

            var hostDiskTask = WmiApi.QueryAsync(
                "SELECT Index, Model, Size, SerialNumber, PNPDeviceID FROM Win32_DiskDrive",
                obj => new HostDisk(
                    Convert.ToInt32(obj["Index"] ?? -1),
                    obj["Model"]?.ToString() ?? "",
                    Convert.ToInt64(obj["Size"] ?? 0L),
                    obj["PNPDeviceID"]?.ToString() ?? ""
                ), WmiScope.CimV2);

            var summaryTask = WmiApi.QueryAsync(QuerySummary, obj => {
                ushort state = Convert.ToUInt16(obj["EnabledState"] ?? (ushort)0);
                return new SummaryItem(
                    obj["Name"]?.ToString() ?? "",
                    obj["ElementName"]?.ToString() ?? "",
                    state,
                    Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    MapRuntimeMemory(obj["MemoryUsage"], null, state, null, DateTimeOffset.UtcNow),
                    Convert.ToUInt64(obj["UpTime"] ?? 0UL)
                );
            }, WmiScope.HyperV);

            var memTask = WmiApi.QueryAsync(QueryMemSettings, obj => new MemItem(
                obj["InstanceID"]?.ToString() ?? "",
                Convert.ToDouble(obj["VirtualQuantity"] ?? 0)
            ), WmiScope.HyperV);

            var configTask = WmiApi.QueryAsync(QuerySettings, obj => {
                string subType = obj["VirtualSystemSubType"]?.ToString() ?? "";
                int gen = subType.EndsWith(":1") ? 1 : (subType.EndsWith(":2") ? 2 : 0);
                return new ConfigItem(
                    obj["ConfigurationID"]?.ToString()?.Trim('{', '}').ToUpper() ?? "",
                    obj["InstanceID"]?.ToString() ?? "",
                    gen,
                    obj["Version"]?.ToString() ?? "0.0"
                );
            }, WmiScope.HyperV);

            var metadataTask = VmMetadataService.ReadAllAsync();

            var gpuPvTask = WmiApi.QueryAsync(QueryGpuPvSettings, obj => new GpuPvItem(
                obj["InstanceID"]?.ToString() ?? "",
                obj["HostResource"] as string[] ?? Array.Empty<string>()
            ), WmiScope.HyperV);

            // 主机可分区 GPU：Win10 早期 GpuPartitionSettingData.HostResource 只写读空，用它做 GpuName 兜底。
            var partGpuTask = WmiApi.QueryAsync("SELECT Name FROM Msvm_PartitionableGpu",
                obj => obj["Name"]?.ToString() ?? "", WmiScope.HyperV);

            var pciMapTask = GetHostVideoControllerMapAsync();

            var allPortsTask = WmiApi.QueryAsync(
                "SELECT ElementName, InstanceID, Address FROM Msvm_SyntheticEthernetPortSettingData",
                obj => new PortItem(
                    obj["InstanceID"]?.ToString() ?? "",
                    obj["ElementName"]?.ToString() ?? "",
                    obj["Address"]?.ToString() ?? ""
                ), WmiScope.HyperV);

            var allAllocsTask = WmiApi.QueryAsync(
                "SELECT EnabledState, InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData",
                obj => new AllocItem(
                    obj["InstanceID"]?.ToString() ?? "",
                    obj["EnabledState"]?.ToString() ?? "",
                    obj["HostResource"] as string[] ?? Array.Empty<string>()
                ), WmiScope.HyperV);

            var allSwitchesTask = WmiApi.QueryAsync(QuerySwitches, obj => new SwitchItem(
                obj["Name"]?.ToString() ?? "",
                obj["ElementName"]?.ToString() ?? ""
            ), WmiScope.HyperV);

            var guestNetTask = WmiApi.QueryAsync(QueryGuestNetwork, obj => new GuestNetItem(
                obj["InstanceID"]?.ToString() ?? "",
                obj["IPAddresses"] as string[] ?? Array.Empty<string>()
            ), WmiScope.HyperV);

            await Task.WhenAll(vDiskTask, pDiskTask, hvDiskTask, hostDiskTask,
                summaryTask, memTask, configTask, gpuPvTask, pciMapTask,
                allPortsTask, allAllocsTask, allSwitchesTask, guestNetTask, partGpuTask, metadataTask).ConfigureAwait(false);

            RequireInventoryRead(summaryTask.Result, "VirtualMachineSummary");
            RequireInventoryRead(configTask.Result, "VirtualMachineConfiguration");
            RequireInventoryRead(metadataTask.Result, "VirtualMachineMetadata");
            RequireInventoryRead(memTask.Result, "MemoryConfiguration");
            RequireInventoryRead(vDiskTask.Result, "VirtualStorageAttachments");
            RequireInventoryRead(pDiskTask.Result, "PhysicalStorageAttachments");
            RequireInventoryRead(hvDiskTask.Result, "VirtualMachinePhysicalDisks");
            RequireInventoryRead(hostDiskTask.Result, "HostPhysicalDisks");
            RequireInventoryRead(allPortsTask.Result, "NetworkAdapters");
            RequireInventoryRead(allAllocsTask.Result, "NetworkConnections");
            var issues = new List<VmQueryIssue>();
            AddInventoryIssue(issues, gpuPvTask.Result, "GpuAssignments", optionalClass: true);
            AddInventoryIssue(issues, partGpuTask.Result, "PartitionableGpus", optionalClass: true);
            AddInventoryIssue(issues, pciMapTask.Result, "VideoControllerNames");
            AddInventoryIssue(issues, allSwitchesTask.Result, "SwitchNames");
            AddInventoryIssue(issues, guestNetTask.Result, "GuestIpAddresses");
            var metadataRows = metadataTask.Result.Data.ToDictionary(row => row.Id, row => row.Items,
                StringComparer.OrdinalIgnoreCase);

            // ── 取出数据 ──────────────────────────────────────────
            var vDisks = vDiskTask.Result.Data ?? new List<DiskAlloc>();
            var pDisks = pDiskTask.Result.Data ?? new List<DiskAlloc>();
            var hvDisks = hvDiskTask.Result.Data ?? new List<HvDisk>();
            var hostDisks = hostDiskTask.Result.Data ?? new List<HostDisk>();
            var summaries = summaryTask.Result.Data ?? new List<SummaryItem>();
            var memList = memTask.Result.Data ?? new List<MemItem>();
            var configs = configTask.Result.Data ?? new List<ConfigItem>();
            var gpuPvList = gpuPvTask.Result.Data ?? new List<GpuPvItem>();
            var allPorts = allPortsTask.Result.Data ?? new List<PortItem>();
            var allAllocs = allAllocsTask.Result.Data ?? new List<AllocItem>();
            var allSwitches = allSwitchesTask.Result.Data ?? new List<SwitchItem>();
            var guestNet = guestNetTask.Result.Data ?? new List<GuestNetItem>();
            var pciFriendlyNames = pciMapTask.Result.Data ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ── 构建辅助映射表 ────────────────────────────────────
            var hvDiskMap = hvDisks
                .Where(d => !string.IsNullOrEmpty(d.DeviceID))
                .ToDictionary(d => d.DeviceID.Replace("\\\\", "\\"), d => d.DriveNumber, StringComparer.OrdinalIgnoreCase);

            var osDiskMap = hostDisks
                .Where(d => d.Index >= 0)
                .ToDictionary(d => d.Index, d => d);

            var configMap = configs
                .Where(x => !string.IsNullOrEmpty(x.VmGuid))
                .ToDictionary(x => x.VmGuid, x => x, StringComparer.OrdinalIgnoreCase);

            // 用已实现配置的完整标识匹配 KVP 组件，不把检查点中的旧选择混入当前列表。
            var osTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var config in configMap.Values)
            {
                string componentId = config.InstanceId + "\\" + VmMetadataService.ComponentId;
                if (!metadataRows.TryGetValue(componentId, out var items))
                    throw new InvalidOperationException("未读取到虚拟机当前配置的 KVP 组件，请刷新后重试。");
                var metadata = VmMetadataService.ParseItems(items);
                if (metadata.TryGetValue(VmMetadataService.OsTypeKey, out string? osType)
                    && !string.IsNullOrEmpty(osType))
                    osTypes[config.VmGuid] = osType;
            }

            var guestIpMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in guestNet)
            {
                string key = item.InstanceID.Split('\\').LastOrDefault() ?? "";
                if (!string.IsNullOrEmpty(key))
                    guestIpMap[key] = item.IPAddresses.ToList();
            }

            // Win10 早期 HostResource 只写读空 → 拿不到 PCI 名。用主机第一块可分区 GPU 的名字兜底
            // （与 GetVmGpuAdaptersAsync 的 Win10 兜底同源；单卡场景就是它）。
            string fallbackGpuName = null;
            foreach (var pg in (partGpuTask.Result.Data ?? new List<string>()))
            {
                string pid = ExtractPciId(pg) ?? "";
                if (!string.IsNullOrEmpty(pid) && pciFriendlyNames.TryGetValue(pid, out var nm)) { fallbackGpuName = nm; break; }
            }

            var gpuMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var gpuOwners = new HashSet<Guid>();
            bool gpuPresenceReliable = gpuPvTask.Result.Success;
            foreach (var setting in gpuPvList)
            {
                string guid = ExtractFirstGuid(setting.InstanceID) ?? "";
                if (!Guid.TryParse(guid, out var ownerId) || ownerId == Guid.Empty)
                {
                    gpuPresenceReliable = false;
                    continue;
                }
                gpuOwners.Add(ownerId);
                string gpuName = null;
                if (setting.HostResources.Length > 0)
                {
                    string pciId = ExtractPciId(setting.HostResources[0]) ?? "";
                    if (!string.IsNullOrEmpty(pciId)) pciFriendlyNames.TryGetValue(pciId, out gpuName);
                }
                if (string.IsNullOrEmpty(gpuName)) gpuName = fallbackGpuName;   // Win10 兜底
                if (!string.IsNullOrEmpty(gpuName)) gpuMap[guid] = gpuName;
            }
            if (gpuPvTask.Result.Success && !gpuPresenceReliable)
                issues.Add(new("GpuAssignments", "GPU 分配清单含无效虚拟机身份，不能确认未列出虚拟机的分配状态。",
                    ApiErrorKind.OperationFailed, ApiErrorSource.Wmi, -1));

            var switchNames = UpdateSwitchNameCache(allSwitchesTask.Result.Success,
                allSwitches.Select(sw => new KeyValuePair<string, string>(sw.Guid, sw.Name)));

            var allocsMap = allAllocs
                .GroupBy(a => {
                    int idx = a.InstanceID.LastIndexOf('\\');
                    return idx > 0 ? a.InstanceID.Substring(0, idx) : a.InstanceID;
                }, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var vmAdaptersMap = new Dictionary<string, List<VmNetworkAdapter>>(StringComparer.OrdinalIgnoreCase);
            foreach (var port in allPorts)
            {
                string vmGuid = ExtractFirstGuid(port.InstanceID) ?? "";
                if (string.IsNullOrEmpty(vmGuid)) continue;

                var adapter = new VmNetworkAdapter
                {
                    Id = port.InstanceID,
                    Name = port.ElementName,
                    MacAddress = FormatMac(port.Address)
                };

                if (allocsMap.TryGetValue(port.InstanceID, out var alloc))
                {
                    adapter.IsConnected = alloc.EnabledState == "2";
                    if (alloc.HostResource.Length > 0)
                    {
                        string swGuid = alloc.HostResource[0].Split('"').Reverse().Skip(1).FirstOrDefault() ?? "";
                        adapter.SwitchId = Guid.TryParse(swGuid, out var parsedSwitchId) ? parsedSwitchId : null;
                        if (!string.IsNullOrEmpty(swGuid) && switchNames.TryGetValue(swGuid, out var sName))
                            adapter.SwitchIdentity = sName;
                    }
                }

                string devKey = port.InstanceID.Split('\\').LastOrDefault() ?? "";
                if (!string.IsNullOrEmpty(devKey) && guestIpMap.TryGetValue(devKey, out var ips))
                    adapter.IpAddresses = ips;
                adapter.IpAddresses = VmIpService.ResolveAdapterAddresses(adapter.MacAddress, adapter.IpAddresses).Addresses;

                if (!vmAdaptersMap.ContainsKey(vmGuid))
                    vmAdaptersMap[vmGuid] = new List<VmNetworkAdapter>();
                vmAdaptersMap[vmGuid].Add(adapter);
            }

            // ── 组装 VmInstance（Model）列表 ─────────────────────
            var resultList = new List<VmInstance>();
            var seenVmIds = new HashSet<Guid>();
            // 本次组装按需复用原生信息缓冲区，文件句柄在每次读取后释放。
            using var diskSettings = new DiskSettingsReader();

            foreach (var s in summaries)
            {
                string vmGuidKey = s.Id.Trim('{', '}').ToUpper();
                if (!Guid.TryParse(s.Id, out var vmGuid) || vmGuid == Guid.Empty)
                    throw new InvalidOperationException("虚拟机摘要返回了无效 GUID，不能归属查询结果。");
                if (!seenVmIds.Add(vmGuid))
                    throw new InvalidOperationException("虚拟机摘要返回了重复 GUID，请重新查询。");
                var vmInfo = new VmInstance(vmGuid, s.Name);

                if (vmAdaptersMap.TryGetValue(vmGuidKey, out var adapters))
                {
                    foreach (var a in adapters) vmInfo.NetworkAdapters.Add(a);
                    vmInfo.MacAddress = adapters.FirstOrDefault()?.MacAddress ?? "00:00:00:00:00:00";
                    vmInfo.IpAddress = VmIpService.GetPrimaryAddress(adapters);
                }

                var allDiskResources = vDisks.Concat(pDisks)
                    .Where(d => d.Parent.ToUpper().Contains(vmGuidKey) || d.InstanceID.ToUpper().Contains(vmGuidKey))
                    .ToList();

                foreach (var d in allDiskResources)
                {
                    if (d.Paths.Length == 0) continue;
                    string pathRaw = d.Paths[0];
                    string cleanPath = pathRaw.Replace("\"", "").Trim();
                    if (string.IsNullOrEmpty(cleanPath)) continue;

                    bool isPhysical = cleanPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase)
                                   || cleanPath.ToUpper().Contains("PHYSICALDRIVE");

                    if (isPhysical)
                    {
                        int dNum = -1;
                        var devMatch = DiskDeviceIdRegex.Match(pathRaw);
                        if (devMatch.Success)
                        {
                            string devId = devMatch.Groups[1].Value.Replace("\\\\", "\\");
                            if (hvDiskMap.TryGetValue(devId, out int mapped)) dNum = mapped;
                        }
                        else if (cleanPath.ToUpper().Contains("PHYSICALDRIVE"))
                        {
                            var numMatch = Regex.Match(cleanPath, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
                            if (numMatch.Success) int.TryParse(numMatch.Groups[1].Value, out dNum);
                        }

                        if (dNum != -1 && osDiskMap.TryGetValue(dNum, out var hostInfo))
                        {
                            vmInfo.Disks.Add(new VmDiskStatus
                            {
                                Name = hostInfo.Model,
                                Path = $"PhysicalDrive{dNum}",
                                CurrentSize = hostInfo.Size,
                                MaxSize = hostInfo.Size,
                                SizeSample = new(hostInfo.Size > 0 ? PerformanceSampleState.Valid : PerformanceSampleState.Unavailable, DateTimeOffset.UtcNow),
                                CapacitySample = new(hostInfo.Size > 0 ? PerformanceSampleState.Valid : PerformanceSampleState.Unavailable, DateTimeOffset.UtcNow),
                                DiskType = "Physical",
                                PnpDeviceId = hostInfo.PnpId
                            });
                        }
                    }
                    else if (cleanPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        long size = 0;
                        PerformanceSampleInfo sizeSample;
                        try { size = new FileInfo(cleanPath).Length; sizeSample = new(PerformanceSampleState.Valid, DateTimeOffset.UtcNow); }
                        catch (Exception error) { sizeSample = new(PerformanceSampleState.Failed, DateTimeOffset.UtcNow, error.Message); }
                        vmInfo.Disks.Add(new VmDiskStatus
                        {
                            Name = Path.GetFileName(cleanPath),
                            Path = cleanPath,
                            CurrentSize = size,
                            MaxSize = size,
                            SizeSample = sizeSample,
                            CapacitySample = sizeSample,
                            DiskType = "ISO"
                        });
                    }
                    else
                    {
                        PerformanceSampleInfo sizeSample = new(PerformanceSampleState.NotSampled, default);
                        PerformanceSampleInfo capacitySample = sizeSample;
                        var (current, max, diskType) = GetDiskSizes(cleanPath, diskSettings.Read,
                            (size, capacity) => { sizeSample = size; capacitySample = capacity; });
                        vmInfo.Disks.Add(new VmDiskStatus
                        {
                            Name = Path.GetFileName(cleanPath),
                            Path = cleanPath,
                            CurrentSize = current,
                            MaxSize = max,
                            SizeSample = sizeSample,
                            CapacitySample = capacitySample,
                            DiskType = diskType
                        });
                    }
                }

                var memoryConfigurations = memList.Where(m => TryGetMemoryVmId(m.FullId, out var memoryId) && memoryId == vmGuid).ToArray();
                if (memoryConfigurations.Length != 1 || !double.IsFinite(memoryConfigurations[0].StartupRam)
                    || memoryConfigurations[0].StartupRam <= 0)
                    throw new InvalidOperationException("不能唯一确认虚拟机的有效内存配置，请重新查询。");
                double startupRam = memoryConfigurations[0].StartupRam;

                if (configMap.TryGetValue(vmGuidKey, out var config))
                {
                    vmInfo.Generation = config.Gen;
                    vmInfo.Version = config.Ver;
                }

                if (!configMap.ContainsKey(vmGuidKey))
                    throw new InvalidOperationException("虚拟机当前配置在查询期间发生变化，请刷新后重试。");
                vmInfo.OsType = osTypes.TryGetValue(vmGuidKey, out var osType) ? osType : "Windows";
                vmInfo.CpuCount = s.Cpu;
                vmInfo.MemoryGiB = startupRam / 1024.0;
                vmInfo.TotalDiskSizeGiB = vmInfo.Disks.Sum(disk => disk.MaxSize / (1024.0 * 1024 * 1024));
                vmInfo.AssignedMemoryGiB = s.Memory.AssignedMiB / 1024.0;
                vmInfo.AssignedMemorySample = s.Memory.AssignedSample;
                vmInfo.GpuName = gpuMap.TryGetValue(vmGuidKey, out var gName) ? gName : null;
                vmInfo.HasGpuAssignment = gpuPresenceReliable ? gpuOwners.Contains(vmGuid) : null;
                // Model 不做 transient 状态机；只填 raw 字段（VM 层通过 Apply→SyncBackendData 处理）
                vmInfo.StateText = VmMapper.MapStateCodeToText(s.State);
                vmInfo.StateCode = s.State;
                vmInfo.RawUptime = TimeSpan.FromMilliseconds(s.Uptime);
                resultList.Add(vmInfo);
            }

            return new VmInventorySnapshot(resultList.OrderByDescending(x => x.IsRunning).ThenBy(x => x.Name).ToArray(), issues.AsReadOnly());
        }

        private sealed class InventoryReadException(ApiResponse result) : Exception(result.Error)
        { internal ApiResponse Result { get; } = result; }

        internal static void RequireInventoryRead<T>(ApiResponse<T> response, string component)
        {
            if (response.HasData) return;
            throw new InventoryReadException(ApiResponse.Fail(component + " 读取失败：" +
                (response.Success ? "未取得有效查询结果。" : response.Error), response.Code,
                response.ErrorSource, response.Exception, response.Success ? ApiErrorKind.OperationFailed : InventoryErrorKind(response, false)));
        }

        internal static void AddInventoryIssue<T>(List<VmQueryIssue> issues, ApiResponse<T> response, string component, bool optionalClass = false)
        {
            if (response.HasData) return;
            issues.Add(new(component, response.Success ? "未取得有效查询结果。" : response.Error,
                InventoryErrorKind(response, optionalClass), response.ErrorSource, response.Code));
        }

        private static ApiErrorKind InventoryErrorKind<T>(ApiResponse<T> response, bool optionalClass)
        {
            if (optionalClass && response.ErrorSource == ApiErrorSource.Wmi && response.Code == (int)ManagementStatus.InvalidClass)
                return ApiErrorKind.Unsupported;
            if (response.ErrorKind != ApiErrorKind.None && response.ErrorKind != ApiErrorKind.Unspecified) return response.ErrorKind;
            if (response.Exception is UnauthorizedAccessException or System.Security.SecurityException
                || response.ErrorSource == ApiErrorSource.Win32 && response.Code == 5
                || response.ErrorSource == ApiErrorSource.Wmi && response.Code == (int)ManagementStatus.AccessDenied)
                return ApiErrorKind.AccessDenied;
            return ApiErrorKind.OperationFailed;
        }

        // --- 性能监控相关方法 ---

        /// <summary>采样磁盘性能计数器并原地更新传入虚拟机的磁盘读写速率。</summary>
        /// <param name="vms">要采样的虚拟机集合；更新方法会修改其中对应性能字段，调用者负责安排呈现线程。</param>
        /// <remarks>读写速率单位为字节/秒，针对后备文件或物理设备，不保证共享介质的速率按 VM 拆分。以完整文件或设备身份匹配计数器，不能唯一匹配时不更新数值。PerformanceSample 记录本次状态；明确非活动的 VM 标记 NotApplicable。虚拟盘身份另外读取全宿主存储分配，避免传入子集掩盖计数器名称编码碰撞。</remarks>
        public async Task UpdateDiskPerformanceAsync(IEnumerable<VmInstance> vms)
        {
            var machines = vms.ToArray();
            var observedAt = DateTimeOffset.UtcNow;
            foreach (var vm in machines)
                foreach (var disk in vm.Disks)
                    disk.PerformanceSample = new(vm.IsRunning ? PerformanceSampleState.Unavailable
                        : InactivePerformanceState(vm), observedAt);
            if (!machines.Any(vm => vm.IsRunning && vm.Disks.Count > 0)) return;
            try
            {
                var perfTask = WmiApi.QueryAsync(QueryDiskPerf, obj => new PerfItem(
                    obj["Name"]?.ToString() ?? "",
                    obj["ReadBytesPersec"] is null ? null : Convert.ToUInt64(obj["ReadBytesPersec"]),
                    obj["WriteBytesPersec"] is null ? null : Convert.ToUInt64(obj["WriteBytesPersec"])
                ), WmiScope.CimV2);
                var identitiesTask = WmiApi.QueryAsync(
                    "SELECT HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 OR ResourceType = 16",
                    obj => obj["HostResource"] as string[]
                        ?? (obj["HostResource"] is string path ? [path] : Array.Empty<string>()), WmiScope.HyperV);
                var physicalIdentitiesTask = machines.Any(vm => vm.IsRunning && vm.Disks.Any(disk => disk.DiskType == "Physical"))
                    ? WmiApi.QueryAsync("SELECT PNPDeviceID FROM Win32_DiskDrive", obj => obj["PNPDeviceID"]?.ToString() ?? "", WmiScope.CimV2)
                    : Task.FromResult(ApiResponse<List<string>>.Ok([]));
                await Task.WhenAll(perfTask, identitiesTask, physicalIdentitiesTask).ConfigureAwait(false);
                var perf = await perfTask;
                var identities = await identitiesTask;
                var physicalIdentities = await physicalIdentitiesTask;
                if (!perf.Success)
                {
                    var failed = InventoryErrorKind(perf, optionalClass: true);
                    SetDiskSampleFailure(machines, observedAt, failed == ApiErrorKind.Unsupported
                        ? PerformanceSampleState.Unsupported : PerformanceSampleState.Failed,
                        perf.Error);
                    return;
                }
                ApplyDiskPerformance(machines, perf.Data ?? [],
                    (identities.Data ?? []).SelectMany(paths => paths), observedAt, physicalIdentities.Data ?? []);
                foreach (var disk in machines.Where(vm => vm.IsRunning).SelectMany(vm => vm.Disks))
                {
                    if (disk.DiskType == "Physical" && !physicalIdentities.Success)
                        disk.PerformanceSample = new(PerformanceSampleState.Failed, observedAt, physicalIdentities.Error);
                    else if (disk.DiskType != "Physical" && !identities.Success)
                        disk.PerformanceSample = new(PerformanceSampleState.Failed, observedAt, identities.Error);
                }
            }
            catch (Exception ex) { SetDiskSampleFailure(machines, observedAt, PerformanceSampleState.Failed, ex.Message); }
        }

        private static void SetDiskSampleFailure(IEnumerable<VmInstance> machines, DateTimeOffset observedAt,
            PerformanceSampleState state, string error)
        {
            foreach (var disk in machines.Where(vm => vm.IsRunning).SelectMany(vm => vm.Disks))
                disk.PerformanceSample = new(state, observedAt, error);
        }

        internal static void ApplyDiskPerformance(IEnumerable<VmInstance> machines, IReadOnlyList<PerfItem> samples,
            IEnumerable<string> registeredPaths, DateTimeOffset observedAt, IEnumerable<string>? physicalIdentities = null)
        {
            var paths = registeredPaths.Select(CanonicalDiskPath).Where(path => path != null)
                .Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var owners = paths.SelectMany(path => DiskCounterNames(path).Select(name => (Name: name, Path: path)))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
            var devices = (physicalIdentities ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var deviceOwners = devices.GroupBy(EncodeDiskCounterName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var vm in machines)
            {
                foreach (var disk in vm.Disks)
                {
                    if (!vm.IsRunning)
                    {
                        disk.PerformanceSample = new(InactivePerformanceState(vm), observedAt);
                        continue;
                    }
                    string? path = disk.DiskType == "Physical" ? null : CanonicalDiskPath(disk.Path);
                    var names = disk.DiskType == "Physical"
                        ? !devices.Contains(disk.PnpDeviceId, StringComparer.OrdinalIgnoreCase) ? [] : new[] { EncodeDiskCounterName(disk.PnpDeviceId) }
                        : path == null || !paths.Contains(path, StringComparer.OrdinalIgnoreCase) ? [] : DiskCounterNames(path);
                    var matches = samples.Where(sample => names.Contains(sample.WmiName, StringComparer.OrdinalIgnoreCase)).ToArray();
                    bool ambiguous = matches.Length > 1
                        || samples.Any(sample => names.Any(name => HasCounterDuplicateSuffix(sample.WmiName, name)))
                        || path != null && names.Any(name => owners.TryGetValue(name, out var candidates) && candidates.Length > 1)
                        || disk.DiskType == "Physical" && names.Any(name => deviceOwners.GetValueOrDefault(name) > 1);
                    if (ambiguous)
                    {
                        disk.PerformanceSample = new(PerformanceSampleState.Ambiguous, observedAt, "存储计数器身份不能唯一对应到当前磁盘。");
                        continue;
                    }
                    if (matches.Length == 0)
                    {
                        disk.PerformanceSample = new(PerformanceSampleState.Unavailable, observedAt, "未找到完整磁盘身份对应的计数器。");
                        continue;
                    }
                    var sample = matches[0];
                    if (!sample.Read.HasValue || !sample.Write.HasValue || sample.Read > long.MaxValue || sample.Write > long.MaxValue)
                    {
                        disk.PerformanceSample = new(PerformanceSampleState.Unavailable, observedAt, "磁盘速率字段缺失或超出有效范围。");
                        continue;
                    }
                    disk.ReadBytesPerSecond = (long)sample.Read.Value;
                    disk.WriteBytesPerSecond = (long)sample.Write.Value;
                    disk.PerformanceSample = new(PerformanceSampleState.Valid, observedAt);
                }
            }
        }

        private static string? CanonicalDiskPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
                else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
                if (!Path.IsPathFullyQualified(path)) return null;
                return Path.GetFullPath(path);
            }
            catch { return null; }
        }

        private static string[] DiskCounterNames(string path)
            => new[] { EncodeDiskCounterName(path), EncodeDiskCounterName(path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..] : @"\\?\" + path) }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // 保留完整路径中的原有标点；编码碰撞必须由全宿主路径集合检出，不能剥掉标点后模糊匹配。
        private static string EncodeDiskCounterName(string path)
            => path.Replace('\\', '-').Replace('/', '-').Replace('(', '[').Replace(')', ']');

        private static bool HasCounterDuplicateSuffix(string instance, string candidate)
            => instance.StartsWith(candidate + "#", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(instance.AsSpan(candidate.Length + 1), out _);

        private static PerformanceSampleState InactivePerformanceState(VmInstance vm)
            => vm.State is VmState.PoweredOff or VmState.Saved or VmState.Suspended or VmState.FastSaved
                or VmState.Hibernated or VmState.NotApplicable ? PerformanceSampleState.NotApplicable : PerformanceSampleState.Unavailable;

        // GetGpuPerformanceAsync 使用 PerformanceCounter（.NET 原生 API），不需要迁移
        /// <summary>采样各虚拟机对应的 GPU 引擎占用，并检测是否出现该虚拟机的 GPU 活动实例。</summary>
        /// <param name="vms">目标虚拟机列表，用于筛选运行中且带 GPU 的虚拟机；此方法返回字典，不原地写入 GPU 指标。</param>
        /// <returns>以虚拟机 GUID 为键的本次采样状态；Sample 与 DriverSample 分别说明占用及活动检测是否有效。失败、预热或缺测不发布有效零值。</returns>
        /// <exception cref="ObjectDisposedException">实例已开始释放，不能再开始 GPU 性能采样。</exception>
        public async Task<Dictionary<Guid, GpuUsageData>> GetGpuPerformanceAsync(IEnumerable<VmInstance> vms)
        {
            BeginCounterSample();
            try
            {
                await _gpuSampleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ThrowIfCountersClosing();
                    return await SampleGpuPerformanceAsync(vms).ConfigureAwait(false);
                }
                finally { _gpuSampleGate.Release(); }
            }
            finally { EndCounterSample(); }
        }

        private async Task<Dictionary<Guid, GpuUsageData>> SampleGpuPerformanceAsync(IEnumerable<VmInstance> vms)
        {
            var results = new Dictionary<Guid, GpuUsageData>();
            var machines = vms.DistinctBy(vm => vm.Id).ToArray();
            var observedAt = DateTimeOffset.UtcNow;
            foreach (var vm in machines)
            {
                var sample = new PerformanceSampleInfo(vm.IsRunning
                    ? vm.HasGpuAssignment == false ? PerformanceSampleState.NotApplicable : PerformanceSampleState.Unavailable
                    : InactivePerformanceState(vm), observedAt);
                results[vm.Id] = new GpuUsageData { Sample = sample, DriverSample = sample };
            }
            var gpuVms = machines.Where(vm => vm.IsRunning && vm.HasGpu).ToList();
            if (!gpuVms.Any()) return results;

            try
            {
                if (!_counterCategoryExists("GPU Engine"))
                {
                    SetGpuSampleFailure(results, gpuVms, observedAt, PerformanceSampleState.Unsupported, "系统未提供 GPU Engine 计数器。");
                    return results;
                }
                // 每次核对进程归属，不能让已退出进程的 PID 缓存把新进程计数归给旧 VM。
                var processResponse = await _queryGpuProcesses().ConfigureAwait(false);
                if (!processResponse.Success)
                {
                    SetGpuSampleFailure(results, gpuVms, observedAt, PerformanceSampleState.Failed, processResponse.Error);
                    return results;
                }
                var processes = processResponse.Data ?? [];
                PublishGpuProcessSnapshot(gpuVms.Select(vm => vm.Id), true, processes, DateTime.UtcNow);
                if (Volatile.Read(ref _closingCounters))
                {
                    SetGpuSampleFailure(results, gpuVms, observedAt, PerformanceSampleState.Unavailable, "采样器正在释放。");
                    return results;
                }
                string[] allGpuInstances = _getCounterInstances("GPU Engine");
                var owners = ProcessSnapshot.Processes.SelectMany(pair => pair.Value.Select(pid => (Pid: pid, Id: pair.Key)))
                    .GroupBy(pair => pair.Pid).ToDictionary(group => group.Key,
                        group => group.Select(pair => pair.Id).Distinct().ToArray());
                var expected = new Dictionary<Guid, string[]>();
                var expectedOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var vm in gpuVms)
                {
                    if (!ProcessSnapshot.Processes.TryGetValue(vm.Id, out var pids))
                    {
                        bool ambiguous = processes.Count(process => IsVmWorker(process, vm.Id)) > 1;
                        var invalid = new PerformanceSampleInfo(ambiguous ? PerformanceSampleState.Ambiguous
                            : PerformanceSampleState.Unavailable, observedAt, "未取得唯一的虚拟机工作进程身份。");
                        results[vm.Id] = new GpuUsageData { Sample = invalid, DriverSample = invalid };
                        continue;
                    }
                    if (pids.Any(pid => owners[pid].Length != 1))
                    {
                        var invalid = new PerformanceSampleInfo(PerformanceSampleState.Ambiguous, observedAt, "GPU 进程身份对应多台虚拟机。");
                        results[vm.Id] = new GpuUsageData { Sample = invalid, DriverSample = invalid };
                        continue;
                    }
                    string[] matching = allGpuInstances.Where(name => TryGpuInstance(name, out int pid, out _)
                        && pids.Contains(pid)).ToArray();
                    var data = results[vm.Id];
                    data.IsDriverBound = matching.Length > 0;
                    data.DriverSample = new(PerformanceSampleState.Valid, observedAt);
                    var measured = matching.Where(name => TryGpuInstance(name, out _, out string engine)
                        && IsReportedGpuEngine(engine)).ToArray();
                    if (measured.Distinct(StringComparer.OrdinalIgnoreCase).Count() != measured.Length)
                        data.Sample = new(PerformanceSampleState.Ambiguous, observedAt, "GPU 计数器实例身份重复。");
                    else if (measured.Length == 0)
                        data.Sample = new(PerformanceSampleState.Unavailable, observedAt, "当前未提供可采样的 GPU 引擎实例。");
                    else
                    {
                        bool identitiesKnown = true;
                        foreach (string name in measured)
                        {
                            TryGpuInstance(name, out int pid, out _);
                            var identity = processes.Where(process => process.Pid == pid).ToArray();
                            if (identity.Length != 1 || string.IsNullOrWhiteSpace(identity[0].CreationDate))
                            {
                                identitiesKnown = false;
                                break;
                            }
                            expectedOwners[name] = $"{vm.Id:D}|{pid}|{identity[0].CreationDate}";
                        }
                        if (identitiesKnown) expected[vm.Id] = measured;
                        else data.Sample = new(PerformanceSampleState.Unavailable, observedAt, "GPU 工作进程的创建身份不完整，不能沿用计数器基线。");
                    }
                    results[vm.Id] = data;
                }
                var desired = expected.Values.SelectMany(names => names).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var stale in _gpuCounters.Where(counter => !desired.Contains(counter.InstanceName)
                    || !_gpuCounterOwners.TryGetValue(counter.InstanceName, out var owner)
                    || !expectedOwners.TryGetValue(counter.InstanceName, out var expectedOwner)
                    || owner != expectedOwner).ToArray())
                {
                    _gpuCounters.Remove(stale);
                    _gpuCounterOwners.Remove(stale.InstanceName);
                    stale.Dispose();
                }
                foreach (var pair in expected)
                {
                    var data = results[pair.Key];
                    bool warming = false;
                    string? error = null;
                    foreach (string name in pair.Value)
                    {
                        var counter = _gpuCounters.FirstOrDefault(item => item.InstanceName.Equals(name, StringComparison.OrdinalIgnoreCase));
                        try
                        {
                            if (counter == null)
                            {
                                counter = _createCounter("GPU Engine", "Utilization Percentage", name);
                                counter.NextValue();
                                _gpuCounters.Add(counter);
                                _gpuCounterOwners[name] = expectedOwners[name];
                                warming = true;
                                continue;
                            }
                            float value = counter.NextValue();
                            if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("GPU 计数器返回无效值。");
                            TryGpuInstance(name, out _, out string engine);
                            switch (engine.ToUpperInvariant())
                            {
                                case "3D": data.Gpu3d += value; break;
                                case "COPY": data.GpuCopy += value; break;
                                case "VIDEOENCODE": case "ENCODE": data.GpuEncode += value; break;
                                case "VIDEODECODE": case "DECODE": data.GpuDecode += value; break;
                            }
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                            if (counter != null)
                            {
                                _gpuCounters.Remove(counter);
                                _gpuCounterOwners.Remove(name);
                                try { counter.Dispose(); } catch { }
                            }
                        }
                    }
                    data.Sample = new(error != null ? PerformanceSampleState.Failed
                        : warming ? PerformanceSampleState.WarmingUp : PerformanceSampleState.Valid, observedAt, error);
                    results[pair.Key] = data;
                }
            }
            catch (Exception ex) { SetGpuSampleFailure(results, gpuVms, observedAt, PerformanceSampleState.Failed, ex.Message); }
            return results;
        }

        private static void SetGpuSampleFailure(Dictionary<Guid, GpuUsageData> results, IEnumerable<VmInstance> targets,
            DateTimeOffset observedAt, PerformanceSampleState state, string error)
        {
            foreach (var vm in targets)
            {
                var invalid = new PerformanceSampleInfo(state, observedAt, error);
                results[vm.Id] = new GpuUsageData { Sample = invalid, DriverSample = invalid };
            }
        }

        private static bool TryGpuInstance(string name, out int pid, out string engine)
        {
            var match = GpuInstanceRegex.Match(name);
            engine = match.Success ? match.Groups[2].Value : string.Empty;
            pid = 0;
            return name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase) && match.Success
                && int.TryParse(match.Groups[1].Value, out pid);
        }

        private static bool IsReportedGpuEngine(string engine)
            => engine.Equals("3D", StringComparison.OrdinalIgnoreCase) || engine.Equals("Copy", StringComparison.OrdinalIgnoreCase)
                || engine.Equals("VideoEncode", StringComparison.OrdinalIgnoreCase) || engine.Equals("Encode", StringComparison.OrdinalIgnoreCase)
                || engine.Equals("VideoDecode", StringComparison.OrdinalIgnoreCase) || engine.Equals("Decode", StringComparison.OrdinalIgnoreCase);

        /// <summary>采样网络性能计数器并原地更新传入虚拟机的收发速率。</summary>
        /// <param name="vms">要采样的虚拟机集合；更新方法会修改其中对应性能字段，调用者负责安排呈现线程。</param>
        /// <remarks>模型中的收发速率采用字节/秒。此方法不建立自动定时器，调用者决定采样间隔。首次建立计数器标记 WarmingUp；只有收发均有效才更新两个速率及 HasNetworkSpeedSample。缺测时保留旧数值，PerformanceSample 说明本次状态。</remarks>
        /// <exception cref="ObjectDisposedException">实例已开始释放，不能再开始网络性能采样。</exception>
        public async Task UpdateNetworkPerformanceAsync(IEnumerable<VmInstance> vms)
        {
            BeginCounterSample();
            try
            {
                await _networkSampleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ThrowIfCountersClosing();
                    SampleNetworkPerformance(vms);
                }
                finally { _networkSampleGate.Release(); }
            }
            finally { EndCounterSample(); }
        }

        private void SampleNetworkPerformance(IEnumerable<VmInstance> vms)
        {
            var machines = vms.ToArray();
            var observedAt = DateTimeOffset.UtcNow;
            foreach (var vm in machines)
                foreach (var adapter in vm.NetworkAdapters)
                {
                    adapter.HasNetworkSpeedSample = false;
                    adapter.PerformanceSample = new(vm.IsRunning ? PerformanceSampleState.Unavailable
                        : InactivePerformanceState(vm), observedAt);
                }
            var adapters = machines.Where(vm => vm.IsRunning).SelectMany(vm => vm.NetworkAdapters).ToArray();

            try
            {
                if (!_counterCategoryExists(NetworkPerfCategory))
                {
                    foreach (var adapter in adapters)
                        adapter.PerformanceSample = new(PerformanceSampleState.Unsupported, observedAt, "系统未提供虚拟网卡性能计数器。");
                    return;
                }
                var instances = _getCounterInstances(NetworkPerfCategory);
                var bindings = new List<(VmNetworkAdapter Adapter, string Name)>();
                foreach (var adapter in adapters)
                {
                    var matches = FindNetworkCounterInstances(adapter.Id, instances);
                    if (matches.Length != 1)
                    {
                        adapter.PerformanceSample = new(matches.Length > 1 ? PerformanceSampleState.Ambiguous
                            : PerformanceSampleState.Unavailable, observedAt, "未找到唯一的虚拟网卡计数器身份。");
                        continue;
                    }
                    bindings.Add((adapter, matches[0]));
                }
                var repeatedIds = bindings.GroupBy(binding => binding.Adapter.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var repeatedNames = bindings.GroupBy(binding => binding.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var repeated in bindings.Where(binding => repeatedIds.Contains(binding.Adapter.Id) || repeatedNames.Contains(binding.Name)))
                    repeated.Adapter.PerformanceSample = new(PerformanceSampleState.Ambiguous, observedAt, "多个目标对应同一个虚拟网卡计数器。");
                bindings.RemoveAll(binding => repeatedIds.Contains(binding.Adapter.Id) || repeatedNames.Contains(binding.Name));
                foreach (var stale in _networkCounters.Where(counter => !bindings.Any(binding =>
                    binding.Adapter.Id.Equals(counter.AdapterId, StringComparison.OrdinalIgnoreCase)
                    && binding.Name.Equals(counter.Sent.InstanceName, StringComparison.OrdinalIgnoreCase))).ToArray())
                {
                    _networkCounters.Remove(stale);
                    stale.Dispose();
                }
                foreach (var binding in bindings)
                {
                    var adapter = binding.Adapter;
                    var counter = _networkCounters.FirstOrDefault(item => item.AdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase)
                        && item.Sent.InstanceName.Equals(binding.Name, StringComparison.OrdinalIgnoreCase));
                    try
                    {
                        if (counter == null)
                        {
                            counter = new NetworkCounterSet(adapter.Id, binding.Name, _createCounter);
                            counter.Sent.NextValue();
                            counter.Received.NextValue();
                            _networkCounters.Add(counter);
                            adapter.PerformanceSample = new(PerformanceSampleState.WarmingUp, observedAt);
                            continue;
                        }
                        float sent = counter.Sent.NextValue();
                        float received = counter.Received.NextValue();
                        if (!float.IsFinite(sent) || !float.IsFinite(received) || sent < 0 || received < 0
                            || (double)sent >= ulong.MaxValue || (double)received >= ulong.MaxValue)
                            throw new InvalidDataException("虚拟网卡速率样本无效。");
                        adapter.SendBytesPerSecond = (ulong)sent;
                        adapter.ReceiveBytesPerSecond = (ulong)received;
                        adapter.HasNetworkSpeedSample = true;
                        adapter.PerformanceSample = new(PerformanceSampleState.Valid, observedAt);
                    }
                    catch (Exception ex)
                    {
                        adapter.PerformanceSample = new(PerformanceSampleState.Failed, observedAt, ex.Message);
                        if (counter != null)
                        {
                            _networkCounters.Remove(counter);
                            try { counter.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var adapter in adapters)
                {
                    adapter.HasNetworkSpeedSample = false;
                    adapter.PerformanceSample = new(PerformanceSampleState.Failed, observedAt, ex.Message);
                }
            }
        }

        private static string? FindNetworkCounterInstance(string adapterId, IEnumerable<string> instances)
        {
            var matches = FindNetworkCounterInstances(adapterId, instances);
            return matches.Length == 1 ? matches[0] : null;
        }

        internal static string[] FindNetworkCounterInstances(string adapterId, IEnumerable<string> instances)
        {
            var ids = Regex.Matches(adapterId, @"[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}")
                .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // InstanceID 的第一个 GUID 是 VM，后续 GUID 才是网卡；只用网卡 GUID，避免同 VM 多网卡串号。
            if (ids.Count > 1) ids = ids.Skip(1).ToList();
            return instances
                .Where(instance => Regex.Matches(instance, @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")
                    .Any(match => ids.Contains(match.Value, StringComparer.OrdinalIgnoreCase)))
                .ToArray();
        }

        private void BeginCounterSample()
        {
            lock (_counterLifetimeLock)
            {
                ThrowIfCountersClosing();
                _activeCounterSamples++;
            }
        }

        private void EndCounterSample()
        {
            lock (_counterLifetimeLock)
            {
                if (--_activeCounterSamples == 0 && _closingCounters)
                    _counterSamplesDrained?.TrySetResult(true);
            }
        }

        private void ThrowIfCountersClosing()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closingCounters), this);
        }

        /// <summary>停止接受新的性能采样，等待进行中的计数器访问结束并释放计数器。</summary>
        public ValueTask DisposeAsync()
        {
            lock (_counterLifetimeLock)
            {
                if (_disposeTask != null) return new ValueTask(_disposeTask);
                Volatile.Write(ref _closingCounters, true);
                Task drained = _activeCounterSamples == 0 ? Task.CompletedTask
                    : (_counterSamplesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                _disposeTask = ReleaseCountersAsync(drained);
                return new ValueTask(_disposeTask);
            }
        }

        /// <summary>同步等待异步释放完成，回收本实例的性能计数器。</summary>
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        private async Task ReleaseCountersAsync(Task drained)
        {
            await drained.ConfigureAwait(false);
            List<Exception> errors = [];
            try { ClearCounters(_gpuCounters); }
            catch (Exception error) { errors.Add(error); }
            _gpuCounterOwners.Clear();
            try { ClearCounters(_networkCounters); }
            catch (Exception error) { errors.Add(error); }
            // No admitted sample can still be queued on either gate after drained.
            _gpuSampleGate.Dispose();
            _networkSampleGate.Dispose();
            if (errors.Count != 0) throw new AggregateException(errors);
        }

        private static void ClearCounters<T>(List<T> counters) where T : IDisposable
        {
            List<Exception>? errors = null;
            foreach (var counter in counters)
            {
                try { counter.Dispose(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            counters.Clear();
            if (errors != null) throw new AggregateException(errors);
        }

        /// <summary>按稳定身份读取虚拟机当前运行状态。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <returns>成功时返回业务状态；目标不存在与查询失败分别返回失败，不伪装成未知状态。</returns>
        public async Task<ApiResponse<VmState>> GetVmStateAsync(Guid vmId)
        {
            if (vmId == Guid.Empty)
                return ApiResponse<VmState>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var result = await WmiApi.QueryFirstAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => VmMapper.MapState(Convert.ToUInt16(obj["EnabledState"])));
            return result.IsEmpty
                ? ApiResponse<VmState>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound) : result;
        }

        /// <summary>按稳定身份检查虚拟机是否处于完全关机状态。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <returns>成功时返回是否完全关机；目标不存在或查询失败返回失败。</returns>
        public async Task<ApiResponse<bool>> IsVmPoweredOffAsync(Guid vmId)
        {
            var state = await GetVmStateAsync(vmId);
            return state.HasData ? ApiResponse<bool>.Ok(state.Data == VmState.PoweredOff)
                : ApiResponse<bool>.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);
        }

        /// <summary>按稳定身份读取虚拟机配置文件所在的根路径。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <returns>成功时返回配置根路径；配置缺失与查询失败分别返回失败。</returns>
        public async Task<ApiResponse<string>> GetVmConfigRootAsync(Guid vmId)
        {
            if (vmId == Guid.Empty)
                return ApiResponse<string>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var result = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                obj => obj["ConfigurationDataRoot"]?.ToString(), WmiScope.HyperV);
            return result.IsEmpty
                ? ApiResponse<string>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound) : result;
        }

        internal async Task<string> GetVmStateAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) throw new InvalidOperationException(identity.Error);
            var response = await GetVmStateAsync(identity.Data);
            if (!response.HasData) throw new InvalidOperationException(response.Error);
            return VmMapper.ToStateCode(response.Data).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal async Task<(bool IsOff, string CurrentState)> IsVmPoweredOffAsync(string vmName)
        {
            var state = await GetVmStateAsync(vmName);
            return (state == "3", state);
        }

        internal async Task<string?> GetVmConfigRootAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) throw new InvalidOperationException(identity.Error);
            var response = await GetVmConfigRootAsync(identity.Data);
            if (!response.HasData) throw new InvalidOperationException(response.Error);
            return response.Data;
        }
        // --- 私有辅助方法：磁盘与硬件映射 ---

        internal IReadOnlyDictionary<string, string> UpdateSwitchNameCache(bool success,
            IEnumerable<KeyValuePair<string, string>> switches)
        {
            lock (_switchCacheLock)
            {
                if (success)
                {
                    var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in switches)
                        if (!string.IsNullOrEmpty(pair.Key)) fresh[pair.Key] = pair.Value;
                    _switchNameCache = fresh;
                }
                return _switchNameCache;
            }
        }

        internal static (long Current, long Max, string DiskType) GetDiskSizes(string path,
            Func<string, (long Max, string Type)> readSettings,
            Action<PerformanceSampleInfo, PerformanceSampleInfo>? reportSamples = null)
        {
            var observed = DateTimeOffset.UtcNow;
            var unavailable = new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observed, "未取得磁盘文件信息。");
            if (string.IsNullOrEmpty(path))
            {
                reportSamples?.Invoke(unavailable, unavailable);
                return (0, 0, "Unknown");
            }
            var before = ReadDiskFileStamp(path, out bool missing);
            if (missing)
            {
                _diskSizeCache.TryRemove(path, out _);
                reportSamples?.Invoke(unavailable, unavailable);
                return (0, 0, "Unknown");
            }
            if (before is DiskFileStamp stamp && _diskSizeCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                reportSamples?.Invoke(new(PerformanceSampleState.Valid, observed), new(PerformanceSampleState.Valid, observed));
                return (stamp.Length, cached.Max, cached.Type);
            }
            _diskSizeCache.TryRemove(path, out _);

            (long Max, string Type) metadata = (0, "Unknown");
            string? metadataError = null;
            try { metadata = readSettings(path); }
            catch (Exception error) { metadataError = error.Message; }
            var after = ReadDiskFileStamp(path, out _);
            long currentSize = after?.Length ?? 0;
            bool changed = before.HasValue && after != before;
            bool capacityValid = metadata.Max > 0 && !changed;
            reportSamples?.Invoke(after.HasValue ? new(PerformanceSampleState.Valid, observed) : unavailable,
                capacityValid ? new(PerformanceSampleState.Valid, observed)
                    : new(PerformanceSampleState.Unavailable, observed, metadataError
                        ?? (changed ? "读取期间磁盘文件发生变化。" : "未取得虚拟磁盘容量。")));
            // Do not persist fallback/error results, or publish a query against a file
            // that changed while metadata was read. Concurrent readers publish complete entries.
            if (metadata.Max > 0 && before is DiskFileStamp unchanged && after == before)
                _diskSizeCache[path] = new(unchanged, metadata.Max, metadata.Type);
            return (currentSize, capacityValid ? metadata.Max : 0, capacityValid ? metadata.Type : "Unknown");
        }

        private static DiskFileStamp? ReadDiskFileStamp(string path, out bool missing)
        {
            missing = false;
            try
            {
                var file = new FileInfo(path);
                // Length preserves the precise exception that Exists would hide (including access denied).
                long length = file.Length;
                return new(length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks);
            }
            catch (Exception exception)
            {
                missing = IsMissingLocalDiskFile(path, exception);
                return null;
            }
        }

        internal static bool IsMissingLocalDiskFile(string path, Exception exception) =>
            // Keep UNC, device and relative paths conservative: a host service may see them differently.
            path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/') &&
            exception is FileNotFoundException or DirectoryNotFoundException;

        // 单次组装范围内的读取器；不缓存磁盘句柄或跨调用持有原生缓冲区。
        internal sealed class DiskSettingsReader : IDisposable
        {
            private readonly Func<IDisposable?> _createService;
            private readonly Func<IDisposable, string, (bool Success, long Max, string Type)> _readSettings;
            private IDisposable? _service;
            private bool _disposed;

            internal DiskSettingsReader() : this(() => new VirtualDiskMetadataReader(),
                (reader, path) => ReadDiskSettings((VirtualDiskMetadataReader)reader, path)) { }

            internal DiskSettingsReader(Func<IDisposable?> createService,
                Func<IDisposable, string, (bool Success, long Max, string Type)> readSettings)
            {
                _createService = createService;
                _readSettings = readSettings;
            }

            internal (long Max, string Type) Read(string path)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    _service ??= _createService();
                    if (_service == null) return (0, "Unknown");
                    var result = _readSettings(_service, path);
                    if (!result.Success) ReleaseService();
                    return (result.Max, result.Type);
                }
                catch
                {
                    ReleaseService();
                    return (0, "Unknown");
                }
            }

            private void ReleaseService()
            {
                var service = _service;
                _service = null;
                // Cleanup failure must not replace the existing per-disk fallback or abort the list.
                try { service?.Dispose(); } catch { }
            }

            public void Dispose()
            {
                _disposed = true;
                ReleaseService();
            }
        }

        private static (bool Success, long Max, string Type) ReadDiskSettings(VirtualDiskMetadataReader reader, string path)
        {
            try
            {
                var (max, type) = reader.ReadSettings(path);
                return (true, max, type);
            }
            catch { return (false, 0, "Unknown"); }
        }

        private async Task<ApiResponse<Dictionary<string, string>>> GetHostVideoControllerMapAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController",
                obj => new { Name = obj["Name"]?.ToString() ?? "", PnpId = obj["PNPDeviceID"]?.ToString() ?? "" },
                WmiScope.CimV2);

            if (!resp.Success) return ApiResponse<Dictionary<string, string>>.Fail(resp.Error, resp.Code,
                resp.ErrorSource, resp.Exception, resp.ErrorKind);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (resp.Data != null)
                foreach (var item in resp.Data)
                {
                    string id = ExtractPciId(item.PnpId) ?? "";
                    if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id))
                        map[id] = item.Name;
                }
            return ApiResponse<Dictionary<string, string>>.Ok(map);
        }

        // --- 私有辅助方法：进程与性能计数器 ---

        private static Task<ApiResponse<List<GpuProcessInfo>>> QueryGpuProcessesAsync()
        {
            // 同时取 vmwp（命令行带 VM GUID）与 vmmem（无命令行，靠 PPID 认亲到其 vmwp）。
            return WmiApi.QueryAsync(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process WHERE Name = 'vmwp.exe' OR Name = 'vmmem'",
                obj => new GpuProcessInfo(Convert.ToInt32(obj["ProcessId"]),
                    Convert.ToInt32(obj["ParentProcessId"]), obj["Name"]?.ToString() ?? "",
                    obj["CommandLine"]?.ToString() ?? "", obj["CreationDate"]?.ToString()),
                WmiScope.CimV2);
        }

        internal bool PublishGpuProcessSnapshot(IEnumerable<Guid> requestedIds, bool success,
            IEnumerable<GpuProcessInfo> processes, DateTime nowUtc)
        {
            if (!success) return false;
            var ids = requestedIds.Distinct().ToArray();
            var fresh = new Dictionary<Guid, int[]>();
            var all = processes.ToArray();
            var vmwps = all.Where(p => p.Name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            var vmmems = all.Where(p => p.Name.Equals("vmmem", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var id in ids)
            {
                var matches = vmwps.Where(process => IsVmWorker(process, id)).ToArray();
                if (matches.Length != 1) continue;
                var wp = matches[0];
                // Win11 uses vmwp; Win10 can report GPU usage on its vmmem child.
                fresh[id] = new[] { wp.Pid }.Concat(vmmems.Where(m => m.ParentPid == wp.Pid).Select(m => m.Pid)).Distinct().ToArray();
            }
            ProcessSnapshot = new(ids, fresh, nowUtc);
            return true;
        }

        private static bool IsVmWorker(GpuProcessInfo process, Guid vmId)
            => process.Name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase)
                && Regex.Matches(process.CommandLine, @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-(?:[0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")
                    .Any(match => Guid.TryParse(match.Value, out Guid id) && id == vmId);

        // --- 私有辅助方法：字符串解析与格式化 ---

        private string ExtractPciId(string input) =>
            string.IsNullOrEmpty(input) ? null :
            Regex.Match(input, @"(VEN_[0-9A-Z]{4}&DEV_[0-9A-Z]{4})", RegexOptions.IgnoreCase).Value.ToUpper();

        private string ExtractFirstGuid(string input) =>
            string.IsNullOrEmpty(input) ? null :
            Regex.Match(input, @"[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}").Value.ToUpper();

        private static string FormatMac(string raw) =>
            string.IsNullOrEmpty(raw) ? "" :
            Regex.Replace(raw.Replace(":", "").Replace("-", ""), "(.{2})", "$1:").TrimEnd(':').ToUpperInvariant();   // 冒号分隔，与 MacAddress.Format 统一（实时刷新与网络页一致）
    }
}
