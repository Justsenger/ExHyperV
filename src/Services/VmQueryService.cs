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


        private static readonly Dictionary<string, (long Current, long Max, string Type)> _diskSizeCache = new();

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
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

            await Task.WhenAll(diskTask, summaryTask, memTask, configTask, gpuPvTask, gpuListTask, pciMapTask);

            return await Task.Run(() =>
            {
                var allDiskAllocations = diskTask.Result;
                var summaries = summaryTask.Result;
                var memSettings = memTask.Result;
                var configMap = configTask.Result
                    .Where(x => !string.IsNullOrEmpty(x.VmGuid))
                    .GroupBy(x => x.VmGuid)
                    .ToDictionary(g => g.Key, g => new { g.First().Gen, g.First().Ver });

                var gpuSettings = gpuPvTask.Result;
                var partitionableGpus = gpuListTask.Result;
                var pciToFriendlyNameMap = pciMapTask.Result;

                var gpuMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var hostPathToPciIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rawPath in partitionableGpus)
                {
                    string shortId = ExtractPciId(rawPath);
                    if (!string.IsNullOrEmpty(shortId)) hostPathToPciIdMap[rawPath] = shortId;
                }

                foreach (var setting in gpuSettings)
                {
                    string vmGuidStr = ExtractFirstGuid(setting.InstanceID);
                    if (vmGuidStr != null && setting.HostResources?.Length > 0)
                    {
                        string assignedPath = setting.HostResources[0];
                        string finalName = "GPU-PV Device";
                        string shortId = hostPathToPciIdMap.TryGetValue(assignedPath, out var id) ? id : ExtractPciId(assignedPath);

                        if (!string.IsNullOrEmpty(shortId))
                        {
                            if (pciToFriendlyNameMap.TryGetValue(shortId, out var friendly)) finalName = friendly;
                            else finalName = $"Unknown Device ({shortId})";
                        }
                        gpuMap[vmGuidStr] = finalName;
                    }
                }

                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    string vmGuidKey = s.Id?.Trim('{', '}').ToUpper();

                    var vmInfo = new VmInstanceInfo(vmId, s.Name);

                    var myDisks = allDiskAllocations.Where(d => d.Parent.ToUpper().Contains(vmGuidKey) || d.InstanceID.ToUpper().Contains(vmGuidKey)).ToList();

                    foreach (var d in myDisks)
                    {
                        if (d.Paths != null && d.Paths.Length > 0)
                        {
                            string path = d.Paths[0].Replace("\"", "").Trim();
                            bool isIso = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);

                            if (isIso || d.ResourceType == 16)
                            {
                                if (File.Exists(path))
                                {
                                    long isoSize = 0;
                                    try { isoSize = new FileInfo(path).Length; } catch { }

                                    vmInfo.Disks.Add(new VmDiskDetails
                                    {
                                        Name = Path.GetFileName(path),
                                        Path = path,
                                        CurrentSize = isoSize,
                                        MaxSize = isoSize,
                                        DiskType = "ISO"
                                    });
                                }
                            }
                            else
                            {
                                var (current, max, diskType) = GetDiskSizes(path);
                                if (max > 0)
                                {
                                    vmInfo.Disks.Add(new VmDiskDetails
                                    {
                                        Name = Path.GetFileName(path),
                                        Path = path,
                                        CurrentSize = current,
                                        MaxSize = max,
                                        DiskType = diskType
                                    });
                                }
                            }
                        }
                    }

                    double startupRam = memSettings.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0;
                    bool isRunning = VmMapper.IsRunning(s.State);
                    bool hasValidRealtimeMem = s.MemUsage > 0;

                    double usageRam = (isRunning && hasValidRealtimeMem) ? s.MemUsage : 0;
                    double assignedRam = (isRunning && hasValidRealtimeMem) ? s.MemUsage : startupRam;

                    int genValue = 0; string verValue = "0.0";
                    if (vmGuidKey != null && configMap.TryGetValue(vmGuidKey, out var config)) { genValue = config.Gen; verValue = config.Ver; }

                    string gpuName = vmGuidKey != null && gpuMap.ContainsKey(vmGuidKey) ? gpuMap[vmGuidKey] : null;

                    vmInfo.OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows";
                    vmInfo.CpuCount = s.Cpu;
                    vmInfo.MemoryGb = Math.Round(startupRam / 1024.0, 1);
                    vmInfo.AssignedMemoryGb = Math.Round(assignedRam / 1024.0, 1);
                    vmInfo.Notes = s.Notes;
                    vmInfo.Generation = genValue;
                    vmInfo.Version = verValue;
                    vmInfo.GpuName = gpuName;

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
            Debug.WriteLine("\n--- [GetGpuPerformanceAsync V3] Cycle Start ---");
            var results = new Dictionary<Guid, GpuUsageData>();
            var runningGpuVms = vms.Where(vm => vm.IsRunning && vm.HasGpu).ToList();

            if (runningGpuVms.Count == 0)
            {
                Debug.WriteLine("[GPU Perf V3] No running VMs with GPU detected. Exiting.");
                return results;
            }
            Debug.WriteLine($"[GPU Perf V3] Found {runningGpuVms.Count} running VM(s) with GPU: {string.Join(", ", runningGpuVms.Select(vm => vm.Name))}");

            try
            {
                // 1. 【新逻辑】如果缓存过期，则刷新虚拟机进程的 PID
                if ((DateTime.Now - _processIdCacheTimestamp).TotalSeconds > 5)
                {
                    Debug.WriteLine("[GPU Perf V3] PID cache expired. Refreshing with new logic...");
                    _vmProcessIdCache.Clear();

                    var processList = await WmiTools.QueryAsync(
                        "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'vmwp.exe'",
                        obj => new {
                            Pid = Convert.ToInt32(obj["ProcessId"]),
                            Cmd = obj["CommandLine"]?.ToString() ?? ""
                        },
                        WmiTools.CimV2Scope
                    );

                    if (processList.Count > 0)
                    {
                        // 将正在运行的虚拟机的 GUID 转换为字符串形式以进行匹配
                        var runningVmGuids = runningGpuVms.Select(vm => vm.Id.ToString()).ToHashSet();

                        foreach (var proc in processList)
                        {
                            // 直接在命令行字符串中查找匹配的 GUID
                            foreach (var vmGuidStr in runningVmGuids)
                            {
                                if (!string.IsNullOrEmpty(proc.Cmd) && proc.Cmd.Contains(vmGuidStr, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (Guid.TryParse(vmGuidStr, out Guid vmGuid))
                                    {
                                        _vmProcessIdCache[vmGuid] = proc.Pid;
                                        Debug.WriteLine($"  -> SUCCESS: Matched PID {proc.Pid} to VM GUID {vmGuidStr}");
                                        // 找到后可以跳出内层循环，继续下一个进程
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    _processIdCacheTimestamp = DateTime.Now;
                    Debug.WriteLine($"[GPU Perf V3] PID cache refreshed. Found {_vmProcessIdCache.Count} mapped vmwp processes.");
                }

                // 如果经过新逻辑仍然找不到任何映射，则提前退出
                if (_vmProcessIdCache.Count == 0)
                {
                    Debug.WriteLine("[GPU Perf V3] After refresh, still failed to map any vmwp.exe PID to a running VM.");
                    return results;
                }

                // 2. 初始化性能计数器 (逻辑不变)
                if (_gpuCategory == null && PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    Debug.WriteLine("[GPU Perf V3] First run: Initializing Performance Counters for 'GPU Engine'.");
                    _gpuCategory = new PerformanceCounterCategory("GPU Engine");
                    var instanceNames = _gpuCategory.GetInstanceNames();
                    _gpuCounters = instanceNames
                        .Where(name => name.Contains("pid_"))
                        .Select(name => new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true))
                        .ToList();
                    _gpuCounters.ForEach(c => c.NextValue());
                    await Task.Delay(100);
                }

                if (_gpuCounters.Count == 0) return results;

                // 3. 读取计数器数据 (逻辑不变)
                var usageByPid = new Dictionary<int, GpuUsageData>();
                foreach (var counter in _gpuCounters)
                {
                    var match = GpuInstanceRegex.Match(counter.InstanceName);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int pid))
                    {
                        if (!usageByPid.ContainsKey(pid)) usageByPid[pid] = new GpuUsageData();

                        string type = match.Groups[2].Value.ToUpper();
                        var currentData = usageByPid[pid];
                        float value = counter.NextValue();
                        switch (type)
                        {
                            case "3D": currentData.Gpu3d += value; break;
                            case "COPY": currentData.GpuCopy += value; break;
                            case "VIDEOENCODE": currentData.GpuEncode += value; break;
                            case "VIDEODECODE": currentData.GpuDecode += value; break;
                        }
                        usageByPid[pid] = currentData;
                    }
                }

                // 4. 将结果映射回 VM (逻辑不变)
                Debug.WriteLine("[GPU Perf V3] Mapping PID data back to VM GUIDs...");
                foreach (var vm in runningGpuVms)
                {
                    if (_vmProcessIdCache.TryGetValue(vm.Id, out int pid) && usageByPid.TryGetValue(pid, out var usage))
                    {
                        results[vm.Id] = usage;
                        Debug.WriteLine($"  -> Mapped data for VM '{vm.Name}' (PID: {pid}): {usage}");
                    }
                    else
                    {
                        results[vm.Id] = new GpuUsageData();
                        Debug.WriteLine($"  -> No data found for VM '{vm.Name}'. Setting to zero.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GPU Perf V3] FATAL ERROR in GetGpuPerformanceAsync: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
            }
            Debug.WriteLine("--- [GetGpuPerformanceAsync V3] Cycle End ---\n");
            return results;
        }
    }
}