using ExHyperV.Models;
using ExHyperV.Tools;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
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
            var diskTask = WmiTools.QueryAsync(QueryDiskAllocations, obj => new {
                InstanceID = obj["InstanceID"]?.ToString() ?? "",
                Parent = obj["Parent"]?.ToString() ?? "",
                Paths = obj["HostResource"] as string[]
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
                    string vmGuid = ExtractFirstGuid(setting.InstanceID);
                    if (vmGuid != null && setting.HostResources?.Length > 0)
                    {
                        string assignedPath = setting.HostResources[0];
                        string finalName = "GPU-PV Device";
                        string shortId = null;

                        if (hostPathToPciIdMap.TryGetValue(assignedPath, out var id)) shortId = id;
                        else shortId = ExtractPciId(assignedPath);

                        if (!string.IsNullOrEmpty(shortId))
                        {
                            if (pciToFriendlyNameMap.TryGetValue(shortId, out var friendly)) finalName = friendly;
                            else finalName = $"Unknown Device ({shortId})";
                        }
                        gpuMap[vmGuid] = finalName;
                    }
                }

                var resultList = new List<VmInstanceInfo>();
                foreach (var s in summaries)
                {
                    Guid.TryParse(s.Id, out var vmId);
                    string vmGuid = s.Id?.Trim('{', '}').ToUpper();

                    var vmDiskSizes = new List<long>();
                    var myDisks = allDiskAllocations.Where(d => d.Parent.ToUpper().Contains(vmGuid) || d.InstanceID.ToUpper().Contains(vmGuid)).ToList();
                    foreach (var d in myDisks)
                    {
                        if (d.Paths != null && d.Paths.Length > 0)
                        {
                            string path = d.Paths[0].Replace("\"", "").Trim();
                            if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                            {
                                long size = GetDiskCapacityWithDiscUtils(path, s.Name);
                                if (size > 0) vmDiskSizes.Add(size);
                            }
                        }
                    }

                    double startupRam = memSettings.FirstOrDefault(m => m.FullId?.Contains(s.Id, StringComparison.OrdinalIgnoreCase) == true)?.StartupRam ?? 0;
                    bool isRunning = VmMapper.IsRunning(s.State);
                    bool hasValidRealtimeMem = s.MemUsage > 0;

                    double usageRam = (isRunning && hasValidRealtimeMem) ? s.MemUsage : 0;
                    double assignedRam = (isRunning && hasValidRealtimeMem) ? s.MemUsage : startupRam;

                    int genValue = 0; string verValue = "0.0";
                    if (vmGuid != null && configMap.TryGetValue(vmGuid, out var config)) { genValue = config.Gen; verValue = config.Ver; }

                    string gpuName = vmGuid != null && gpuMap.ContainsKey(vmGuid) ? gpuMap[vmGuid] : null;

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
                var parameters = new Dictionary<string, object>
                {
                    { "SystemSettings", embeddedInstance }
                };

                var result = await WmiTools.ExecuteMethodAsync(serviceWql, "ModifySystemSettings", parameters);
                return result.Success;
            }
            catch
            {
                return false;
            }
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
            var result = await WmiTools.QueryAsync(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController",
                item => new { Name = item["Name"]?.ToString(), PnpId = item["PNPDeviceID"]?.ToString() },
                WmiTools.CimV2Scope
            );

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in result)
            {
                string shortId = ExtractPciId(item.PnpId);
                if (!string.IsNullOrEmpty(shortId) && !string.IsNullOrEmpty(item.Name) && !map.ContainsKey(shortId))
                    map[shortId] = item.Name;
            }
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
    }
}