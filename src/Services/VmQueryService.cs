using ExHyperV.Models;
using ExHyperV.Tools;
using System.Diagnostics;
using System.IO;
using System.Management;
using DiscUtils;
using DiscUtils.Streams; // 必须引用这个以获得 Ownership 枚举

namespace ExHyperV.Services
{
    public class VmQueryService
    {
        public struct VmDynamicMemoryData { public long AssignedMb; public int AvailablePercent; }

        private const string QuerySummary = "SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation";
        private const string QueryMemSettings = "SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData WHERE ResourceType = 4";
        private const string QueryDiskAllocations = "SELECT InstanceID, Parent, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31";

        private static readonly Dictionary<string, long> _diskSizeCache = new();

        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            return await Task.Run(() =>
            {
                Debug.WriteLine("\n========== [Storage Sync Start] ==========");
                var allDiskAllocations = Utils.Wmi.Query(QueryDiskAllocations, obj => new {
                    InstanceID = obj["InstanceID"]?.ToString() ?? "",
                    Parent = obj["Parent"]?.ToString() ?? "",
                    Paths = obj["HostResource"] as string[]
                });

                var summaries = Utils.Wmi.Query(QuerySummary, obj => new {
                    Id = obj["Name"]?.ToString(),
                    Name = obj["ElementName"]?.ToString(),
                    State = (ushort)(obj["EnabledState"] ?? 0),
                    Cpu = Convert.ToInt32(obj["NumberOfProcessors"] ?? 1),
                    MemUsage = Convert.ToDouble(obj["MemoryUsage"] ?? 0),
                    Uptime = (ulong)(obj["UpTime"] ?? 0),
                    Notes = obj["Notes"]?.ToString() ?? string.Empty
                });

                var memSettings = Utils.Wmi.Query(QueryMemSettings, obj => new {
                    FullId = obj["InstanceID"]?.ToString(),
                    StartupRam = Convert.ToDouble(obj["VirtualQuantity"] ?? 0)
                });

                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    string vmGuid = s.Id.ToUpper();
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

                    double finalRam = VmMapper.IsRunning(s.State) && s.MemUsage > 0 ? s.MemUsage : (memSettings.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0);

                    var vmInfo = new VmInstanceInfo(vmId, s.Name)
                    {
                        OsType = Utils.GetTagValue(s.Notes, "OSType") ?? "Windows",
                        CpuCount = s.Cpu,
                        MemoryGb = Math.Round(finalRam / 1024.0, 1),
                        AssignedMemoryGb = Math.Round(finalRam / 1024.0, 1),
                        DiskSizeRaw = vmDiskSizes,
                        Notes = s.Notes
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

                // 显式以共享读写模式打开，防止文件被 Hyper-V 锁死
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    VirtualDisk disk = null;

                    // 根据扩展名手动分发构造函数
                    if (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase))
                    {
                        disk = new DiscUtils.Vhdx.Disk(fs, Ownership.None);
                    }
                    else if (path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avhd", StringComparison.OrdinalIgnoreCase))
                    {
                        disk = new DiscUtils.Vhd.Disk(fs, Ownership.None);
                    }

                    if (disk != null)
                    {
                        using (disk)
                        {
                            long cap = disk.Capacity;
                            Debug.WriteLine($"[DiscUtils] {vmName} -> {Path.GetFileName(path)} | 设定容量: {cap / 1073741824.0:N2} GB");
                            _diskSizeCache[path] = cap;
                            return cap;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DiscUtils Error] 处理 {path} 失败: {ex.Message}");
            }

            // 兜底：如果 DiscUtils 无法识别，返回物理大小
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