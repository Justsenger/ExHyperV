using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
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

        // === 修改查询语句：加入 Version 字段 ===
        private const string QuerySettings = "SELECT ConfigurationID, VirtualSystemSubType, Version FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

        private static readonly Dictionary<string, long> _diskSizeCache = new();

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            return await Task.Run(() =>
            {
                Debug.WriteLine("\n========== [Storage Sync Start] ==========");

                // 1. 获取所有磁盘分配信息
                var allDiskAllocations = Utils.Wmi.Query(QueryDiskAllocations, obj => new {
                    InstanceID = obj["InstanceID"]?.ToString() ?? "",
                    Parent = obj["Parent"]?.ToString() ?? "",
                    Paths = obj["HostResource"] as string[]
                });

                // 2. 获取虚拟机状态摘要
                var summaries = Utils.Wmi.Query(QuerySummary, obj => new {
                    Id = obj["Name"]?.ToString(),
                    Name = obj["ElementName"]?.ToString(),
                    State = (ushort)(obj["EnabledState"] ?? 0),
                    Cpu = Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    MemUsage = Convert.ToDouble(obj["MemoryUsage"] ?? 0),
                    Uptime = (ulong)(obj["UpTime"] ?? 0),
                    Notes = obj["Notes"]?.ToString() ?? string.Empty
                });

                // 3. 获取静态内存配置
                var memSettings = Utils.Wmi.Query(QueryMemSettings, obj => new {
                    FullId = obj["InstanceID"]?.ToString(),
                    StartupRam = Convert.ToDouble(obj["VirtualQuantity"] ?? 0)
                });

                // 4. === 核心修改：抓取代数(SubType)和配置版本(Version) ===
                var configMap = Utils.Wmi.Query(QuerySettings, obj => {
                    string subType = obj["VirtualSystemSubType"]?.ToString() ?? "";
                    string version = obj["Version"]?.ToString() ?? "0.0";
                    int gen = 0;

                    // 解析代数：Microsoft:Hyper-V:SubType:1 为 1代，:2 为 2代
                    if (subType.EndsWith(":1")) gen = 1;
                    else if (subType.EndsWith(":2")) gen = 2;

                    return new
                    {
                        VmGuid = obj["ConfigurationID"]?.ToString()?.Trim('{', '}').ToUpper(),
                        Gen = gen,
                        Ver = version
                    };
                })
                .Where(x => !string.IsNullOrEmpty(x.VmGuid))
                .GroupBy(x => x.VmGuid)
                .ToDictionary(g => g.Key, g => new { g.First().Gen, g.First().Ver });

                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    // 统一 ID 格式用于字典匹配
                    string vmGuid = s.Id?.Trim('{', '}').ToUpper();

                    var vmDiskSizes = new List<long>();
                    var myDisks = allDiskAllocations.Where(d =>
                        d.Parent.ToUpper().Contains(vmGuid) ||
                        d.InstanceID.ToUpper().Contains(vmGuid)).ToList();

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

                    double finalRam = VmMapper.IsRunning(s.State) && s.MemUsage > 0
                        ? s.MemUsage
                        : (memSettings.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0);

                    // 5. === 从配置映射表中提取 Gen 和 Version ===
                    int genValue = 0;
                    string verValue = "0.0";
                    if (vmGuid != null && configMap.TryGetValue(vmGuid, out var config))
                    {
                        genValue = config.Gen;
                        verValue = config.Ver;
                    }

                    var vmInfo = new VmInstanceInfo(vmId, s.Name)
                    {
                        OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows",
                        CpuCount = s.Cpu,
                        MemoryGb = Math.Round(finalRam / 1024.0, 1),
                        AssignedMemoryGb = Math.Round(finalRam / 1024.0, 1),
                        DiskSizeRaw = vmDiskSizes,
                        Notes = s.Notes,
                        Generation = genValue, // 设置 1代/2代
                        Version = verValue    // 设置 11.0/12.0 等版本
                    };

                    vmInfo.SyncBackendData(VmMapper.MapStateCodeToText(s.State), TimeSpan.FromMilliseconds(s.Uptime));
                    resultList.Add(vmInfo);
                }
                Debug.WriteLine("========== [Storage Sync End] ==========\n");
                return resultList.OrderByDescending(x => x.State == "运行中").ThenBy(x => x.Name).ToList();
            });
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
                    VirtualDisk disk = null;
                    if (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase))
                        disk = new DiscUtils.Vhdx.Disk(fs, Ownership.None);
                    else if (path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhd", StringComparison.OrdinalIgnoreCase))
                        disk = new DiscUtils.Vhd.Disk(fs, Ownership.None);

                    if (disk != null)
                    {
                        using (disk)
                        {
                            long cap = disk.Capacity;
                            _diskSizeCache[path] = cap;
                            return cap;
                        }
                    }
                }
            }
            catch { }
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        public async Task<bool> SetVmOsTypeAsync(string vmName, string osType)
        {
            return await Task.Run(() => {
                try
                {
                    using (var ps = System.Management.Automation.PowerShell.Create())
                    {
                        ps.AddScript($"(Get-VM -Name '{vmName}').Notes");
                        var r = ps.Invoke();
                        string n = r.FirstOrDefault()?.ToString() ?? string.Empty;
                        ps.Commands.Clear();
                        string un = Utils.UpdateTagValue(n, "OSType", osType);
                        ps.AddCommand("Set-VM").AddParameter("Name", vmName).AddParameter("Notes", un).Invoke();
                        return !ps.HadErrors;
                    }
                }
                catch { return false; }
            });
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
                    if (id != null) map[id] = new VmDynamicMemoryData { AssignedMb = Convert.ToInt64(item["MemoryUsage"] ?? 0), AvailablePercent = Convert.ToInt32(item["MemoryAvailable"] ?? 0) };
                }
            }
            catch { }
            return map;
        }
    }
}