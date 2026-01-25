using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

//查询服务


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
                    Notes = VmMapper.ParseNotes(obj["Notes"]) // 调用 Mapper
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

                    // 计算 Disk (取第一个 VHD 的物理大小)
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

                    // 构造模型
                    resultList.Add(new VmInstanceInfo(
                        vmId,
                        s.Name,
                        VmMapper.MapStateCodeToText(s.State), // 调用 Mapper
                        VmMapper.ParseOsTypeFromNotes(s.Notes), // 调用 Mapper
                        s.Cpu,
                        Math.Round(finalRam / 1024.0, 1),
                        diskStr,
                        TimeSpan.FromMilliseconds(s.Uptime))
                    {
                        Notes = s.Notes,
                        Generation = 0
                    });
                }

                return resultList.OrderByDescending(x => x.State == "运行中").ThenBy(x => x.Name).ToList();
            });
        }

        /// <summary>
        /// 获取所有虚拟机的实时内存数据
        /// </summary>
        public Dictionary<string, VmDynamicMemoryData> GetVmRuntimeMemoryData()
        {
            var map = new Dictionary<string, VmDynamicMemoryData>();
            try
            {
                var scope = new ManagementScope(@"root\virtualization\v2");
                // 同时查询 MemoryUsage (分配量) 和 MemoryAvailable (可用百分比)
                var query = new SelectQuery("SELECT Name, MemoryUsage, MemoryAvailable FROM Msvm_SummaryInformation");

                using var searcher = new ManagementObjectSearcher(scope, query);
                using var collection = searcher.Get();

                foreach (var item in collection)
                {
                    var vmId = item["Name"]?.ToString();
                    var usageObj = item["MemoryUsage"];
                    var availObj = item["MemoryAvailable"]; // 这是一个整数百分比 (例如 17)

                    if (vmId != null && usageObj != null)
                    {
                        long assigned = Convert.ToInt64(usageObj);

                        // 如果虚拟机刚启动或没装集成服务，Available 可能是 null
                        int available = 0;
                        if (availObj != null)
                        {
                            available = Convert.ToInt32(availObj);
                        }

                        map[vmId] = new VmDynamicMemoryData
                        {
                            AssignedMb = assigned,
                            AvailablePercent = available
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