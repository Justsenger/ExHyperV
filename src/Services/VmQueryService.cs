using ExHyperV.Api;
using ExHyperV.Models;
using ExHyperV.Tools;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace ExHyperV.Services
{
    public class VmQueryService
    {
        // --- 数据结构定义 ---

        public struct VmDynamicMemoryData { public long AssignedMb; public int AvailablePercent; }

        public struct GpuUsageData
        {
            public double Gpu3d;
            public double GpuCopy;
            public double GpuEncode;
            public double GpuDecode;
            public bool IsDriverBound;
        }

        // --- 静态变量与缓存 ---

        private static readonly Dictionary<string, string> _switchNameCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (long Current, long Max, string Type)> _diskSizeCache = new();
        private static Dictionary<Guid, int> _vmProcessIdCache = new();
        private static DateTime _processIdCacheTimestamp = DateTime.MinValue;
        private List<PerformanceCounter> _gpuCounters = new();
        private static readonly Regex GpuInstanceRegex = new Regex(@"pid_(\d+).*engtype_([a-zA-Z0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // --- WMI 查询语句常量 ---

        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QuerySettings = "SELECT ConfigurationID, VirtualSystemSubType, Version FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        private const string QueryGpuPvSettings = "SELECT InstanceID, HostResource FROM Msvm_GpuPartitionSettingData";
        private const string QueryDiskPerf = "SELECT Name, ReadBytesPersec, WriteBytesPersec FROM Win32_PerfFormattedData_Counters_HyperVVirtualStorageDevice";
        private const string QuerySwitches = "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch";
        private const string QueryGuestNetwork = "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration";

        // --- 内部数据传输对象（替代匿名类型，避免 dynamic 强转）---

        private sealed record DiskAlloc(string InstanceID, string Parent, string[] Paths, int ResourceType);
        private sealed record HvDisk(string DeviceID, int DriveNumber);
        private sealed record HostDisk(int Index, string Model, long Size, string PnpId);
        private sealed record SummaryItem(string Id, string Name, ushort State, int Cpu, double MemUsage, ulong Uptime, string Notes);
        private sealed record MemItem(string FullId, double StartupRam);
        private sealed record ConfigItem(string VmGuid, int Gen, string Ver);
        private sealed record GpuPvItem(string InstanceID, string[] HostResources);
        private sealed record PortItem(string InstanceID, string ElementName, string Address);
        private sealed record AllocItem(string InstanceID, string EnabledState, string[] HostResource);
        private sealed record SwitchItem(string Guid, string Name);
        private sealed record GuestNetItem(string InstanceID, string[] IPAddresses);
        private sealed record PerfItem(string WmiName, ulong Read, ulong Write);
        private sealed record MemRuntimeItem(string Id, VmDynamicMemoryData Data);

        // --- 核心查询方法 ---

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            const string QueryVirtualDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 OR ResourceType = 16";
            const string QueryPhysicalDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 17";

            // ── 并发发起所有查询 ──────────────────────────────────
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
                long rawMem = Convert.ToInt64(obj["MemoryUsage"] ?? 0);
                return new SummaryItem(
                    obj["Name"]?.ToString() ?? "",
                    obj["ElementName"]?.ToString() ?? "",
                    Convert.ToUInt16(obj["EnabledState"] ?? (ushort)0),
                    Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    (rawMem <= 0 || rawMem > 1048576) ? 0.0 : (double)rawMem,
                    Convert.ToUInt64(obj["UpTime"] ?? 0UL),
                    obj["Notes"]?.ToString() ?? ""
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
                    gen,
                    obj["Version"]?.ToString() ?? "0.0"
                );
            }, WmiScope.HyperV);

            var gpuPvTask = WmiApi.QueryAsync(QueryGpuPvSettings, obj => new GpuPvItem(
                obj["InstanceID"]?.ToString() ?? "",
                obj["HostResource"] as string[] ?? Array.Empty<string>()
            ), WmiScope.HyperV);

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
                allPortsTask, allAllocsTask, allSwitchesTask, guestNetTask);

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
            var pciFriendlyNames = pciMapTask.Result;

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

            var guestIpMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in guestNet)
            {
                string key = item.InstanceID.Split('\\').LastOrDefault() ?? "";
                if (!string.IsNullOrEmpty(key))
                    guestIpMap[key] = item.IPAddresses.ToList();
            }

            var gpuMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in gpuPvList)
            {
                string guid = ExtractFirstGuid(setting.InstanceID) ?? "";
                if (!string.IsNullOrEmpty(guid) && setting.HostResources.Length > 0)
                {
                    string pciId = ExtractPciId(setting.HostResources[0]) ?? "";
                    if (!string.IsNullOrEmpty(pciId) && pciFriendlyNames.TryGetValue(pciId, out var gpuName))
                        gpuMap[guid] = gpuName;
                }
            }

            foreach (var sw in allSwitches)
                if (!string.IsNullOrEmpty(sw.Guid))
                    _switchNameCache[sw.Guid] = sw.Name;

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
                    if (adapter.IsConnected && alloc.HostResource.Length > 0)
                    {
                        string swGuid = alloc.HostResource[0].Split('"').Reverse().Skip(1).FirstOrDefault() ?? "";
                        if (!string.IsNullOrEmpty(swGuid) && _switchNameCache.TryGetValue(swGuid, out var sName))
                            adapter.SwitchName = sName;
                    }
                }

                string devKey = port.InstanceID.Split('\\').LastOrDefault() ?? "";
                if (!string.IsNullOrEmpty(devKey) && guestIpMap.TryGetValue(devKey, out var ips))
                    adapter.IpAddresses = ips;

                if (!vmAdaptersMap.ContainsKey(vmGuid))
                    vmAdaptersMap[vmGuid] = new List<VmNetworkAdapter>();
                vmAdaptersMap[vmGuid].Add(adapter);
            }

            // ── 组装 VmInstanceInfo 列表 ──────────────────────────
            var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);
            var resultList = new List<VmInstanceInfo>();

            foreach (var s in summaries)
            {
                string vmGuidKey = s.Id.Trim('{', '}').ToUpper();
                Guid.TryParse(s.Id, out var vmGuid);
                var vmInfo = new VmInstanceInfo(vmGuid, s.Name);

                if (vmAdaptersMap.TryGetValue(vmGuidKey, out var adapters))
                {
                    foreach (var a in adapters) vmInfo.NetworkAdapters.Add(a);
                    vmInfo.MacAddress = adapters.FirstOrDefault()?.MacAddress ?? "00-00-00-00-00-00";
                    vmInfo.IpAddress = adapters
                        .SelectMany(a => a.IpAddresses ?? Enumerable.Empty<string>())
                        .FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip) && !ip.Contains(':'))
                        ?? "---";
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
                        var devMatch = deviceIdRegex.Match(pathRaw);
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
                            vmInfo.Disks.Add(new VmDiskDetails
                            {
                                Name = hostInfo.Model,
                                Path = $"PhysicalDrive{dNum}",
                                CurrentSize = hostInfo.Size,
                                MaxSize = hostInfo.Size,
                                DiskType = "Physical",
                                PnpDeviceId = hostInfo.PnpId
                            });
                        }
                    }
                    else if (cleanPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        long size = 0;
                        try { if (File.Exists(cleanPath)) size = new FileInfo(cleanPath).Length; } catch { }
                        vmInfo.Disks.Add(new VmDiskDetails
                        {
                            Name = Path.GetFileName(cleanPath),
                            Path = cleanPath,
                            CurrentSize = size,
                            MaxSize = size,
                            DiskType = "ISO"
                        });
                    }
                    else
                    {
                        var (current, max, diskType) = GetDiskSizes(cleanPath);
                        vmInfo.Disks.Add(new VmDiskDetails
                        {
                            Name = Path.GetFileName(cleanPath),
                            Path = cleanPath,
                            CurrentSize = current,
                            MaxSize = max > 0 ? max : current,
                            DiskType = diskType
                        });
                    }
                }

                double startupRam = memList
                    .FirstOrDefault(m => m.FullId.Contains(s.Id, StringComparison.OrdinalIgnoreCase))
                    ?.StartupRam ?? 0.0;

                if (configMap.TryGetValue(vmGuidKey, out var config))
                {
                    vmInfo.Generation = config.Gen;
                    vmInfo.Version = config.Ver;
                }

                vmInfo.OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows";
                vmInfo.CpuCount = s.Cpu;
                vmInfo.MemoryGb = Math.Round(startupRam / 1024.0, 1);
                vmInfo.AssignedMemoryGb = Math.Round((s.MemUsage > 0 ? s.MemUsage : startupRam) / 1024.0, 1);
                vmInfo.Notes = s.Notes;
                vmInfo.GpuName = gpuMap.TryGetValue(vmGuidKey, out var gName) ? gName : null;
                vmInfo.SyncBackendData(VmMapper.MapStateCodeToText(s.State), TimeSpan.FromMilliseconds(s.Uptime));
                resultList.Add(vmInfo);
            }

            return resultList.OrderByDescending(x => x.IsRunning).ThenBy(x => x.Name).ToList();
        }

        // --- 性能监控相关方法 ---

        public async Task UpdateDiskPerformanceAsync(IEnumerable<VmInstanceInfo> vms)
        {
            try
            {
                var perfResp = await WmiApi.QueryAsync(QueryDiskPerf, obj => new PerfItem(
                    obj["Name"]?.ToString() ?? "",
                    Convert.ToUInt64(obj["ReadBytesPersec"] ?? 0UL),
                    Convert.ToUInt64(obj["WriteBytesPersec"] ?? 0UL)
                ), WmiScope.CimV2);

                var perfData = perfResp.Data;
                if (perfData == null || !perfData.Any()) return;

                string Clean(string s) => Regex.Replace(s ?? "", @"[\\_\-\s\&\?]", "").ToUpperInvariant();
                var processedPerf = perfData.Select(p => new { Data = p, Cleaned = Clean(p.WmiName) }).ToList();

                foreach (var vm in vms)
                {
                    if (!vm.IsRunning) continue;
                    foreach (var disk in vm.Disks)
                    {
                        disk.ReadSpeedBps = 0;
                        disk.WriteSpeedBps = 0;
                        string target = disk.DiskType == "Physical"
                            ? Clean(disk.PnpDeviceId)
                            : Clean(Path.GetFileName(disk.Path));
                        if (string.IsNullOrEmpty(target)) continue;
                        var match = processedPerf.FirstOrDefault(p => p.Cleaned.Contains(target));
                        if (match != null)
                        {
                            disk.ReadSpeedBps = (long)match.Data.Read;
                            disk.WriteSpeedBps = (long)match.Data.Write;
                        }
                    }
                }
            }
            catch { }
        }

        // GetGpuPerformanceAsync 使用 PerformanceCounter（.NET 原生 API），不需要迁移
        public async Task<Dictionary<Guid, GpuUsageData>> GetGpuPerformanceAsync(IEnumerable<VmInstanceInfo> vms)
        {
            var results = new Dictionary<Guid, GpuUsageData>();
            var gpuVms = vms.Where(vm => vm.IsRunning && vm.HasGpu).ToList();
            if (!gpuVms.Any()) return results;

            try
            {
                bool pidRefreshed = false;
                if ((DateTime.Now - _processIdCacheTimestamp).TotalSeconds > 5)
                {
                    await RefreshVmPidCache(gpuVms);
                    pidRefreshed = true;
                }
                if (!_vmProcessIdCache.Any()) return results;
                if (pidRefreshed || !_gpuCounters.Any()) RebuildGpuCounters();

                string[] allGpuInstances = Array.Empty<string>();
                try
                {
                    var category = new PerformanceCounterCategory("GPU Engine");
                    allGpuInstances = category.GetInstanceNames();
                }
                catch { }

                var usageByPid = new Dictionary<int, GpuUsageData>();
                foreach (var pair in _vmProcessIdCache)
                {
                    int pid = pair.Value;
                    bool isBound = allGpuInstances.Any(n => n.Contains($"pid_{pid}_", StringComparison.OrdinalIgnoreCase));
                    usageByPid[pid] = new GpuUsageData { IsDriverBound = isBound };
                }

                foreach (var counter in _gpuCounters.ToList())
                {
                    try
                    {
                        var m = GpuInstanceRegex.Match(counter.InstanceName);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int pid) && usageByPid.ContainsKey(pid))
                        {
                            float val = counter.NextValue();
                            var d = usageByPid[pid];
                            string type = m.Groups[2].Value.ToUpper();
                            if (type.Contains("3D")) d.Gpu3d += val;
                            else if (type.Contains("COPY")) d.GpuCopy += val;
                            else if (type.Contains("ENCODE")) d.GpuEncode += val;
                            else if (type.Contains("DECODE")) d.GpuDecode += val;
                            usageByPid[pid] = d;
                        }
                    }
                    catch
                    {
                        _gpuCounters.Remove(counter);
                        counter.Dispose();
                    }
                }

                foreach (var vm in gpuVms)
                    if (_vmProcessIdCache.TryGetValue(vm.Id, out int pid))
                        results[vm.Id] = usageByPid[pid];
            }
            catch { }

            return results;
        }

        public async Task<Dictionary<string, VmDynamicMemoryData>> GetVmRuntimeMemoryDataAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT Name, MemoryUsage, MemoryAvailable FROM Msvm_SummaryInformation",
                obj => {
                    long usage = Convert.ToInt64(obj["MemoryUsage"] ?? 0);
                    return new MemRuntimeItem(
                        obj["Name"]?.ToString() ?? "",
                        new VmDynamicMemoryData
                        {
                            AssignedMb = (usage < 0 || usage > 1048576) ? 0 : usage,
                            AvailablePercent = Math.Clamp(Convert.ToInt32(obj["MemoryAvailable"] ?? 0), 0, 100)
                        });
                }, WmiScope.HyperV);

            return (resp.Data ?? new List<MemRuntimeItem>())
                .Where(x => !string.IsNullOrEmpty(x.Id))
                .ToDictionary(x => x.Id, x => x.Data);
        }

        // --- 虚拟机设置修改方法 ---

        public async Task<bool> SetVmNotesAsync(string vmName, string newNotes)
        {
            try
            {
                var result = await WmiApi.WithObjectAsync(
                    wql: $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    modifier: obj => obj["Notes"] = new string[] { newNotes ?? string.Empty },
                    submitMethod: "ModifySystemSettings",
                    submitParamName: "SystemSettings",
                    wrapInArray: false,
                    scope: WmiScope.HyperV,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService");
                return result.Success;
            }
            catch { return false; }
        }

        public async Task<bool> SetVmOsTypeAsync(string vmName, string osType)
        {
            try
            {
                var currentResp = await WmiApi.QueryFirstAsync(
                    $"SELECT Notes FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => obj["Notes"] is string[] arr ? string.Join("\n", arr) : obj["Notes"]?.ToString() ?? "",
                    WmiScope.HyperV);

                string oldNotes = currentResp.Data ?? "";
                string newNotes = Utils.UpdateTagValue(oldNotes, "OSType", osType);
                if (oldNotes == newNotes) return true;

                var result = await WmiApi.WithObjectAsync(
                    wql: $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    modifier: obj => obj["Notes"] = new string[] { newNotes },
                    submitMethod: "ModifySystemSettings",
                    submitParamName: "SystemSettings",
                    wrapInArray: false,
                    scope: WmiScope.HyperV,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService");
                return result.Success;
            }
            catch { return false; }
        }


        public async Task<string> GetVmStateAsync(string vmName)
        {
            var r = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["EnabledState"]?.ToString() ?? "NotFound");
            return r.Data ?? "NotFound";
        }

        public async Task<(bool IsOff, string CurrentState)> IsVmPoweredOffAsync(string vmName)
        {
            var r = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["EnabledState"]?.ToString() ?? "Unknown");
            string state = r.Data ?? "Unknown";
            return (state == "3", state);
        }
        // --- 私有辅助方法：磁盘与硬件映射 ---

        private (long Current, long Max, string DiskType) GetDiskSizes(string path)
        {
            if (string.IsNullOrEmpty(path)) return (0, 0, "Unknown");
            if (_diskSizeCache.TryGetValue(path, out var cached)) return cached;

            long currentSize = 0;
            try { var fi = new FileInfo(path); if (fi.Exists) currentSize = fi.Length; } catch { }

            long maxSize = 0;
            string diskType = "Unknown";
            try
            {
                using var svcForScope = WmiApi.GetVirtualSystemManagementService();
                using var searcher = new ManagementObjectSearcher(
                    svcForScope.Scope,
                    new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
                using var service = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (service != null)
                {
                    using var inParams = service.GetMethodParameters("GetVirtualHardDiskSettingData");
                    inParams["Path"] = path;
                    using var outParams = service.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
                    if ((uint)(outParams["ReturnValue"] ?? 1u) == 0u)
                    {
                        string xml = outParams["SettingData"]?.ToString() ?? "";
                        var tM = Regex.Match(xml, @"<PROPERTY NAME=""Type"" TYPE=""uint16""><VALUE>(\d+)</VALUE>");
                        var sM = Regex.Match(xml, @"<PROPERTY NAME=""MaxInternalSize"" TYPE=""uint64""><VALUE>(\d+)</VALUE>");
                        if (tM.Success) diskType = tM.Groups[1].Value switch { "2" => "Fixed", "3" => "Dynamic", "4" => "Differencing", _ => "Unknown" };
                        if (sM.Success) maxSize = long.Parse(sM.Groups[1].Value);
                    }
                }
            }
            catch { }

            var result = (currentSize, maxSize > 0 ? maxSize : currentSize, diskType == "Unknown" ? "Dynamic" : diskType);
            if (result.Item2 > 0) _diskSizeCache[path] = result;
            return result;
        }

        private async Task<Dictionary<string, string>> GetHostVideoControllerMapAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController",
                obj => new { Name = obj["Name"]?.ToString() ?? "", PnpId = obj["PNPDeviceID"]?.ToString() ?? "" },
                WmiScope.CimV2);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (resp.Data != null)
                foreach (var item in resp.Data)
                {
                    string id = ExtractPciId(item.PnpId) ?? "";
                    if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id))
                        map[id] = item.Name;
                }
            return map;
        }

        // --- 私有辅助方法：进程与性能计数器 ---

        private async Task RefreshVmPidCache(List<VmInstanceInfo> runningGpuVms)
        {
            _vmProcessIdCache.Clear();
            var resp = await WmiApi.QueryAsync(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'vmwp.exe'",
                obj => new { Pid = Convert.ToInt32(obj["ProcessId"]), Cmd = obj["CommandLine"]?.ToString() ?? "" },
                WmiScope.CimV2);

            if (resp.Data != null)
                foreach (var vm in runningGpuVms)
                {
                    var proc = resp.Data
                        .FirstOrDefault(p => p.Cmd.Contains(vm.Id.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (proc != null) _vmProcessIdCache[vm.Id] = proc.Pid;
                }
            _processIdCacheTimestamp = DateTime.Now;
        }

        private void RebuildGpuCounters()
        {
            try
            {
                foreach (var c in _gpuCounters) c.Dispose();
                _gpuCounters.Clear();
                if (!PerformanceCounterCategory.Exists("GPU Engine")) return;
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                var targets = _vmProcessIdCache.Values.Select(p => $"pid_{p}_").ToList();
                foreach (var name in instances)
                {
                    if (targets.Any(t => name.StartsWith(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                            pc.NextValue();
                            _gpuCounters.Add(pc);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // --- 私有辅助方法：字符串解析与格式化 ---

        private string ExtractPciId(string input) =>
            string.IsNullOrEmpty(input) ? null :
            Regex.Match(input, @"(VEN_[0-9A-Z]{4}&DEV_[0-9A-Z]{4})", RegexOptions.IgnoreCase).Value.ToUpper();

        private string ExtractFirstGuid(string input) =>
            string.IsNullOrEmpty(input) ? null :
            Regex.Match(input, @"[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}").Value.ToUpper();

        private static string FormatMac(string raw) =>
            string.IsNullOrEmpty(raw) ? "" :
            Regex.Replace(raw.Replace(":", "").Replace("-", ""), "(.{2})", "$1-").TrimEnd('-').ToUpperInvariant();
    }
}