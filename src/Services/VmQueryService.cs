using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscUtils;
using DiscUtils.Streams;

namespace ExHyperV.Services
{
    public class VmQueryService
    {
        public struct VmDynamicMemoryData { public long AssignedMb; public int AvailablePercent; }

        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QueryDiskAllocations = "SELECT InstanceID, Parent, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31";
        private const string QuerySettings = "SELECT ConfigurationID, VirtualSystemSubType, Version FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        private const string QueryGpuPvSettings = "SELECT InstanceID, HostResource FROM Msvm_GpuPartitionSettingData";
        private const string QueryPartitionableGpus = "SELECT Name FROM Msvm_PartitionableGpu";

        private static readonly Dictionary<string, long> _diskSizeCache = new();

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            return await Task.Run(() =>
            {
                var allDiskAllocations = Utils.Wmi.Query(QueryDiskAllocations, obj => new {
                    InstanceID = obj["InstanceID"]?.ToString() ?? "",
                    Parent = obj["Parent"]?.ToString() ?? "",
                    Paths = obj["HostResource"] as string[]
                });

                var summaries = Utils.Wmi.Query(QuerySummary, obj => {
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

                var memSettings = Utils.Wmi.Query(QueryMemSettings, obj => new {
                    FullId = obj["InstanceID"]?.ToString(),
                    StartupRam = Convert.ToDouble(obj["VirtualQuantity"] ?? 0)
                });

                var configMap = Utils.Wmi.Query(QuerySettings, obj => {
                    string subType = obj["VirtualSystemSubType"]?.ToString() ?? "";
                    string version = obj["Version"]?.ToString() ?? "0.0";
                    int gen = 0;
                    if (subType.EndsWith(":1")) gen = 1;
                    else if (subType.EndsWith(":2")) gen = 2;
                    return new { VmGuid = obj["ConfigurationID"]?.ToString()?.Trim('{', '}').ToUpper(), Gen = gen, Ver = version };
                })
                .Where(x => !string.IsNullOrEmpty(x.VmGuid))
                .GroupBy(x => x.VmGuid)
                .ToDictionary(g => g.Key, g => new { g.First().Gen, g.First().Ver });

                var gpuMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var pciToFriendlyNameMap = GetHostVideoControllerMap();
                    var hostPathToPciIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var partitionableGpus = Utils.Wmi.Query(QueryPartitionableGpus, obj => obj["Name"]?.ToString());

                    foreach (var rawPath in partitionableGpus)
                    {
                        if (string.IsNullOrEmpty(rawPath)) continue;
                        string shortId = ExtractPciId(rawPath);
                        if (!string.IsNullOrEmpty(shortId)) hostPathToPciIdMap[rawPath] = shortId;
                    }

                    var gpuSettings = Utils.Wmi.Query(QueryGpuPvSettings, obj => new {
                        InstanceID = obj["InstanceID"]?.ToString(),
                        HostResources = obj["HostResource"] as string[]
                    });

                    foreach (var setting in gpuSettings)
                    {
                        string vmGuid = ExtractFirstGuid(setting.InstanceID);
                        if (vmGuid == null) continue;

                        if (setting.HostResources != null && setting.HostResources.Length > 0)
                        {
                            string assignedPath = setting.HostResources[0];
                            string finalName = "GPU-PV Device";
                            string shortId = null;

                            if (hostPathToPciIdMap.TryGetValue(assignedPath, out var id)) shortId = id;
                            else
                            {
                                var fuzzyMatch = hostPathToPciIdMap.Keys.FirstOrDefault(k =>
                                    assignedPath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    k.IndexOf(assignedPath, StringComparison.OrdinalIgnoreCase) >= 0);
                                if (fuzzyMatch != null) shortId = hostPathToPciIdMap[fuzzyMatch];
                                else shortId = ExtractPciId(assignedPath);
                            }

                            if (!string.IsNullOrEmpty(shortId))
                            {
                                if (pciToFriendlyNameMap.TryGetValue(shortId, out var friendly)) finalName = friendly;
                                else finalName = $"Unknown Device ({shortId})";
                            }
                            gpuMap[vmGuid] = finalName;
                        }
                    }
                }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }

                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    string vmGuid = s.Id?.Trim('{', '}').ToUpper();

                    var vmDiskSizes = new List<long>();
                    try
                    {
                        var myDisks = allDiskAllocations.Where(d => d.Parent.ToUpper().Contains(vmGuid) || d.InstanceID.ToUpper().Contains(vmGuid)).ToList();
                        foreach (var d in myDisks)
                        {
                            if (d.Paths != null && d.Paths.Length > 0)
                            {
                                string path = d.Paths[0].Replace("\"", "").Trim();
                                if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) continue;
                                long size = GetDiskCapacityWithDiscUtils(path, s.Name);
                                if (size > 0) vmDiskSizes.Add(size);
                            }
                        }
                    }
                    catch { }

                    double startupRam = memSettings.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0;
                    bool isRunning = VmMapper.IsRunning(s.State);
                    bool hasValidRealtimeMem = s.MemUsage > 0;

                    double usageRam = (isRunning && hasValidRealtimeMem) ? s.MemUsage : 0;
                    double assignedRam = (isRunning && hasValidRealtimeMem) ? s.MemUsage : startupRam;

                    int genValue = 0; string verValue = "0.0";
                    if (vmGuid != null && configMap.TryGetValue(vmGuid, out var config)) { genValue = config.Gen; verValue = config.Ver; }

                    string gpuName = null;
                    if (vmGuid != null && gpuMap.ContainsKey(vmGuid)) gpuName = gpuMap[vmGuid];

                    var vmInfo = new VmInstanceInfo(vmId, s.Name)
                    {
                        OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows",
                        CpuCount = s.Cpu,
                        MemoryGb = Math.Round(startupRam / 1024.0, 1),
                        AssignedMemoryGb = Math.Round(assignedRam / 1024.0, 1),
                        DiskSizeRaw = vmDiskSizes,
                        Notes = s.Notes,
                        Generation = genValue,
                        Version = verValue,
                        GpuName = gpuName
                    };

                    vmInfo.SyncBackendData(VmMapper.MapStateCodeToText(s.State), TimeSpan.FromMilliseconds(s.Uptime));
                    resultList.Add(vmInfo);
                }

                return resultList.OrderByDescending(x => x.State == "运行中").ThenBy(x => x.Name).ToList();
            });
        }
        // ==========================================
        // 1. 修改 OS 类型的方法
        // ==========================================
        public async Task<bool> SetVmOsTypeAsync(string vmName, string osType)
        {
            return await Task.Run(() => {
                try
                {
                    Debug.WriteLine($"[SetVmOsType] 开始修改虚拟机: {vmName}, 目标系统: {osType}");

                    var options = new ConnectionOptions
                    {
                        Impersonation = ImpersonationLevel.Impersonate,
                        Authentication = AuthenticationLevel.PacketPrivacy,
                        EnablePrivileges = true
                    };
                    var scope = new ManagementScope(@"\\.\root\virtualization\v2", options);
                    scope.Connect();

                    using var serviceClass = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null);
                    using var service = serviceClass.GetInstances().Cast<ManagementObject>().FirstOrDefault();
                    if (service == null) return false;

                    // A. 找到虚拟机
                    string safeVmName = vmName.Replace("'", "''");
                    using var vmSearcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{safeVmName}'"));
                    using var vm = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vm == null) return false;

                    // B. 找到当前生效的配置 (Realized)
                    var relatedSettings = vm.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>();
                    using var activeSettings = relatedSettings.FirstOrDefault(s =>
                        s["VirtualSystemType"]?.ToString()?.Contains("Realized") == true);

                    if (activeSettings == null) return false;

                    // C. 读取并更新 Notes (注意：WMI 中 Notes 是 string[] 类型)
                    string currentNotes = "";
                    if (activeSettings["Notes"] is string[] notesArray && notesArray.Length > 0)
                    {
                        currentNotes = string.Join("\n", notesArray);
                    }

                    string newNotes = Utils.UpdateTagValue(currentNotes, "OSType", osType);
                    if (currentNotes == newNotes) return true;

                    // 关键修复：必须赋值为 string 数组
                    activeSettings["Notes"] = new string[] { newNotes };

                    // D. 提交修改
                    string embeddedInstance = activeSettings.GetText(TextFormat.CimDtd20);
                    using var inParams = service.GetMethodParameters("ModifySystemSettings");
                    inParams["SystemSettings"] = embeddedInstance;

                    using var outParams = service.InvokeMethod("ModifySystemSettings", inParams, null);

                    // 调用下面的辅助方法等待结果
                    return WaitForJob(scope, outParams);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SetVmOsType] 异常: {ex.Message}");
                    return false;
                }
            });
        }

        // ==========================================
        // 2. 缺失的辅助方法：等待 WMI 任务完成
        // ==========================================
        private bool WaitForJob(ManagementScope scope, ManagementBaseObject outParams)
        {
            uint ret = (uint)(outParams["ReturnValue"] ?? 0);

            // 0 表示同步操作已直接成功
            if (ret == 0) return true;

            // 4096 表示异步任务已启动，需要等待 Job 完成
            if (ret == 4096)
            {
                string jobPath = outParams["Job"]?.ToString();
                if (string.IsNullOrEmpty(jobPath)) return false;

                using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);

                // 轮询 Job 状态 (最多等待 10 秒)
                var timeout = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < timeout)
                {
                    System.Threading.Thread.Sleep(200);
                    job.Get(); // 刷新 Job 对象数据

                    ushort state = (ushort)(job["JobState"] ?? 0);

                    // 7 = 已完成 (Success)
                    if (state == 7) return true;

                    // 8, 9, 10 = 失败/已终止/异常
                    if (state == 8 || state == 9 || state == 10)
                    {
                        Debug.WriteLine($"[WMI Job Error] {job["ErrorDescription"]}");
                        return false;
                    }
                }
                return false; // 超时
            }

            // 其他返回值均为错误码
            Debug.WriteLine($"[WMI Error] ReturnValue: {ret}");
            return false;
        }

        private Dictionary<string, string> GetHostVideoControllerMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope(@"root\cimv2");
                var query = new SelectQuery("SELECT Name, PNPDeviceID FROM Win32_VideoController");
                using var searcher = new ManagementObjectSearcher(scope, query);
                using var collection = searcher.Get();

                foreach (var item in collection)
                {
                    string name = item["Name"]?.ToString();
                    string pnpId = item["PNPDeviceID"]?.ToString();
                    string shortId = ExtractPciId(pnpId);
                    if (!string.IsNullOrEmpty(shortId) && !string.IsNullOrEmpty(name) && !map.ContainsKey(shortId)) map[shortId] = name;
                }
            }
            catch { }
            return map;
        }

        private string ExtractPciId(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = Regex.Match(input, @"(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpper() : null;
        }

        private string ExtractFirstGuid(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            var match = Regex.Match(input, @"[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}");
            return match.Success ? match.Value.ToUpper() : null;
        }

        private long GetDiskCapacityWithDiscUtils(string path, string vmName)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            if (_diskSizeCache.TryGetValue(path, out long cached)) return cached;
            try
            {
                if (!File.Exists(path)) return 0;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var disk = new DiscUtils.Vhdx.Disk(fs, Ownership.None)) { long cap = disk.Capacity; _diskSizeCache[path] = cap; return cap; }
                    }
                    else if (path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhd", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var disk = new DiscUtils.Vhd.Disk(fs, Ownership.None)) { long cap = disk.Capacity; _diskSizeCache[path] = cap; return cap; }
                    }
                }
            }
            catch { }
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        public Dictionary<string, VmDynamicMemoryData> GetVmRuntimeMemoryData()
        {
            var map = new Dictionary<string, VmDynamicMemoryData>();
            try
            {
                var scope = new ManagementScope(@"root\virtualization\v2");
                using var searcher = new ManagementObjectSearcher(scope, new SelectQuery("SELECT Name, MemoryUsage, MemoryAvailable FROM Msvm_SummaryInformation"));
                using var collection = searcher.Get();
                foreach (var item in collection)
                {
                    var id = item["Name"]?.ToString();
                    if (id == null) continue;

                    // --- 严格范围校验 ---
                    long rawUsage = Convert.ToInt64(item["MemoryUsage"] ?? 0);
                    int rawAvailable = Convert.ToInt32(item["MemoryAvailable"] ?? 0);

                    // 逻辑：如果 rawUsage 超过 1TB (1048576 MB) 或小于 0，直接判定为 0
                    // 这样就保证了它永远在 [0, 右边数字] 的合理感知范围内
                    long finalAssignedMb = (rawUsage < 0 || rawUsage > 1048576) ? 0 : rawUsage;

                    // AvailablePercent 同样，如果不在 0-100 之间，通常也是无效数据
                    int finalAvailablePercent = (rawAvailable < 0 || rawAvailable > 100) ? 0 : rawAvailable;

                    map[id] = new VmDynamicMemoryData
                    {
                        AssignedMb = finalAssignedMb,
                        AvailablePercent = finalAvailablePercent
                    };
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Memory Refresh Error: {ex.Message}"); }
            return map;
        }
    }
}