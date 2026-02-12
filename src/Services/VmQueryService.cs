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
        public struct VmDynamicMemoryData { public long AssignedMb; public int AvailablePercent; }

        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QueryDiskAllocations = "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 OR ResourceType = 16";
        private const string QuerySettings = "SELECT ConfigurationID, VirtualSystemSubType, Version FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        private const string QueryGpuPvSettings = "SELECT InstanceID, HostResource FROM Msvm_GpuPartitionSettingData";
        private const string QueryPartitionableGpus = "SELECT Name FROM Msvm_PartitionableGpu";
        private const string QueryDiskPerf = "SELECT Name, ReadBytesPersec, WriteBytesPersec FROM Win32_PerfFormattedData_Counters_HyperVVirtualStorageDevice";
        private const string QueryNetwork = "SELECT InstanceID, Address FROM Msvm_SyntheticEthernetPortSettingData";
        // --- 替换/新增的网络相关查询 ---
        private const string QueryNetworkSettings = "SELECT InstanceID, ElementName, Address FROM Msvm_SyntheticEthernetPortSettingData";
        private const string QueryNetworkAllocations = "SELECT Parent, EnabledState, HostResource FROM Msvm_EthernetPortAllocationSettingData";
        private const string QuerySwitches = "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch";
        private static readonly Dictionary<string, (long Current, long Max, string Type)> _diskSizeCache = new();

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            // 1. 基础资源查询任务
            var diskTask = WmiTools.QueryAsync(QueryDiskAllocations, obj => new {
                InstanceID = obj["InstanceID"]?.ToString() ?? "",
                Parent = obj["Parent"]?.ToString() ?? "",
                Paths = obj["HostResource"] as string[],
                ResourceType = Convert.ToInt32(obj["ResourceType"] ?? 0)
            });

            var summaryTask = WmiTools.QueryAsync(QuerySummary, obj => {
                long rawMem = Convert.ToInt64(obj["MemoryUsage"] ?? 0);
                double validMem = (rawMem <= 0 || rawMem > 1048576) ? 0 : (double)rawMem;
                return new
                {
                    Id = obj["Name"]?.ToString(),
                    Name = obj["ElementName"]?.ToString(),
                    State = (ushort)(obj["EnabledState"] ?? 0),
                    Cpu = Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    MemUsage = validMem,
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
                string version = obj["Version"]?.ToString() ?? "0.0";
                int gen = subType.EndsWith(":1") ? 1 : (subType.EndsWith(":2") ? 2 : 0);
                return new { VmGuid = obj["ConfigurationID"]?.ToString()?.Trim('{', '}').ToUpper(), Gen = gen, Ver = version };
            });

            var gpuPvTask = WmiTools.QueryAsync(QueryGpuPvSettings, obj => new {
                InstanceID = obj["InstanceID"]?.ToString(),
                HostResources = obj["HostResource"] as string[]
            });

            var gpuListTask = WmiTools.QueryAsync(QueryPartitionableGpus, obj => obj["Name"]?.ToString());
            var pciMapTask = GetHostVideoControllerMapAsync();

            // 2. 网络基础信息查询任务 (Dashboard 专用)
            var netSettingsTask = WmiTools.QueryAsync(QueryNetworkSettings, obj => new {
                Id = obj["InstanceID"]?.ToString(),
                VmGuid = ExtractFirstGuid(obj["InstanceID"]?.ToString()),
                Name = obj["ElementName"]?.ToString(),
                Mac = obj["Address"]?.ToString()
            });

            var netAllocTask = WmiTools.QueryAsync(QueryNetworkAllocations, obj => new {
                PortPath = obj["Parent"]?.ToString(),
                IsConnected = Convert.ToUInt16(obj["EnabledState"] ?? 0) == 2,
                SwitchPath = (obj["HostResource"] as string[])?.FirstOrDefault()
            });

            var switchTask = WmiTools.QueryAsync(QuerySwitches, obj => new {
                Path = obj.Path.Path,
                Name = obj["ElementName"]?.ToString()
            });

            await Task.WhenAll(diskTask, summaryTask, memTask, configTask, gpuPvTask, gpuListTask, pciMapTask, netSettingsTask, netAllocTask, switchTask);

            return await Task.Run(() =>
            {
                var summaries = summaryTask.Result;

                // 1. 防重处理：虚拟机配置映射
                var configMap = configTask.Result
                    .Where(x => !string.IsNullOrEmpty(x.VmGuid))
                    .GroupBy(x => x.VmGuid)
                    .ToDictionary(g => g.Key, g => new { g.First().Gen, g.First().Ver }, StringComparer.OrdinalIgnoreCase);

                // 2. 防重处理：交换机名映射
                var switchMap = switchTask.Result
                    .Where(x => !string.IsNullOrEmpty(x.Path))
                    .GroupBy(x => x.Path)
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

                // 3. 防重处理：GPU 映射
                var gpuMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var hostPathToPciIdMap = gpuListTask.Result
                    .Where(x => !string.IsNullOrEmpty(x))
                    .GroupBy(x => x)
                    .ToDictionary(g => g.Key, g => ExtractPciId(g.Key), StringComparer.OrdinalIgnoreCase);

                var pciToFriendlyNameMap = pciMapTask.Result;
                foreach (var setting in gpuPvTask.Result)
                {
                    string vmGuidStr = ExtractFirstGuid(setting.InstanceID);
                    if (vmGuidStr != null && setting.HostResources?.Length > 0)
                    {
                        string hostPath = setting.HostResources[0];
                        string shortId = hostPathToPciIdMap.TryGetValue(hostPath, out var id) ? id : ExtractPciId(hostPath);
                        if (!gpuMap.ContainsKey(vmGuidStr))
                        {
                            gpuMap[vmGuidStr] = (shortId != null && pciToFriendlyNameMap.TryGetValue(shortId, out var friendly)) ? friendly : "GPU-PV Device";
                        }
                    }
                }

                // 4. 网络映射逻辑 (已自带 GroupBy，比较安全)
                var vmNetMap = netSettingsTask.Result
                    .Where(n => !string.IsNullOrEmpty(n.VmGuid))
                    .GroupJoin(netAllocTask.Result,
                        n => n.Id,
                        a => a.PortPath,
                        (n, allocs) => new { n.VmGuid, n.Name, n.Mac, Alloc = allocs.FirstOrDefault() })
                    .GroupBy(x => x.VmGuid.ToUpper())
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    string vmGuidKey = s.Id?.Trim('{', '}').ToUpper();
                    var vmInfo = new VmInstanceInfo(vmId, s.Name);

                    // --- 注入网卡列表 (Dashboard 卡片使用) ---
                    if (vmGuidKey != null && vmNetMap.TryGetValue(vmGuidKey, out var netList))
                    {
                        foreach (var n in netList)
                        {
                            var adapter = new VmNetworkAdapter
                            {
                                Id = n.Name, // Dashboard 仅做标识
                                Name = n.Name,
                                MacAddress = Regex.Replace(n.Mac ?? "", ".{2}", "$0-").TrimEnd('-'),
                                IsConnected = n.Alloc?.IsConnected ?? false,
                                SwitchName = (n.Alloc?.SwitchPath != null && switchMap.TryGetValue(n.Alloc.SwitchPath, out var swName)) ? swName : "未连接"
                            };
                            vmInfo.NetworkAdapters.Add(adapter);
                        }
                        // 兼容旧代码的单 IP 显示逻辑
                        vmInfo.MacAddress = vmInfo.NetworkAdapters.FirstOrDefault()?.MacAddress ?? "00-00-00-00-00-00";
                    }

                    // --- 处理磁盘 (保持原有逻辑) ---
                    var myDisks = diskTask.Result.Where(d => d.Parent.ToUpper().Contains(vmGuidKey) || d.InstanceID.ToUpper().Contains(vmGuidKey)).ToList();
                    foreach (var d in myDisks)
                    {
                        if (d.Paths != null && d.Paths.Length > 0)
                        {
                            string path = d.Paths[0].Replace("\"", "").Trim();
                            if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || d.ResourceType == 16)
                            {
                                if (File.Exists(path)) vmInfo.Disks.Add(new VmDiskDetails { Name = Path.GetFileName(path), Path = path, CurrentSize = new FileInfo(path).Length, MaxSize = new FileInfo(path).Length, DiskType = "ISO" });
                            }
                            else
                            {
                                var (current, max, diskType) = GetDiskSizes(path);
                                if (max > 0) vmInfo.Disks.Add(new VmDiskDetails { Name = Path.GetFileName(path), Path = path, CurrentSize = current, MaxSize = max, DiskType = diskType });
                            }
                        }
                    }

                    // --- 基础元数据 (保持原有逻辑) ---
                    double startupRam = memTask.Result.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0;
                    if (vmGuidKey != null && configMap.TryGetValue(vmGuidKey, out var config)) { vmInfo.Generation = config.Gen; vmInfo.Version = config.Ver; }

                    vmInfo.OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows";
                    vmInfo.CpuCount = s.Cpu;
                    vmInfo.MemoryGb = Math.Round(startupRam / 1024.0, 1);
                    vmInfo.AssignedMemoryGb = Math.Round(((s.MemUsage > 0) ? s.MemUsage : startupRam) / 1024.0, 1);
                    vmInfo.Notes = s.Notes;
                    vmInfo.GpuName = vmGuidKey != null && gpuMap.ContainsKey(vmGuidKey) ? gpuMap[vmGuidKey] : null;

                    vmInfo.SyncBackendData(VmMapper.MapStateCodeToText(s.State), TimeSpan.FromMilliseconds(s.Uptime));
                    resultList.Add(vmInfo);
                }
                return resultList.OrderByDescending(x => x.IsRunning).ThenBy(x => x.Name).ToList();
            });
        }
        public async Task UpdateDiskPerformanceAsync(IEnumerable<VmInstanceInfo> vms)
        {
            try
            {
                var perfData = await WmiTools.QueryAsync(QueryDiskPerf, obj => new
                {
                    WmiInstanceName = obj["Name"]?.ToString() ?? "",
                    ReadBps = Convert.ToUInt64(obj["ReadBytesPersec"] ?? 0),
                    WriteBps = Convert.ToUInt64(obj["WriteBytesPersec"] ?? 0)
                }, WmiTools.CimV2Scope);

                if (perfData == null) return;

                foreach (var vm in vms)
                {
                    if (!vm.IsRunning)
                    {
                        foreach (var d in vm.Disks) { d.ReadSpeedBps = 0; d.WriteSpeedBps = 0; }
                        continue;
                    }

                    foreach (var disk in vm.Disks)
                    {
                        if (!string.IsNullOrEmpty(disk.Path))
                        {
                            string fileName = Path.GetFileName(disk.Path);
                            var match = perfData.FirstOrDefault(p =>
                                p.WmiInstanceName.Contains(fileName, StringComparison.OrdinalIgnoreCase));

                            if (match != null)
                            {
                                disk.ReadSpeedBps = (long)match.ReadBps;
                                disk.WriteSpeedBps = (long)match.WriteBps;
                            }
                            else
                            {
                                disk.ReadSpeedBps = 0;
                                disk.WriteSpeedBps = 0;
                            }
                        }
                        else
                        {
                            var match = perfData.FirstOrDefault(p =>
                                p.WmiInstanceName.Contains(vm.Name, StringComparison.OrdinalIgnoreCase) &&
                                p.WmiInstanceName.Contains("Virtual Drive", StringComparison.OrdinalIgnoreCase));

                            if (match != null)
                            {
                                disk.ReadSpeedBps = (long)match.ReadBps;
                                disk.WriteSpeedBps = (long)match.WriteBps;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateDiskPerformance Error: {ex.Message}");
            }
        }

        private (long Current, long Max, string DiskType) GetDiskSizes(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (0, 0, "Unknown");
            if (_diskSizeCache.TryGetValue(path, out var cached)) return cached;

            long currentSize = 0;
            try { currentSize = new FileInfo(path).Length; } catch { }

            long maxSize = 0;
            string diskType = "Unknown";

            try
            {
                ManagementScope scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
                using var serviceInstance = searcher.Get().Cast<ManagementObject>().FirstOrDefault();

                if (serviceInstance != null)
                {
                    using var inParams = serviceInstance.GetMethodParameters("GetVirtualHardDiskSettingData");
                    inParams["Path"] = path;

                    using var outParams = serviceInstance.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
                    uint retVal = (uint)(outParams["ReturnValue"] ?? 1);

                    if (retVal == 0)
                    {
                        string xmlData = outParams["SettingData"]?.ToString() ?? "";
                        var typeMatch = Regex.Match(xmlData, @"<PROPERTY NAME=""Type"" TYPE=""uint16""><VALUE>(\d+)</VALUE>");
                        var sizeMatch = Regex.Match(xmlData, @"<PROPERTY NAME=""MaxInternalSize"" TYPE=""uint64""><VALUE>(\d+)</VALUE>");

                        if (typeMatch.Success)
                        {
                            diskType = typeMatch.Groups[1].Value switch
                            {
                                "2" => "Fixed",
                                "3" => "Dynamic",
                                "4" => "Differencing",
                                _ => "Unknown"
                            };
                        }
                        if (sizeMatch.Success) maxSize = long.Parse(sizeMatch.Groups[1].Value);
                    }
                }
            }
            catch { }

            if (maxSize <= 0) maxSize = currentSize;
            if (diskType == "Unknown") diskType = "Dynamic";

            var result = (currentSize, maxSize, diskType);
            _diskSizeCache[path] = result;
            return result;
        }

        public async Task<bool> SetVmOsTypeAsync(string vmName, string osType)
        {
            try
            {
                string safeVmName = vmName.Replace("'", "''");
                string getSettingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{safeVmName}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
                var activeSettingsList = await WmiTools.QueryAsync(getSettingsWql, obj => obj);
                using var activeSettings = activeSettingsList.FirstOrDefault();
                if (activeSettings == null) return false;

                string currentNotes = "";
                if (activeSettings["Notes"] is string[] notesArray && notesArray.Length > 0)
                    currentNotes = string.Join("\n", notesArray);

                string newNotes = Utils.UpdateTagValue(currentNotes, "OSType", osType);
                if (currentNotes == newNotes) return true;

                activeSettings["Notes"] = new string[] { newNotes };
                string embeddedInstance = activeSettings.GetText(TextFormat.CimDtd20);
                string serviceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
                var parameters = new Dictionary<string, object> { { "SystemSettings", embeddedInstance } };
                var result = await WmiTools.ExecuteMethodAsync(serviceWql, "ModifySystemSettings", parameters);
                return result.Success;
            }
            catch { return false; }
        }

        public async Task<Dictionary<string, VmDynamicMemoryData>> GetVmRuntimeMemoryDataAsync()
        {
            var dataList = await WmiTools.QueryAsync("SELECT Name, MemoryUsage, MemoryAvailable FROM Msvm_SummaryInformation", item =>
            {
                var id = item["Name"]?.ToString();
                long rawUsage = Convert.ToInt64(item["MemoryUsage"] ?? 0);
                int rawAvailable = Convert.ToInt32(item["MemoryAvailable"] ?? 0);
                long finalAssignedMb = (rawUsage < 0 || rawUsage > 1048576) ? 0 : rawUsage;
                int finalAvailablePercent = (rawAvailable < 0 || rawAvailable > 100) ? 0 : rawAvailable;
                return new { Id = id, Data = new VmDynamicMemoryData { AssignedMb = finalAssignedMb, AvailablePercent = finalAvailablePercent } };
            });
            return dataList.Where(x => x.Id != null).ToDictionary(x => x.Id, x => x.Data);
        }

        private async Task<Dictionary<string, string>> GetHostVideoControllerMapAsync()
        {
            var result = await WmiTools.QueryAsync("SELECT Name, PNPDeviceID FROM Win32_VideoController", item => new { Name = item["Name"]?.ToString(), PnpId = item["PNPDeviceID"]?.ToString() }, WmiTools.CimV2Scope);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in result)
            {
                string shortId = ExtractPciId(item.PnpId);
                if (!string.IsNullOrEmpty(shortId) && !string.IsNullOrEmpty(item.Name) && !map.ContainsKey(shortId)) map[shortId] = item.Name;
            }
            return map;
        }

        private string ExtractPciId(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = Regex.Match(input, @"(VEN_[0-9A-Z]{4}&DEV_[0-9A-Z]{4})", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpper() : null;
        }

        private string ExtractFirstGuid(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = Regex.Match(input, @"[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}");
            return match.Success ? match.Value.ToUpper() : null;
        }


        public struct GpuUsageData
        {
            public double Gpu3d;
            public double GpuCopy;
            public double GpuEncode;
            public double GpuDecode;

            public override string ToString() => $"3D: {Gpu3d:F1}, Copy: {GpuCopy:F1}, Enc: {GpuEncode:F1}, Dec: {GpuDecode:F1}";
        }

        private static Dictionary<Guid, int> _vmProcessIdCache = new();
        private static DateTime _processIdCacheTimestamp = DateTime.MinValue;
        private PerformanceCounterCategory? _gpuCategory;
        private List<PerformanceCounter> _gpuCounters = new();
        private static readonly Regex GpuInstanceRegex = new Regex(@"pid_(\d+).*engtype_([a-zA-Z0-9]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public async Task<Dictionary<Guid, GpuUsageData>> GetGpuPerformanceAsync(IEnumerable<VmInstanceInfo> vms)
        {
            var results = new Dictionary<Guid, GpuUsageData>();
            var runningGpuVms = vms.Where(vm => vm.IsRunning && vm.HasGpu).ToList();

            if (runningGpuVms.Count == 0) return results;

            try
            {
                // 1. 刷新 PID 缓存 (每 5 秒一次)
                bool pidRefreshed = false;
                if ((DateTime.Now - _processIdCacheTimestamp).TotalSeconds > 5)
                {
                    await RefreshVmPidCache(runningGpuVms);
                    pidRefreshed = true;
                }

                if (_vmProcessIdCache.Count == 0) return results;

                // 2. 【核心修复】如果 PID 刷新了，或者计数器列表为空，则重新构建计数器池
                // 这样可以捕捉到因为“连接桌面”而动态产生的 VideoEncode/Decode 实例
                if (pidRefreshed || _gpuCounters.Count == 0)
                {
                    RebuildGpuCounters();
                }

                // 3. 读取数据并累加
                var usageByPid = new Dictionary<int, GpuUsageData>();

                // 预创建 PID 槽位
                foreach (var pid in _vmProcessIdCache.Values) usageByPid[pid] = new GpuUsageData();

                // 使用本地列表防止迭代时被修改
                var activeCounters = _gpuCounters.ToList();
                foreach (var counter in activeCounters)
                {
                    try
                    {
                        var match = GpuInstanceRegex.Match(counter.InstanceName);
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int pid))
                        {
                            if (usageByPid.ContainsKey(pid))
                            {
                                string type = match.Groups[2].Value.ToUpper();
                                float value = counter.NextValue();

                                var data = usageByPid[pid];
                                // 合并同类项逻辑
                                if (type.Contains("3D")) data.Gpu3d += value;
                                else if (type.Contains("COPY")) data.GpuCopy += value;
                                else if (type.Contains("ENCODE")) data.GpuEncode += value;
                                else if (type.Contains("DECODE")) data.GpuDecode += value;
                                usageByPid[pid] = data;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // 实例已消失（虚拟机连接断开或引擎关闭），标记需要重构
                        _gpuCounters.Remove(counter);
                        counter.Dispose();
                    }
                }

                // 4. 映射回结果
                foreach (var vm in runningGpuVms)
                {
                    if (_vmProcessIdCache.TryGetValue(vm.Id, out int pid))
                        results[vm.Id] = usageByPid[pid];
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GPU Perf ERROR] {ex.Message}");
                _gpuCategory = null; // 发生严重错误时重置，下次循环重新初始化
            }

            return results;
        }

        private async Task RefreshVmPidCache(List<VmInstanceInfo> runningGpuVms)
        {
            _vmProcessIdCache.Clear();
            var processList = await WmiTools.QueryAsync(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'vmwp.exe'",
                obj => new { Pid = Convert.ToInt32(obj["ProcessId"]), Cmd = obj["CommandLine"]?.ToString() ?? "" },
                WmiTools.CimV2Scope);

            foreach (var vm in runningGpuVms)
            {
                string guidStr = vm.Id.ToString();
                var proc = processList.FirstOrDefault(p => p.Cmd.Contains(guidStr, StringComparison.OrdinalIgnoreCase));
                if (proc != null) _vmProcessIdCache[vm.Id] = proc.Pid;
            }
            _processIdCacheTimestamp = DateTime.Now;
        }

        private void RebuildGpuCounters()
        {
            try
            {
                // 清理旧计数器
                foreach (var c in _gpuCounters) c.Dispose();
                _gpuCounters.Clear();

                if (!PerformanceCounterCategory.Exists("GPU Engine")) return;

                var category = new PerformanceCounterCategory("GPU Engine");
                var instanceNames = category.GetInstanceNames();

                // 只为我们关心的虚拟机 PID 创建计数器，极大提高性能
                var targetPids = _vmProcessIdCache.Values.Select(p => $"pid_{p}_").ToList();

                foreach (var name in instanceNames)
                {
                    if (targetPids.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                            pc.NextValue(); // 预热
                            _gpuCounters.Add(pc);
                        }
                        catch { /* 忽略单个失效实例 */ }
                    }
                }
                _gpuCategory = category;
            }
            catch { }
        }

    }
}