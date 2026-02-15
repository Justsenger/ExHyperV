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
        // --- 1. 必须保留的基础定义 (之前被错误删除了) ---
        public struct VmDynamicMemoryData { public long AssignedMb; public int AvailablePercent; }

        public struct GpuUsageData
        {
            public double Gpu3d;
            public double GpuCopy;
            public double GpuEncode;
            public double GpuDecode;
        }

        private static readonly Dictionary<string, string> _switchNameCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (long Current, long Max, string Type)> _diskSizeCache = new();

        // GPU 性能相关私有变量
        private static Dictionary<Guid, int> _vmProcessIdCache = new();
        private static DateTime _processIdCacheTimestamp = DateTime.MinValue;
        private List<PerformanceCounter> _gpuCounters = new();
        private static readonly Regex GpuInstanceRegex = new Regex(@"pid_(\d+).*engtype_([a-zA-Z0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // WMI 查询语句
        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QuerySettings = "SELECT ConfigurationID, VirtualSystemSubType, Version FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        private const string QueryGpuPvSettings = "SELECT InstanceID, HostResource FROM Msvm_GpuPartitionSettingData";
        private const string QueryPartitionableGpus = "SELECT Name FROM Msvm_PartitionableGpu";
        private const string QueryDiskPerf = "SELECT Name, ReadBytesPersec, WriteBytesPersec FROM Win32_PerfFormattedData_Counters_HyperVVirtualStorageDevice";
        private const string QuerySwitches = "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch";
        private const string QueryGuestNetwork = "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration";

        // --- 2. 虚拟机列表查询 (整合了存储修复逻辑) ---
        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            // 虚拟磁盘和物理磁盘分配查询
            const string QueryVirtualDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 OR ResourceType = 16";
            const string QueryPhysicalDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 17";

            var vDiskTask = WmiTools.QueryAsync(QueryVirtualDiskAllocations, obj => new {
                InstanceID = obj["InstanceID"]?.ToString() ?? "",
                Parent = obj["Parent"]?.ToString() ?? "",
                Paths = obj["HostResource"] as string[] ?? (obj["HostResource"] is string s ? new[] { s } : new string[0]),
                ResourceType = Convert.ToInt32(obj["ResourceType"] ?? 0)
            });

            var pDiskTask = WmiTools.QueryAsync(QueryPhysicalDiskAllocations, obj => new {
                InstanceID = obj["InstanceID"]?.ToString() ?? "",
                Parent = obj["Parent"]?.ToString() ?? "",
                Paths = obj["HostResource"] as string[] ?? (obj["HostResource"] is string s ? new[] { s } : new string[0]),
                ResourceType = Convert.ToInt32(obj["ResourceType"] ?? 0)
            });

            var hvDiskTask = WmiTools.QueryAsync("SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber IS NOT NULL", obj => new {
                DeviceID = obj["DeviceID"]?.ToString() ?? "",
                DriveNumber = Convert.ToInt32(obj["DriveNumber"] ?? -1)
            });

            var hostDiskTask = WmiTools.QueryAsync("SELECT Index, Model, Size, SerialNumber, PNPDeviceID FROM Win32_DiskDrive", obj => new {
                Index = Convert.ToInt32(obj["Index"] ?? -1),
                Model = obj["Model"]?.ToString(),
                Size = Convert.ToInt64(obj["Size"] ?? 0),
                PnpId = obj["PNPDeviceID"]?.ToString()
            }, WmiTools.CimV2Scope);

            var summaryTask = WmiTools.QueryAsync(QuerySummary, obj => {
                long rawMem = Convert.ToInt64(obj["MemoryUsage"] ?? 0);
                return new
                {
                    Id = obj["Name"]?.ToString(),
                    Name = obj["ElementName"]?.ToString(),
                    State = (ushort)(obj["EnabledState"] ?? 0),
                    Cpu = Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    MemUsage = (rawMem <= 0 || rawMem > 1048576) ? 0 : (double)rawMem,
                    Uptime = (ulong)(obj["UpTime"] ?? 0),
                    Notes = obj["Notes"]?.ToString() ?? string.Empty
                };
            });

            var memTask = WmiTools.QueryAsync(QueryMemSettings, obj => new {
                FullId = obj["InstanceID"]?.ToString(),
                StartupRam = Convert.ToDouble(obj["VirtualQuantity"] ?? 0)
            });

            var configTask = WmiTools.QueryAsync(QuerySettings, obj => {
                string subType = obj["VirtualSystemSubType"]?.ToString() ?? "";
                int gen = subType.EndsWith(":1") ? 1 : (subType.EndsWith(":2") ? 2 : 0);
                return new { VmGuid = obj["ConfigurationID"]?.ToString()?.Trim('{', '}').ToUpper(), Gen = gen, Ver = obj["Version"]?.ToString() ?? "0.0" };
            });

            var gpuPvTask = WmiTools.QueryAsync(QueryGpuPvSettings, obj => new { InstanceID = obj["InstanceID"]?.ToString(), HostResources = obj["HostResource"] as string[] });
            var pciMapTask = GetHostVideoControllerMapAsync();
            var allPortsTask = WmiTools.QueryAsync("SELECT ElementName, InstanceID, Address FROM Msvm_SyntheticEthernetPortSettingData", (o) => (ManagementObject)o);
            var allAllocsTask = WmiTools.QueryAsync("SELECT EnabledState, InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData", (o) => (ManagementObject)o);
            var allSwitchesTask = WmiTools.QueryAsync(QuerySwitches, obj => new { Guid = obj["Name"]?.ToString(), Name = obj["ElementName"]?.ToString() });
            var guestNetTask = WmiTools.QueryAsync(QueryGuestNetwork, obj => new { InstanceID = obj["InstanceID"]?.ToString(), IPAddresses = obj["IPAddresses"] as string[] });

            await Task.WhenAll(vDiskTask, pDiskTask, hvDiskTask, hostDiskTask, summaryTask, memTask, configTask, gpuPvTask, pciMapTask, allPortsTask, allAllocsTask, allSwitchesTask, guestNetTask);

            return await Task.Run(() =>
            {
                var summaries = summaryTask.Result;
                var hvDiskMap = hvDiskTask.Result.Where(d => !string.IsNullOrEmpty(d.DeviceID)).ToDictionary(d => d.DeviceID.Replace("\\\\", "\\"), d => d.DriveNumber, StringComparer.OrdinalIgnoreCase);
                var osDiskMap = hostDiskTask.Result.Where(d => d.Index >= 0).ToDictionary(d => d.Index, d => d);
                var configMap = configTask.Result.Where(x => !string.IsNullOrEmpty(x.VmGuid)).ToDictionary(x => x.VmGuid, x => new { x.Gen, x.Ver }, StringComparer.OrdinalIgnoreCase);

                var guestIpMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in guestNetTask.Result)
                {
                    string key = item.InstanceID?.Split('\\').LastOrDefault();
                    if (key != null && item.IPAddresses != null) guestIpMap[key] = item.IPAddresses.ToList();
                }

                var gpuMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var pciFriendlyNames = pciMapTask.Result;
                foreach (var setting in gpuPvTask.Result)
                {
                    string guid = ExtractFirstGuid(setting.InstanceID);
                    if (guid != null && setting.HostResources?.Length > 0)
                    {
                        string pciId = ExtractPciId(setting.HostResources[0]);
                        if (pciId != null && pciFriendlyNames.TryGetValue(pciId, out var name)) gpuMap[guid] = name;
                    }
                }

                foreach (var sw in allSwitchesTask.Result) if (!string.IsNullOrEmpty(sw.Guid)) _switchNameCache[sw.Guid] = sw.Name;

                var allocsMap = allAllocsTask.Result.GroupBy(a => {
                    string id = a["InstanceID"]?.ToString() ?? "";
                    int idx = id.LastIndexOf('\\');
                    return idx > 0 ? id.Substring(0, idx) : id;
                }, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var vmAdaptersMap = new Dictionary<string, List<VmNetworkAdapter>>(StringComparer.OrdinalIgnoreCase);
                foreach (var port in allPortsTask.Result)
                {
                    string fullId = port["InstanceID"]?.ToString() ?? "";
                    string vmGuid = ExtractFirstGuid(fullId);
                    if (string.IsNullOrEmpty(vmGuid)) continue;
                    var adapter = new VmNetworkAdapter { Id = fullId, Name = port["ElementName"]?.ToString(), MacAddress = FormatMac(port["Address"]?.ToString()) };
                    if (allocsMap.TryGetValue(fullId, out var alloc))
                    {
                        adapter.IsConnected = alloc["EnabledState"]?.ToString() == "2";
                        if (adapter.IsConnected && alloc["HostResource"] is string[] hr && hr.Length > 0)
                        {
                            string swGuid = hr[0].Split('"').Reverse().Skip(1).FirstOrDefault();
                            if (swGuid != null && _switchNameCache.TryGetValue(swGuid, out var sName)) adapter.SwitchName = sName;
                        }
                    }
                    string devKey = fullId.Split('\\').LastOrDefault();
                    if (devKey != null && guestIpMap.TryGetValue(devKey, out var ips)) adapter.IpAddresses = ips;
                    if (!vmAdaptersMap.ContainsKey(vmGuid)) vmAdaptersMap[vmGuid] = new List<VmNetworkAdapter>();
                    vmAdaptersMap[vmGuid].Add(adapter);
                }

                var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);
                var resultList = new List<VmInstanceInfo>();

                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    string vmGuidKey = s.Id?.Trim('{', '}').ToUpper();
                    var vmInfo = new VmInstanceInfo(vmId, s.Name);

                    if (vmGuidKey != null && vmAdaptersMap.TryGetValue(vmGuidKey, out var adapters))
                    {
                        foreach (var a in adapters) vmInfo.NetworkAdapters.Add(a);
                        vmInfo.MacAddress = adapters.FirstOrDefault()?.MacAddress ?? "00-00-00-00-00-00";
                        vmInfo.IpAddress = adapters.SelectMany(a => a.IpAddresses ?? Enumerable.Empty<string>()).FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip) && !ip.Contains(':')) ?? "---";
                    }

                    // 磁盘合并处理
                    var allDiskResources = vDiskTask.Result.Concat(pDiskTask.Result)
                        .Where(d => (d.Parent?.ToUpper().Contains(vmGuidKey) == true) || (d.InstanceID?.ToUpper().Contains(vmGuidKey) == true))
                        .ToList();

                    foreach (var d in allDiskResources)
                    {
                        if (d.Paths == null || d.Paths.Length == 0) continue;
                        string pathRaw = d.Paths[0];
                        string cleanPath = pathRaw.Replace("\"", "").Trim();
                        if (string.IsNullOrEmpty(cleanPath)) continue;

                        bool isPhysical = cleanPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) || cleanPath.ToUpper().Contains("PHYSICALDRIVE");

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
                                vmInfo.Disks.Add(new VmDiskDetails { Name = hostInfo.Model ?? $"PhysicalDrive{dNum}", Path = $"PhysicalDrive{dNum}", CurrentSize = hostInfo.Size, MaxSize = hostInfo.Size, DiskType = "Physical", PnpDeviceId = hostInfo.PnpId });
                            }
                        }
                        else if (cleanPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                        {
                            long size = 0; try { if (File.Exists(cleanPath)) size = new FileInfo(cleanPath).Length; } catch { }
                            vmInfo.Disks.Add(new VmDiskDetails { Name = Path.GetFileName(cleanPath), Path = cleanPath, CurrentSize = size, MaxSize = size, DiskType = "ISO" });
                        }
                        else
                        {
                            var (current, max, diskType) = GetDiskSizes(cleanPath);
                            vmInfo.Disks.Add(new VmDiskDetails { Name = Path.GetFileName(cleanPath), Path = cleanPath, CurrentSize = current, MaxSize = max > 0 ? max : current, DiskType = diskType });
                        }
                    }

                    double startupRam = memTask.Result.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0;
                    if (vmGuidKey != null && configMap.TryGetValue(vmGuidKey, out var config)) { vmInfo.Generation = config.Gen; vmInfo.Version = config.Ver; }
                    vmInfo.OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows";
                    vmInfo.CpuCount = s.Cpu;
                    vmInfo.MemoryGb = Math.Round(startupRam / 1024.0, 1);
                    vmInfo.AssignedMemoryGb = Math.Round(((s.MemUsage > 0) ? s.MemUsage : startupRam) / 1024.0, 1);
                    vmInfo.Notes = s.Notes;
                    vmInfo.GpuName = (vmGuidKey != null && gpuMap.TryGetValue(vmGuidKey, out var gName)) ? gName : null;
                    vmInfo.SyncBackendData(VmMapper.MapStateCodeToText(s.State), TimeSpan.FromMilliseconds(s.Uptime));
                    resultList.Add(vmInfo);
                }
                return resultList.OrderByDescending(x => x.IsRunning).ThenBy(x => x.Name).ToList();
            });
        }

        // --- 3. 磁盘性能与大小 (保持修复后的版本) ---
        public async Task UpdateDiskPerformanceAsync(IEnumerable<VmInstanceInfo> vms)
        {
            try
            {
                var perfData = await WmiTools.QueryAsync(QueryDiskPerf, obj => new { WmiName = obj["Name"]?.ToString() ?? "", Read = Convert.ToUInt64(obj["ReadBytesPersec"] ?? 0), Write = Convert.ToUInt64(obj["WriteBytesPersec"] ?? 0) }, WmiTools.CimV2Scope);
                if (perfData == null || !perfData.Any()) return;
                string Clean(string s) => Regex.Replace(s ?? "", @"[\\_\-\s\&\?]", "").ToUpperInvariant();
                var processedPerf = perfData.Select(p => new { Data = p, Cleaned = Clean(p.WmiName) }).ToList();

                foreach (var vm in vms)
                {
                    if (!vm.IsRunning) continue;
                    foreach (var disk in vm.Disks)
                    {
                        disk.ReadSpeedBps = 0; disk.WriteSpeedBps = 0;
                        string target = disk.DiskType == "Physical" ? Clean(disk.PnpDeviceId) : Clean(Path.GetFileName(disk.Path));
                        if (string.IsNullOrEmpty(target)) continue;
                        var match = processedPerf.FirstOrDefault(p => p.Cleaned.Contains(target));
                        if (match != null) { disk.ReadSpeedBps = (long)match.Data.Read; disk.WriteSpeedBps = (long)match.Data.Write; }
                    }
                }
            }
            catch { }
        }

        private (long Current, long Max, string DiskType) GetDiskSizes(string path)
        {
            if (string.IsNullOrEmpty(path)) return (0, 0, "Unknown");
            if (_diskSizeCache.TryGetValue(path, out var cached)) return cached;
            long currentSize = 0; try { var fi = new FileInfo(path); if (fi.Exists) currentSize = fi.Length; } catch { }
            long maxSize = 0; string diskType = "Unknown";
            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\virtualization\v2"); scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
                using var service = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (service != null)
                {
                    using var inParams = service.GetMethodParameters("GetVirtualHardDiskSettingData"); inParams["Path"] = path;
                    using var outParams = service.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
                    if ((uint)(outParams["ReturnValue"] ?? 1) == 0)
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

        // --- 4. GPU 性能逻辑 (修复了删除变量导致的报错，补全了逻辑) ---
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

                var usageByPid = _vmProcessIdCache.Values.ToDictionary(p => p, p => new GpuUsageData());
                foreach (var counter in _gpuCounters.ToList())
                {
                    try
                    {
                        var m = GpuInstanceRegex.Match(counter.InstanceName);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int pid) && usageByPid.ContainsKey(pid))
                        {
                            string type = m.Groups[2].Value.ToUpper();
                            float val = counter.NextValue();
                            var d = usageByPid[pid];
                            if (type.Contains("3D")) d.Gpu3d += val;
                            else if (type.Contains("COPY")) d.GpuCopy += val;
                            else if (type.Contains("ENCODE")) d.GpuEncode += val;
                            else if (type.Contains("DECODE")) d.GpuDecode += val;
                            usageByPid[pid] = d;
                        }
                    }
                    catch { _gpuCounters.Remove(counter); counter.Dispose(); }
                }
                foreach (var vm in gpuVms) if (_vmProcessIdCache.TryGetValue(vm.Id, out int pid)) results[vm.Id] = usageByPid[pid];
            }
            catch { }
            return results;
        }

        private async Task RefreshVmPidCache(List<VmInstanceInfo> runningGpuVms)
        {
            _vmProcessIdCache.Clear();
            var processes = await WmiTools.QueryAsync("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'vmwp.exe'", obj => new { Pid = Convert.ToInt32(obj["ProcessId"]), Cmd = obj["CommandLine"]?.ToString() ?? "" }, WmiTools.CimV2Scope);
            foreach (var vm in runningGpuVms)
            {
                var proc = processes.FirstOrDefault(p => p.Cmd.Contains(vm.Id.ToString(), StringComparison.OrdinalIgnoreCase));
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
                        try { var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true); pc.NextValue(); _gpuCounters.Add(pc); } catch { }
                    }
                }
            }
            catch { }
        }

        // --- 5. 其他辅助方法 ---
        public async Task<bool> SetVmOsTypeAsync(string vmName, string osType)
        {
            try
            {
                string safeName = vmName.Replace("'", "''");
                var settingsList = await WmiTools.QueryAsync($"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{safeName}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'", o => o);
                var settings = settingsList.FirstOrDefault();
                if (settings == null) return false;
                string oldNotes = (settings["Notes"] is string[] arr && arr.Length > 0) ? string.Join("\n", arr) : "";
                string newNotes = Utils.UpdateTagValue(oldNotes, "OSType", osType);
                if (oldNotes == newNotes) return true;
                settings["Notes"] = new string[] { newNotes };
                var result = await WmiTools.ExecuteMethodAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "ModifySystemSettings", new Dictionary<string, object> { { "SystemSettings", settings.GetText(TextFormat.CimDtd20) } });
                return result.Success;
            }
            catch { return false; }
        }

        public async Task<Dictionary<string, VmDynamicMemoryData>> GetVmRuntimeMemoryDataAsync()
        {
            var list = await WmiTools.QueryAsync("SELECT Name, MemoryUsage, MemoryAvailable FROM Msvm_SummaryInformation", item => {
                long usage = Convert.ToInt64(item["MemoryUsage"] ?? 0);
                return new { Id = item["Name"]?.ToString(), Data = new VmDynamicMemoryData { AssignedMb = (usage < 0 || usage > 1048576) ? 0 : usage, AvailablePercent = Math.Clamp(Convert.ToInt32(item["MemoryAvailable"] ?? 0), 0, 100) } };
            });
            return list.Where(x => x.Id != null).ToDictionary(x => x.Id, x => x.Data);
        }

        private async Task<Dictionary<string, string>> GetHostVideoControllerMapAsync()
        {
            var res = await WmiTools.QueryAsync("SELECT Name, PNPDeviceID FROM Win32_VideoController", i => new { Name = i["Name"]?.ToString(), PnpId = i["PNPDeviceID"]?.ToString() }, WmiTools.CimV2Scope);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in res) { string id = ExtractPciId(item.PnpId); if (id != null && !map.ContainsKey(id)) map[id] = item.Name; }
            return map;
        }

        private string ExtractPciId(string input) => string.IsNullOrEmpty(input) ? null : Regex.Match(input, @"(VEN_[0-9A-Z]{4}&DEV_[0-9A-Z]{4})", RegexOptions.IgnoreCase).Value.ToUpper();
        private string ExtractFirstGuid(string input) => string.IsNullOrEmpty(input) ? null : Regex.Match(input, @"[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}").Value.ToUpper();
        private static string FormatMac(string raw) => string.IsNullOrEmpty(raw) ? "" : Regex.Replace(raw.Replace(":", "").Replace("-", ""), "(.{2})", "$1-").TrimEnd('-').ToUpperInvariant();
    }
}