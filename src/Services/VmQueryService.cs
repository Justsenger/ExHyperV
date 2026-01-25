using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class VmQueryService
    {
        public struct VmDynamicMemoryData
        {
            public long AssignedMb;      // MemoryUsage
            public int AvailablePercent; // MemoryAvailable
        }

        // WQL 常量
        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QueryDiskSettings = "SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31";

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            return await Task.Run(() =>
            {
                // 1. 并行查询基础数据
                var summaries = Utils.Wmi.Query(QuerySummary, obj => new
                {
                    Id = obj["Name"]?.ToString(),
                    Name = obj["ElementName"]?.ToString(),
                    State = (ushort)(obj["EnabledState"] ?? 0),
                    Cpu = Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    MemUsage = Convert.ToDouble(obj["MemoryUsage"] ?? 0),
                    Uptime = (ulong)(obj["UpTime"] ?? 0),
                    Notes = VmMapper.ParseNotes(obj["Notes"])
                });

                var memSettings = Utils.Wmi.Query(QueryMemSettings, obj => new {
                    FullId = obj["InstanceID"]?.ToString(),
                    StartupRam = Convert.ToDouble(obj["VirtualQuantity"] ?? 0)
                });

                var diskSettings = Utils.Wmi.Query(QueryDiskSettings, obj => new {
                    FullId = obj["InstanceID"]?.ToString(),
                    Paths = (string[])obj["HostResource"]
                });

                // 2. 聚合
                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);

                    // 计算 RAM
                    double finalRam = 0;
                    if (VmMapper.IsRunning(s.State) && s.MemUsage > 0)
                        finalRam = s.MemUsage;
                    else
                    {
                        var conf = memSettings.FirstOrDefault(m => m.FullId?.Contains(s.Id) == true);
                        if (conf != null) finalRam = conf.StartupRam;
                    }

                    // 计算 Disk
                    string diskStr = "N/A";
                    var vmDisk = diskSettings.FirstOrDefault(d => d.FullId?.Contains(s.Id) == true);
                    if (vmDisk?.Paths?.Length > 0)
                    {
                        try
                        {
                            if (File.Exists(vmDisk.Paths[0]))
                            {
                                long len = new FileInfo(vmDisk.Paths[0]).Length;
                                diskStr = $"{Math.Round(len / 1073741824.0, 0)}G";
                            }
                        }
                        catch { }
                    }

                    // --- 修复构造函数报错 ---
                    // 使用 2 参数构造函数 + 对象初始化器
                    var vmInfo = new VmInstanceInfo(vmId, s.Name)
                    {
                        OsType = VmMapper.ParseOsTypeFromNotes(s.Notes),
                        CpuCount = s.Cpu,
                        MemoryGb = Math.Round(finalRam / 1024.0, 1),
                        AssignedMemoryGb = Math.Round(finalRam / 1024.0, 1),
                        DiskSize = diskStr,
                        Notes = s.Notes,
                        Generation = 0
                    };

                    // 同步后端状态和运行时间锚点
                    vmInfo.SyncBackendData(
                        VmMapper.MapStateCodeToText(s.State),
                        TimeSpan.FromMilliseconds(s.Uptime)
                    );

                    resultList.Add(vmInfo);
                }

                return resultList.OrderByDescending(x => x.State == "运行中").ThenBy(x => x.Name).ToList();
            });
        }

        public Dictionary<string, VmDynamicMemoryData> GetVmRuntimeMemoryData()
        {
            var map = new Dictionary<string, VmDynamicMemoryData>();
            try
            {
                var scope = new ManagementScope(@"root\virtualization\v2");
                var query = new SelectQuery("SELECT Name, MemoryUsage, MemoryAvailable FROM Msvm_SummaryInformation");

                using var searcher = new ManagementObjectSearcher(scope, query);
                using var collection = searcher.Get();

                foreach (var item in collection)
                {
                    var vmId = item["Name"]?.ToString();
                    var usageObj = item["MemoryUsage"];
                    var availObj = item["MemoryAvailable"];

                    if (vmId != null && usageObj != null)
                    {
                        map[vmId] = new VmDynamicMemoryData
                        {
                            AssignedMb = Convert.ToInt64(usageObj),
                            AvailablePercent = availObj != null ? Convert.ToInt32(availObj) : 0
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI Memory Query Error: {ex.Message}");
            }
            return map;
        }
    }
}