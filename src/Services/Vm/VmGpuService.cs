using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;
using Renci.SshNet;

namespace ExHyperV.Services
{
    public class VmGpuService
    {
        private readonly VmQueryService _queryService;
        public VmGpuService(VmQueryService queryService)
        {
            _queryService = queryService;
        }

        private class VmDiskTarget
        {
            public bool IsPhysical { get; set; }
            public string Path { get; set; } = string.Empty;        // 虚拟文件的VHDX 路径
            public int PhysicalDiskNumber { get; set; } // 物理硬盘的 Disk Number (e.g. 0, 1, 2)
        }
        private const string ScriptBaseUrl = "https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/";

        public Task PrepareHostEnvironmentAsync()
        {
            return Task.Run(() =>
            {
                HyperVGpuPolicyService.AllowUnsupportedGpuAssignment();
            });
        }

        private int ExecuteCommand(string command)
        {
            try
            {
                Process process = new()
                {
                    StartInfo =
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();
                return process.ExitCode;
            }
            catch { return -1; }
        }

        public string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();

            // 1. 只去除开头的设备路径前缀，不破坏中间的井号
            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);

            // 2. 截掉 GUID 及其之后的内容
            int suffixIndex = normalizedId.IndexOf("{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);

            // 3. 将所有反斜杠统一换成井号，并清理末尾
            return normalizedId.Replace('\\', '#').TrimEnd('#');
        }

        // 挂载VHDX时寻找可用的盘符
        private char GetFreeDriveLetter()
        {
            var usedLetters = DriveInfo.GetDrives().Select(d => d.Name[0]).ToList();
            for (char c = 'Z'; c >= 'A'; c--)
            {
                if (!usedLetters.Contains(c))
                {
                    return c;
                }
            }
            throw new IOException(Properties.Resources.Error_NoAvailableDriveLetters);
        }

        #region 硬件信息与虚拟机查询
        public async Task<List<GpuInfo>> GetHostGpusAsync()
        {
            var pciInfoProvider = new PciIds();
            await pciInfoProvider.EnsureInitializedAsync();

            var gpuList = new List<GpuInfo>();

            // 1. 设备列表 + 完整 InstanceId + 厂商 + 驱动版本：走原生 CfgMgr 的"在位"枚举(Win32Api.GetPresentDisplayAdapters)。
            //    替代偶发数秒慢的 Win32_VideoController(它实探显示驱动、GPU 空闲会被唤醒)；FILTER_PRESENT 天然排除幻影设备。
            //    已在系统上用原生 CM_* 实测核对：仅列在位 GPU、DriverVersion 取自正确 DEVPKEY，与 Win32_VideoController 数据一致。
            var displayAdapters = await Task.Run(() => Win32Api.GetPresentDisplayAdapters());
            foreach (var (instanceId, name, manu, driverVersion) in displayAdapters)
            {
                if (string.IsNullOrEmpty(name)) continue;
                gpuList.Add(new GpuInfo
                {
                    Name = name,
                    Manu = string.IsNullOrEmpty(manu) ? pciInfoProvider.GetVendorFromInstanceId(instanceId) : manu,
                    InstanceId = instanceId,
                    DriverVersion = driverVersion,
                    Vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId)
                });
            }

            // 2. GPU RAM 注册表
            try
            {
                var gpuRamMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var classKey = baseKey.OpenSubKey(
                    @"SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (classKey != null)
                {
                    foreach (var subKeyName in classKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = classKey.OpenSubKey(subKeyName);
                            if (subKey == null) continue;
                            string matchingId = subKey.GetValue("MatchingDeviceId")?.ToString()?.ToUpper();
                            if (string.IsNullOrEmpty(matchingId)) continue;
                            long memSize = 0;
                            var qwMem = subKey.GetValue("HardwareInformation.qwMemorySize");
                            if (qwMem != null) try { memSize = Convert.ToInt64(qwMem); } catch { }
                            if (memSize == 0)
                            {
                                var mem = subKey.GetValue("HardwareInformation.MemorySize");
                                if (mem != null && mem is not byte[]) try { memSize = Convert.ToInt64(mem); } catch { }
                            }
                            if (memSize > 0) gpuRamMap[matchingId] = memSize;
                        }
                        catch { continue; }
                    }
                }

                foreach (var existingGpu in gpuList)
                {
                    var matched = gpuRamMap.FirstOrDefault(kv =>
                    {
                        Debug.WriteLine($"MatchingId: '{kv.Key}', GpuInstanceId: '{existingGpu.InstanceId.ToUpper()}'");
                        return existingGpu.InstanceId.ToUpper().Contains(kv.Key);
                    });
                    if (matched.Key != null)
                        existingGpu.Ram = matched.Value.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU RAM registry error: {ex.Message}");
            }

            // 3. Msvm_PartitionableGpu
            var partResp = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_PartitionableGpu",
                obj => obj["Name"]?.ToString() ?? "");

            if (partResp.HasData)
            {
                foreach (var pname in partResp.Data)
                {
                    if (string.IsNullOrEmpty(pname)) continue;
                    string normalizedPName = NormalizeDeviceId(pname);
                    var existingGpu = gpuList.FirstOrDefault(g =>
                        NormalizeDeviceId(g.InstanceId) == normalizedPName);
                    if (existingGpu != null) existingGpu.Pname = pname;
                }
            }

            return gpuList;
        }
        public async Task<List<(string Id, string InstancePath)>> GetVmGpuAdaptersAsync(string vmName)
        {
            var result = new List<(string Id, string InstancePath)>();
            string scopePath = @"\\.\root\virtualization\v2";
            try
            {
                var notesResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => obj["Notes"] is string[] arr ? string.Join("\n", arr) : obj["Notes"]?.ToString() ?? "");
                string vmNotes = notesResp.Data ?? "";

                string query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";
                using var searcher = new ManagementObjectSearcher(scopePath, query);
                using var vmCollection = searcher.Get();
                var computerSystem = vmCollection.Cast<ManagementObject>().FirstOrDefault();
                if (computerSystem == null) return result;

                using var relatedSettings = computerSystem.GetRelated(
                    "Msvm_VirtualSystemSettingData",
                    "Msvm_SettingsDefineState",
                    null, null, null, null, false, null);
                var virtualSystemSetting = relatedSettings.Cast<ManagementObject>().FirstOrDefault();
                if (virtualSystemSetting == null) return result;

                using var gpuSettingsCollection = virtualSystemSetting.GetRelated(
                    "Msvm_GpuPartitionSettingData",
                    "Msvm_VirtualSystemSettingDataComponent",
                    null, null, null, null, false, null);

                foreach (var gpuSetting in gpuSettingsCollection.Cast<ManagementObject>())
                {
                    string adapterId = gpuSetting["InstanceID"]?.ToString();
                    string instancePath = string.Empty;

                    string[] hostResources = (string[])gpuSetting["HostResource"];
                    if (hostResources != null && hostResources.Length > 0)
                    {
                        try
                        {
                            using var partitionableGpu = new ManagementObject(hostResources[0]);
                            partitionableGpu.Get();
                            instancePath = partitionableGpu["Name"]?.ToString();
                        }
                        catch { }
                    }

                    if (string.IsNullOrEmpty(instancePath) || instancePath.Contains("Unknown"))
                    {
                        string tagPrefix = "[AssignedGPU:";
                        int startIndex = vmNotes.IndexOf(tagPrefix);
                        if (startIndex != -1)
                        {
                            startIndex += tagPrefix.Length;
                            int endIndex = vmNotes.IndexOf("]", startIndex);
                            if (endIndex != -1)
                                instancePath = vmNotes.Substring(startIndex, endIndex - startIndex);
                        }
                    }

                    if (!string.IsNullOrEmpty(adapterId))
                        result.Add((adapterId, instancePath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI Query Error: {ex.Message}");
            }
            return result;
        }
        #endregion

        #region 虚拟机状态与控制管理
        // SSH重新连接
        private async Task<bool> WaitForVmToBeResponsiveAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromMinutes(1)) // 1分钟总超时
            {
                if (cancellationToken.IsCancellationRequested) return false;
                try
                {
                    using (var client = new TcpClient())
                    {
                        var connectTask = client.ConnectAsync(host, port);
                        if (await Task.WhenAny(connectTask, Task.Delay(2000, cancellationToken)) == connectTask)
                        {
                            await connectTask;
                            return true;
                        }
                    }
                }
                catch { }
                await Task.Delay(5000, cancellationToken);
            }
            return false; // 超时
        }

        /// <summary>检查VM配置，判断是否满足GPU-PV要求。</summary>
        /// <param name="vmName">待检查的VM名称</param>
        /// <returns>如果满足要求，返回true。否则返回false。</returns>

        public async Task<bool> CheckVmForGpuAsync(string vmName)
        {
            try
            {
                // 读裸 WMI 的真实属性：HighMmioGapSize(MB) + GuestControlledCacheTypes。
                // 旧实现读 HighMemoryMappedIoSpace/HighMemoryMappedIoBaseAddress(字节)——那是 PowerShell Get-VM 对象的属性，
                // Msvm_VirtualSystemSettingData 上【根本不存在】(实测 6 台 VM 全 <属性不存在>) → 恒判"未配置" → 每次添加都白白强制关机+重优化。
                var r = await WmiApi.QueryFirstAsync(
                    $"SELECT HighMmioGapSize, GuestControlledCacheTypes FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => (
                        HighMmioMb: Convert.ToUInt64(obj["HighMmioGapSize"] ?? 0),
                        Cache: Convert.ToBoolean(obj["GuestControlledCacheTypes"] ?? false)));

                if (!r.HasData) return false;
                if (!r.Data.Cache) return false;

                // 与 ConfigureMmio 会写入的目标比对(同一 ComputeMmioPlan、主机自适应)：已 >= 目标高位 MMIO 间隙即视为达标、无需重配。
                ulong targetMb = VmMmioService.ComputeMmioPlan()?.HighSizeMb ?? 30720UL; // 读不到主机上限时回退 30GB(MB)
                return r.Data.HighMmioMb >= targetMb;
            }
            catch { return false; }
        }
        public async Task<bool> OptimizeVmForGpuAsync(string vmName)
        {
            return await VmMmioService.ConfigureMmioAsync(vmName);
        }

        #endregion

        #region 磁盘与分区操作
        private async Task<List<VmDiskTarget>> GetAllVmHardDrivesAsync(string vmName)
        {
            var vm = new VmInstance(Guid.Empty, vmName);
            await VmStorageService.LoadVmStorageItemsAsync(vm);

            Debug.WriteLine($"[GPU] StorageItems count: {vm.StorageItems.Count}");
            foreach (var item in vm.StorageItems)
                Debug.WriteLine($"[GPU] Item: DriveType={item.DriveType}, DiskType={item.DiskType}, Path={item.PathOrDiskNumber}, DiskNumber={item.DiskNumber}");

            return vm.StorageItems
                .Where(i => i.DriveType == "HardDisk")
                .Select(i => new VmDiskTarget
                {
                    IsPhysical = i.DiskType == "Physical",
                    Path = i.DiskType == "Physical" ? null : i.PathOrDiskNumber,
                    PhysicalDiskNumber = i.DiskType == "Physical" ? i.DiskNumber : 0
                })
                .ToList();
        }

        public async Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName)
        {
            return await Task.Run(async () =>
            {
                var allPartitions = new List<PartitionInfo>();
                var diskTargets = await GetAllVmHardDrivesAsync(vmName);

                foreach (var target in diskTargets)
                {
                    int hostDiskNumber = -1;
                    try
                    {
                        if (target.IsPhysical)
                        {
                            await HostDiskService.SetDiskOfflineStatusAsync(target.PhysicalDiskNumber, false);
                            await HostDiskService.SetDiskReadOnlyAsync(target.PhysicalDiskNumber, true);
                            await Task.Delay(500);
                            hostDiskNumber = target.PhysicalDiskNumber;
                        }
                        else
                        {
                            var mountResult = await VmStorageService.MountVhdxAsync(target.Path);
                            if (mountResult.Success)
                                hostDiskNumber = mountResult.DiskNumber;
                        }

                        if (hostDiskNumber != -1)
                        {
                            var diskParser = new DiskParser();
                            var devicePath = $@"\\.\PhysicalDrive{hostDiskNumber}";
                            var partitions = diskParser.GetPartitions(devicePath);

                            foreach (var p in partitions)
                            {
                                p.DiskPath = target.IsPhysical ? target.PhysicalDiskNumber.ToString() : target.Path;
                                p.DiskDisplayName = target.IsPhysical ? $"Physical Disk {target.PhysicalDiskNumber}" : Path.GetFileName(target.Path);
                                p.IsPhysicalDisk = target.IsPhysical;
                                allPartitions.Add(p);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(string.Format(Properties.Resources.Error_Format_FailMsg, $"{target.Path ?? "Physical"}: {ex.Message}"));
                    }
                    finally
                    {
                        if (target.IsPhysical)
                        {
                            await HostDiskService.SetDiskReadOnlyAsync(target.PhysicalDiskNumber, false);
                            await HostDiskService.SetDiskOfflineStatusAsync(target.PhysicalDiskNumber, true);
                        }
                        else if (!string.IsNullOrEmpty(target.Path))
                        {
                            await VmStorageService.DismountVhdxAsync(target.Path);
                        }
                    }
                }
                return allPartitions;
            });
        }
        #endregion

        #region GPU 分配与解绑
        public async Task<(bool Success, string Message)> AssignGpuPartitionAsync(string vmName, string gpuInstancePath)
        {
            try
            {
                // 1. 查 Msvm_PartitionableGpu 拿 WMI 对象路径
                var gpuResp = await WmiApi.QueryAsync(
                    "SELECT * FROM Msvm_PartitionableGpu",
                    obj => new { WmiPath = obj.Path.Path, Name = obj["Name"]?.ToString() ?? "" });
                if (!gpuResp.HasData) return (false, Properties.Resources.Error_Gpu_NoPartition);
                var matched = gpuResp.Data.FirstOrDefault(g =>
                    string.Equals(g.Name, gpuInstancePath, StringComparison.OrdinalIgnoreCase));
                if (matched == null) return (false, Properties.Resources.Error_Gpu_NoPartition);
                string gpuWmiPath = matched.WmiPath;

                // 2. 查 ResourcePool → AllocationCapabilities → Default 模板
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var poolSearcher = new ManagementObjectSearcher(ms,
                    new ObjectQuery("SELECT * FROM Msvm_ResourcePool WHERE ResourceType = 32770 AND ResourceSubType = 'Microsoft:Hyper-V:GPU Partition' AND Primordial = TRUE"));
                using var poolCollection = poolSearcher.Get();
                using var pool = poolCollection.Cast<ManagementObject>().FirstOrDefault();
                if (pool == null) return (false, Properties.Resources.Error_Gpu_NoPartition);

                using var capsCollection = pool.GetRelated("Msvm_AllocationCapabilities");
                using var caps = capsCollection.Cast<ManagementObject>().FirstOrDefault();
                if (caps == null) return (false, Properties.Resources.Error_Gpu_NoPartition);

                using var templateCollection = caps.GetRelated("Msvm_GpuPartitionSettingData");
                using var template = templateCollection.Cast<ManagementObject>()
                    .FirstOrDefault(t => t["InstanceID"]?.ToString()?.EndsWith("\\Default", StringComparison.OrdinalIgnoreCase) == true);
                if (template == null) return (false, Properties.Resources.Error_Gpu_NoPartition);

                // 3. 设置 HostResource 为 WMI 路径，清空 InstanceID
                template["InstanceID"] = null;
                template["HostResource"] = new string[] { gpuWmiPath };
                string gpuXml = template.GetText(TextFormat.CimDtd20);

                // 4. 查 VM SettingData 路径
                var vmSettingResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => obj.Path.Path);
                if (!vmSettingResp.HasData) return (false, Properties.Resources.Error_Vm_GetSettings);

                // 5. AddResourceSettings
                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "AddResourceSettings",
                    p =>
                    {
                        p["AffectedConfiguration"] = vmSettingResp.Data;
                        p["ResourceSettings"] = new string[] { gpuXml };
                    });

                if (!result.Success) return (false, result.Error);

                // 6. 验证
                var vmGuidResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                    obj => obj["Name"]?.ToString() ?? "");
                string vmGuid = vmGuidResp.Data ?? "";

                var verifyResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_GpuPartitionSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
                    obj => obj["InstanceID"]?.ToString() ?? "");
                if (!verifyResp.HasData) return (false, Properties.Resources.Error_Gpu_NoPartition);

                return (true, "OK");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
        public async Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId)
        {
            try
            {
                // 1. 查 GpuPartitionSettingData 拿对象路径
                string escapedId = adapterId.Replace("\\", "\\\\");
                var adapterResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_GpuPartitionSettingData WHERE InstanceID = '{escapedId}'",
                    obj => obj.Path.Path);
                if (!adapterResp.HasData) return false;

                // 2. RemoveResourceSettings
                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "RemoveResourceSettings",
                    p => p["ResourceSettings"] = new string[] { adapterResp.Data });
                if (!result.Success) return false;

                // 3. Notes 清理
                await WmiApi.WithObjectAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj =>
                    {
                        string current = obj["Notes"] is string[] arr ? string.Join("\n", arr) : obj["Notes"]?.ToString() ?? "";
                        if (Regex.IsMatch(current, @"\[AssignedGPU:[^\]]+\]"))
                            obj["Notes"] = new string[] { Regex.Replace(current, @"\[AssignedGPU:[^\]]+\]", "").Trim() };
                    });

                return true;
            }
            catch { return false; }
        }        
        #endregion

        #region Windows 驱动环境注入
        public async Task<(bool Success, string Message)> SyncWindowsDriversAsync(
            string vmName,
            string gpuInstancePath,
            string gpuManu,
            PartitionInfo selectedPartition,
            Action<string> progressCallback = null)
        {
            if (selectedPartition == null) return (false, Properties.Resources.Error_Common_NoPartitionSelected);

            var diskTarget = new VmDiskTarget
            {
                IsPhysical = selectedPartition.IsPhysicalDisk,
                Path = selectedPartition.IsPhysicalDisk ? null : selectedPartition.DiskPath,
                PhysicalDiskNumber = selectedPartition.IsPhysicalDisk ? int.Parse(selectedPartition.DiskPath) : 0
            };

            string result = await InjectWindowsDriversAsync(vmName, diskTarget, selectedPartition, gpuManu, gpuInstancePath, progressCallback);
            return result == "OK" ? (true, "OK") : (false, result);
        }

        private async Task<string> InjectWindowsDriversAsync(
            string vmName, VmDiskTarget diskTarget, PartitionInfo partition, string gpuManu, string gpuInstancePath, Action<string> progressCallback = null)
        {
            string assignedDriveLetter = null;
            int hostDiskNumber = -1;
            string savedCtrlType = "SCSI";
            int savedCtrlNum = 0;
            int savedCtrlLoc = 0;
            bool isPhysical = diskTarget.IsPhysical;
            bool detachSuccess = false;


            void Log(string msg) => progressCallback?.Invoke(msg);

            try
            {
                if (isPhysical)
                {
                    Log(string.Format(Properties.Resources.Msg_Gpu_DismountingDisk, diskTarget.PhysicalDiskNumber));
                    hostDiskNumber = diskTarget.PhysicalDiskNumber;
                    var detachResult = await VmStorageService.DetachPhysicalDiskAsync(vmName, hostDiskNumber);
                    if (!detachResult.Success) return Properties.Resources.Error_Gpu_DiskNotFound;
                    savedCtrlType = detachResult.CtrlType;
                    savedCtrlNum = detachResult.CtrlNum;
                    savedCtrlLoc = detachResult.CtrlLoc;
                    detachSuccess = true;



                    await HostDiskService.SetDiskOfflineStatusAsync(hostDiskNumber, false);
                    await HostDiskService.SetDiskReadOnlyAsync(hostDiskNumber, false);

                }
                else
                {
                    var mountResult = await VmStorageService.MountVhdxAsync(diskTarget.Path);
                    if (!mountResult.Success)
                        return Properties.Resources.Error_Gpu_MountVhdFailed;
                    hostDiskNumber = mountResult.DiskNumber;
                }

                Log(string.Format(Properties.Resources.Msg_Gpu_AssignTempDrive, hostDiskNumber, partition.PartitionNumber));

                char suggestedLetter = GetFreeDriveLetter();
                var assignResult = await VmStorageService.AssignPartitionDriveLetterAsync(hostDiskNumber, partition.PartitionNumber, suggestedLetter);
                if (!assignResult.Success)
                    return string.Format(Properties.Resources.Error_Gpu_InjectFailed, "Failed to assign drive letter");
                assignedDriveLetter = $"{suggestedLetter}:\\";

                var volResp = await WmiApi.QueryFirstCimAsync(
                    $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{assignedDriveLetter[0]}'",
                    obj => obj["FileSystem"]?.ToString() ?? "",
                    WmiScope.Storage);

                if (volResp.HasData && string.IsNullOrWhiteSpace(volResp.Data))
                    return Properties.Resources.Error_Gpu_BitLocker;


                if (!Directory.Exists(Path.Combine(assignedDriveLetter, "Windows", "System32")))
                {
                    return string.Format(Properties.Resources.Error_Gpu_InvalidSystemPart, assignedDriveLetter);
                }

                string sourceFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
                string destFolder = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");

                if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);

                Log(Properties.Resources.Msg_Gpu_SyncingFiles);

                using (Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = "robocopy.exe",
                    Arguments = $"\"{sourceFolder}\" \"{destFolder}\" /E /R:1 /W:1 /MT:32 /NDL /NJH /NJS /NC /NS",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }))
                {
                    await p.WaitForExitAsync();
                }

                // 同步重活（注册表提取 + 各厂商 Promote*：内部对每个文件 spawn cmd.exe 并同步 WaitForExit 几十~上百次）
                // 挪到后台线程——否则作为 robocopy await 之后的续体跑在 UI 线程上，会冻结主界面（转圈/窗口都卡死）。
                // Log 回调仍在 UI 续体里执行（这些 Task.Run 之间的代码在 UI 线程），更新 task.Description 安全、无需 Dispatcher。
                await Task.Run(() => PromoteRegistryDefinedFiles(assignedDriveLetter)); // 微软注册表文件提取

                if (gpuManu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    Log(Properties.Resources.Msg_Gpu_InjectingReg);
                    await Task.Run(() =>
                    {
                        NvidiaReg(assignedDriveLetter);
                        PromoteNvidiaFiles(assignedDriveLetter);
                    });
                    await NvidiaProgramFoldersAsync(assignedDriveLetter, Log);
                }
                else if (gpuManu.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => PromoteIntelGpuFiles(assignedDriveLetter));
                }
                else if (gpuManu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuManu.Contains("Advanced", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => PromoteAmdGpuFiles(assignedDriveLetter));
                }
                else if (gpuManu.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || gpuManu.Contains("QCOM", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => PromoteQualcommGpuFiles(assignedDriveLetter));
                }


                return "OK";
            }
            catch (Exception ex) { return string.Format(Properties.Resources.Error_Gpu_InjectFailed, ex.Message); }
            finally
            {
                if (isPhysical && hostDiskNumber != -1 && detachSuccess)
                {
                    Log(Properties.Resources.Msg_Gpu_Remounting);
                    try
                    {
                        await VmStorageService.RemoveAllPartitionAccessPathsAsync(hostDiskNumber);
                        await HostDiskService.SetDiskOfflineStatusAsync(hostDiskNumber, true);
                        await Task.Delay(1000);

                        await VmStorageService.AddDriveAsync(
                            vmName, savedCtrlType, savedCtrlNum, savedCtrlLoc,
                            "HardDisk", pathOrNumber: hostDiskNumber.ToString(),
                            isPhysical: true);
                        Log(Properties.Resources.Msg_Gpu_RemountSuccess);

                    }
                    catch (Exception ex) { Log(string.Format(Properties.Resources.Error_Gpu_RemountFailed, ex.Message)); }
                }
                else if (!string.IsNullOrEmpty(diskTarget?.Path))
                {
                    Log(Properties.Resources.Msg_Gpu_Unmounting);
                    await VmStorageService.DismountVhdxAsync(diskTarget.Path);
                }
            }
        }

        private void PromoteRegistryDefinedFiles(string assignedDriveLetter)
        {
            string classGuidPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var classKey = baseKey.OpenSubKey(classGuidPath);
                if (classKey == null) return;

                foreach (var subKeyName in classKey.GetSubKeyNames())
                {
                    using var subKey = classKey.OpenSubKey(subKeyName);
                    if (subKey == null) continue;
                    ProcessPromotionRegistryKey(subKey, "CopyToVmWhenNewer", assignedDriveLetter, "System32");
                    ProcessPromotionRegistryKey(subKey, "CopyToVmOverwrite", assignedDriveLetter, "System32");

                    if (Directory.Exists(Path.Combine(assignedDriveLetter, "Windows", "SysWOW64")))
                    {
                        ProcessPromotionRegistryKey(subKey, "CopyToVmWhenNewerWow64", assignedDriveLetter, "SysWOW64");
                        ProcessPromotionRegistryKey(subKey, "CopyToVmOverwriteWow64", assignedDriveLetter, "SysWOW64");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Universal Registry Promotion error: {ex.Message}");
            }
        }

        private void PromoteNvidiaFiles(string assignedDriveLetter)
        {
            // --- 1. System32 (64位核心) ---
            string s32 = "System32";
            LinkSingleFile(assignedDriveLetter, "MCU.exe", "MCU.exe", s32);
            LinkSingleFile(assignedDriveLetter, "nvapi64.dll", "nvapi64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcpl.dll", "nvcpl.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcuda_loader64.dll", "nvcuda.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcudadebugger.dll", "nvcudadebugger.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcuvid64.dll", "nvcuvid.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvdebugdump.exe", "nvdebugdump.exe", s32);
            LinkSingleFile(assignedDriveLetter, "nvEncodeAPI64.dll", "nvEncodeAPI64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "NvFBC64.dll", "NvFBC64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvidia-pcc.exe", "nvidia-pcc.exe", s32);
            LinkSingleFile(assignedDriveLetter, "nvidia-smi.exe", "nvidia-smi.exe", s32);
            LinkSingleFile(assignedDriveLetter, "NvIFR64.dll", "NvIFR64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvinfo.pb", "nvinfo.pb", s32);
            LinkSingleFile(assignedDriveLetter, "nvml_loader.dll", "nvml.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvofapi64.dll", "nvofapi64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "OpenCL64.dll", "OpenCL.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x64.dll", "vulkan-1.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x64.dll", "vulkan-1-999-0-0-0.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-x64.exe", "vulkaninfo.exe", s32);
            LinkSingleFile(assignedDriveLetter, "NV_DISP.CAT", "oem25.cat", @"System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");

            // --- 2. System32 特殊子目录 ---
            LinkSingleFile(assignedDriveLetter, "license.txt", "license.txt", @"System32\drivers\NVIDIA Corporation");
            LinkSingleFile(assignedDriveLetter, "dbInstaller.exe", "dbInstaller.exe", @"System32\drivers\NVIDIA Corporation\Drs");
            LinkSingleFile(assignedDriveLetter, "nvdrsdb.bin", "nvdrsdb.bin", @"System32\drivers\NVIDIA Corporation\Drs");

            // --- 3. lxss (WSL Linux 支持) ---
            string lxssPath = @"System32\lxss\lib";
            LinkSingleFile(assignedDriveLetter, "libcuda_loader.so", "libcuda.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libcuda_loader.so", "libcuda.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libcuda_loader.so", "libcuda.so.1.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libcudadebugger.so.1", "libcudadebugger.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvcuvid.so.1", "libnvcuvid.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvcuvid.so.1", "libnvcuvid.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvdxdlkernels.so", "libnvdxdlkernels.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-encode.so.1", "libnvidia-encode.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-encode.so.1", "libnvidia-encode.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-ml_loader.so", "libnvidia-ml.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-ngx.so.1", "libnvidia-ngx.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-opticalflow.so.1", "libnvidia-opticalflow.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-opticalflow.so.1", "libnvidia-opticalflow.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvoptix_loader.so.1", "libnvoptix.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvwgf2umx.so", "libnvwgf2umx.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "nvidia-ngx-updater", "nvidia-ngx-updater", lxssPath);
            LinkSingleFile(assignedDriveLetter, "nvidia-smi", "nvidia-smi", lxssPath);

            // --- 4. SysWOW64 (32位兼容层) ---
            string sw64 = "SysWOW64";
            LinkSingleFile(assignedDriveLetter, "nvapi.dll", "nvapi.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvcuda_loader32.dll", "nvcuda.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvcuvid32.dll", "nvcuvid.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvEncodeAPI.dll", "nvEncodeAPI.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "NvFBC.dll", "NvFBC.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "NvIFR.dll", "NvIFR.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvofapi.dll", "nvofapi.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "OpenCL32.dll", "OpenCL.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x86.dll", "vulkan-1.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x86.dll", "vulkan-1-999-0-0-0.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-x86.exe", "vulkaninfo.exe", sw64);
        }
        private void PromoteIntelGpuFiles(string assignedDriveLetter)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            string catPath = @"System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}";

            // --- 1. System32 (64位原生组件) ---
            // 基础管理与 API
            LinkSingleFile(assignedDriveLetter, "ControlLib.dll", "ControlLib.dll", s32);
            LinkSingleFile(assignedDriveLetter, "intel_gfx_api-x64.dll", "intel_gfx_api-x64.dll", s32);

            // 视频加速核心 (Intel Media SDK / VPL)
            LinkSingleFile(assignedDriveLetter, "mfx_loader_dll_hw64.dll", "libmfxhw64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vpl_dispatcher_64.dll", "libvpl.dll", s32);
            LinkSingleFile(assignedDriveLetter, "mfxplugin64_hw.dll", "mfxplugin64_hw.dll", s32);

            // Vulkan 核心 
            LinkSingleFile(assignedDriveLetter, "vulkan-1-64.dll", "vulkan-1.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-64.dll", "vulkan-1-999-0-0-0.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-64.exe", "vulkaninfo.exe", s32);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-64.exe", "vulkaninfo-1-999-0-0-0.exe", s32);

            // 计算接口 (OneAPI / Level Zero)
            LinkSingleFile(assignedDriveLetter, "ze_intel_gpu_raytracing.dll", "ze_intel_gpu_raytracing.dll", s32);
            LinkSingleFile(assignedDriveLetter, "ze_loader.dll", "ze_loader.dll", s32);
            LinkSingleFile(assignedDriveLetter, "ze_tracing_layer.dll", "ze_tracing_layer.dll", s32);
            LinkSingleFile(assignedDriveLetter, "ze_validation_layer.dll", "ze_validation_layer.dll", s32);

            // 驱动证书映射 (CatRoot)
            LinkSingleFile(assignedDriveLetter, "igdlh.cat", "oem95.cat", catPath);
            LinkSingleFile(assignedDriveLetter, "igdlh.cat", "oem108.cat", catPath);


            // --- 2. SysWOW64 (32位兼容组件) ---
            // 基础管理
            LinkSingleFile(assignedDriveLetter, "ControlLib32.dll", "ControlLib32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "IntelControlLib32.dll", "IntelControlLib32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "intel_gfx_api-x86.dll", "intel_gfx_api-x86.dll", sw64);

            // 32位视频加速
            LinkSingleFile(assignedDriveLetter, "mfx_loader_dll_hw32.dll", "libmfxhw32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vpl_dispatcher_32.dll", "libvpl.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "mfxplugin32_hw.dll", "mfxplugin32_hw.dll", sw64);

            // 32位 Vulkan
            LinkSingleFile(assignedDriveLetter, "vulkan-1-32.dll", "vulkan-1.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-32.dll", "vulkan-1-999-0-0-0.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-32.exe", "vulkaninfo.exe", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-32.exe", "vulkaninfo-1-999-0-0-0.exe", sw64);
        }
        private void PromoteAmdGpuFiles(string assignedDriveLetter)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            string catPath = @"System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}";

            // --- 1. System32 (64位原生组件) ---
            // 核心渲染与 API 
            LinkSingleFile(assignedDriveLetter, "atidxxstub64.dll", "atidxx64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdxcstub64.dll", "amdxc64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdxc64.so", "amdxc64.so", s32); // WSL支持

            // 基础管理库
            LinkSingleFile(assignedDriveLetter, "amdadlx64.dll", "amdadlx64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdave64.dll", "amdave64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdgfxinfo64.dll", "amdgfxinfo64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdlvr64.dll", "amdlvr64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdpcom64.dll", "amdpcom64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amfrt64.dll", "amfrt64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atiadlxx.dll", "atiadlxx.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atimpc64.dll", "atimpc64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atisamu64.dll", "atisamu64.dll", s32);

            // 服务与工具
            LinkSingleFile(assignedDriveLetter, "amdsasrv64.dll", "amdsasrv64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdsacli64.dll", "amdsacli64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atieclxx.exe", "atieclxx.exe", s32);
            LinkSingleFile(assignedDriveLetter, "atieah64.exe", "atieah64.exe", s32);
            LinkSingleFile(assignedDriveLetter, "EEURestart.exe", "EEURestart.exe", s32);
            LinkSingleFile(assignedDriveLetter, "GameManager64.dll", "GameManager64.dll", s32);

            // 资源与数据
            LinkSingleFile(assignedDriveLetter, "atiapfxx.blb", "atiapfxx.blb", s32);
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", s32);
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", s32);
            LinkSingleFile(assignedDriveLetter, "AMDKernelEvents.mc", "AMDKernelEvents.man", s32); 
            LinkSingleFile(assignedDriveLetter, "detoured64.dll", "detoured.dll", s32);

            // 特殊子目录 (amdkmpfd)
            string amdSubDir = @"System32\AMD\amdkmpfd";
            LinkSingleFile(assignedDriveLetter, "amdkmpfd.ctz", "amdkmpfd.ctz", amdSubDir);
            LinkSingleFile(assignedDriveLetter, "amdkmpfd.itz", "amdkmpfd.itz", amdSubDir);
            LinkSingleFile(assignedDriveLetter, "amdkmpfd.stz", "amdkmpfd.stz", amdSubDir);

            // 驱动证书映射
            LinkSingleFile(assignedDriveLetter, "u0418637.cat", "oem43.cat", catPath);


            // --- 2. SysWOW64 (32位兼容组件) ---
            // 核心渲染
            LinkSingleFile(assignedDriveLetter, "atidxxstub32.dll", "atidxx32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdxcstub32.dll", "amdxc32.dll", sw64);

            // 基础管理库
            LinkSingleFile(assignedDriveLetter, "amdadlx32.dll", "amdadlx32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdave32.dll", "amdave32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdgfxinfo32.dll", "amdgfxinfo32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdlvr32.dll", "amdlvr32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdpcom32.dll", "amdpcom32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amfrt32.dll", "amfrt32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "atimpc32.dll", "atimpc32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "atisamu32.dll", "atisamu32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "GameManager32.dll", "GameManager32.dll", sw64);

            // 32位特殊命名映射
            LinkSingleFile(assignedDriveLetter, "atiadlxy.dll", "atiadlxx.dll", sw64); 
            LinkSingleFile(assignedDriveLetter, "detoured32.dll", "detoured.dll", sw64);

            // 资源
            LinkSingleFile(assignedDriveLetter, "atiapfxx.blb", "atiapfxx.blb", sw64);
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", sw64);
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", sw64);

            // --- 3. Vulkan 支持 (通用) ---
            LinkSingleFile(assignedDriveLetter, "amdvlk64.dll", "amdvlk64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdvlk64.dll", "vulkan-1.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdvlk32.dll", "vulkan-1.dll", sw64);
        }
        private void PromoteQualcommGpuFiles(string assignedDriveLetter)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            string sc32 = "SyChpe32";
            string catPath = @"System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}";

            // --- 1. System32 (原生 ARM64 组件) ---
            LinkSingleFile(assignedDriveLetter, "OpenCL.dll", "OpenCL.dll", s32);
            LinkSingleFile(assignedDriveLetter, "qcdxkmsuc8380.mbn", "qcdxkmsuc8380.mbn", s32);
            LinkSingleFile(assignedDriveLetter, "qchdcpumd8380.dll", "qchdcpumd8380.dll", s32);
            // 映射证书
            LinkSingleFile(assignedDriveLetter, "qcdx8380.cat", "oem7.cat", catPath);

            // --- 2. SysWOW64 (标准 x86 组件) ---
            LinkSingleFile(assignedDriveLetter, "qcdx11x86um.dll", "qcdx11x86um.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcdx12x86um.dll", "qcdx12x86um.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcdxdmlx86.dll", "qcdxdmlx86.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcdxsdx86.dll", "qcdxsdx86.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcegpx86.dll", "qcegpx86.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcgpux86compilercore.DLL", "qcgpux86compilercore.DLL", sw64);
            LinkSingleFile(assignedDriveLetter, "qcvidencx86um.DLL", "qcvidencum.DLL", sw64);

            // --- 3. SyChpe32 (CHPE 仿真加速组件) ---
            LinkSingleFile(assignedDriveLetter, "qcdx11chpeum.dll", "qcdx11x86um.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcdx12chpeum.dll", "qcdx12x86um.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcdxdmlchpe.dll", "qcdxdmlx86.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcdxsdchpe.dll", "qcdxsdx86.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcegpchpe.dll", "qcegpdx86.dll", sc32); // 注意：目标是 qcegpdx86.dll
            LinkSingleFile(assignedDriveLetter, "qcgpuchpecompilercore.dll", "qcgpux86compilercore.DLL", sc32);
        }
        private void ProcessPromotionRegistryKey(Microsoft.Win32.RegistryKey adapterKey, string subKeyName, string assignedDriveLetter, string targetSubDir)
        {
            using var promotionKey = adapterKey.OpenSubKey(subKeyName);
            if (promotionKey == null) return;

            foreach (var valName in promotionKey.GetValueNames())
            {
                var val = promotionKey.GetValue(valName);
                string sourceSearch = null;
                string targetLinkName = null;

                if (val is string[] pairs && pairs.Length > 0)
                {
                    sourceSearch = pairs[0];
                    targetLinkName = (pairs.Length > 1) ? pairs[1] : pairs[0];
                }
                else if (val is string single)
                {
                    sourceSearch = targetLinkName = single;
                }

                if (!string.IsNullOrEmpty(sourceSearch))
                {
                    LinkSingleFile(assignedDriveLetter, sourceSearch, targetLinkName, targetSubDir);
                }
            }
        }

        private void LinkSingleFile(string assignedDriveLetter, string sourceName, string targetName, string targetSubDir)
        {
            try
            {
                string guestRepo = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");
                string hostDestDir = Path.Combine(assignedDriveLetter, "Windows", targetSubDir);

                if (!Directory.Exists(hostDestDir))
                {
                    Directory.CreateDirectory(hostDestDir);
                }
                if (targetSubDir.Equals("System32", StringComparison.OrdinalIgnoreCase) ||
                    targetSubDir.Contains("SyChpe32", StringComparison.OrdinalIgnoreCase) ||
                    targetSubDir.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteCommand($"cmd /c takeown /f \"{hostDestDir}\" /a");
                    ExecuteCommand($"cmd /c icacls \"{hostDestDir}\" /grant *S-1-5-32-544:F");
                }

                string hostLinkPath = Path.Combine(hostDestDir, targetName);
                if (System.IO.File.Exists(hostLinkPath) || System.IO.Directory.Exists(hostLinkPath))
                {
                    return;
                }
                try
                {
                    if (System.IO.File.GetAttributes(hostLinkPath) != (System.IO.FileAttributes)(-1))
                    {
                        ExecuteCommand($"cmd /c del /f /q \"{hostLinkPath}\"");
                    }
                }
                catch {}

                var foundFiles = new DirectoryInfo(guestRepo)
                                    .GetFiles(sourceName, SearchOption.AllDirectories)
                                    .OrderByDescending(f => f.LastWriteTime)
                                    .ToList();

                if (foundFiles.Count == 0) return;

                string hostSourceFile = foundFiles[0].FullName;
                string guestInternalTarget = hostSourceFile.Replace(assignedDriveLetter, "C:");

                ExecuteCommand($"cmd /c mklink \"{hostLinkPath}\" \"{guestInternalTarget}\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Link error for {sourceName}: {ex.Message}");
            }
        }
        private string NvidiaReg(string letter)
        {
            string tempRegFile = Path.Combine(Path.GetTempPath(), $"nvlddmkm_{Guid.NewGuid()}.reg");
            string systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";

            try
            {
                ExecuteCommand($"reg unload HKLM\\OfflineSystem");

                string localKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm";
                if (ExecuteCommand($@"reg export ""{localKeyPath}"" ""{tempRegFile}"" /y") != 0) return Properties.Resources.Error_ExportLocalRegistryInfoFailed;
                if (ExecuteCommand($@"reg load HKLM\OfflineSystem ""{systemHiveFile}""") != 0) return Properties.Resources.Error_OfflineLoadVmRegistryFailed;

                string originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
                string targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
                string regContent = File.ReadAllText(tempRegFile);
                regContent = regContent.Replace(originalText, targetText);
                regContent = regContent.Replace("DriverStore", "HostDriverStore");
                File.WriteAllText(tempRegFile, regContent);
                ExecuteCommand($@"reg import ""{tempRegFile}""");

                return "OK";
            }
            catch (Exception ex)
            {
                return string.Format(Properties.Resources.Error_NvidiaRegistryProcessingException, ex.Message);
            }
            finally
            {
                ExecuteCommand($"reg unload HKLM\\OfflineSystem");
                if (File.Exists(tempRegFile)) File.Delete(tempRegFile);
            }
        }

        private async Task NvidiaProgramFoldersAsync(string assignedDriveLetter, Action<string> Log)
        {
            var sourceFolders = new List<string>
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation")
    };

            foreach (var sourcePath in sourceFolders)
            {
                if (!Directory.Exists(sourcePath)) continue;
                string root = Path.GetPathRoot(sourcePath);
                string relativePath = sourcePath.Substring(root.Length);

                string targetPath = Path.Combine(assignedDriveLetter, relativePath);

                Log?.Invoke(string.Format(Properties.Resources.Log_Gpu_Syncing, sourcePath, targetPath));

                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                }
                using (Process p = Process.Start(new ProcessStartInfo
                {
                    FileName = "robocopy.exe",
                    Arguments = $"\"{sourcePath}\" \"{targetPath}\" /E /R:1 /W:1 /MT:32 /XJ /NDL /NJH /NJS /NC /NS",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }))
                {
                    await p.WaitForExitAsync();
                }
            }
        }

        #endregion

        #region Linux 驱动环境与脚本部署
        private string FindGpuDriverSourcePath(string gpuInstancePath)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
            if (Directory.Exists(path)) return path;
            return @"C:\Windows\System32\DriverStore\FileRepository";
        }

        private async Task UploadLocalFilesAsync(SshCredentials credentials, string remoteDirectory)
        {
            string systemWslLibPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lxss", "lib");

            if (Directory.Exists(systemWslLibPath))
            {
                var allFiles = Directory.GetFiles(systemWslLibPath);
                foreach (var filePath in allFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    await SshService.UploadFileAsync(credentials, filePath, $"{remoteDirectory}/{fileName}");
                }
            }
        }

        public async Task<List<LinuxScriptItem>> GetAvailableScriptsAsync()
        {
            var allScripts = new List<LinuxScriptItem>();

            // --- 1. 扫描本地文件夹 ---
            string localFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserScripts");
            if (!Directory.Exists(localFolder)) Directory.CreateDirectory(localFolder);

            try
            {
                var files = Directory.GetFiles(localFolder, "*.sh");
                foreach (var file in files)
                {
                    string content = await File.ReadAllTextAsync(file);
                    var item = ParseScriptHeader(content);
                    item.IsLocal = true;
                    // 添加“本地”标识
                    item.Name = string.Format(Properties.Resources.VmGPUService_LogLocal, item.Name);
                    item.SourcePathOrUrl = file;
                    item.FileName = Path.GetFileName(file);
                    allScripts.Add(item);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Local script scan error: {ex.Message}"); }

            // --- 2. 远程扫描 (解析 index.json) ---
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                string jsonUrl = $"{ScriptBaseUrl}index.json";
                var jsonString = await httpClient.GetStringAsync(jsonUrl);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var remoteScripts = JsonSerializer.Deserialize<List<LinuxScriptItem>>(jsonString, options);

                if (remoteScripts != null)
                {
                    foreach (var item in remoteScripts)
                    {
                        item.IsLocal = false;
                        // 添加“在线”标识
                        item.Name = string.Format(Properties.Resources.VmGPUService_LogOnline, item.Name);
                        item.SourcePathOrUrl = $"{ScriptBaseUrl}{item.FileName}";
                        allScripts.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Remote script index fetch failed: {ex.Message}");
            }

            // 排序保持不变：本地优先，同类按名称排序
            return allScripts
                .OrderByDescending(x => x.IsLocal)
                .ThenBy(x => x.Name)
                .ToList();
        }

        private LinuxScriptItem ParseScriptHeader(string content)
        {
            var item = new LinuxScriptItem();
            item.Name = Regex.Match(content, @"# @Name:\s*(.*)").Groups[1].Value.Trim();
            item.Description = Regex.Match(content, @"# @Description:\s*(.*)").Groups[1].Value.Trim();
            item.Author = Regex.Match(content, @"# @Author:\s*(.*)").Groups[1].Value.Trim();
            item.Version = Regex.Match(content, @"# @Version:\s*(.*)").Groups[1].Value.Trim();

            if (string.IsNullOrEmpty(item.Name)) item.Name = "Unknown Script";
            return item;
        }

        // 支持重启循环的部署函数
        public Task<string> ProvisionLinuxGpuAsync(string vmName, LinuxScriptItem script, SshCredentials credentials, Action<string> progressCallback, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                void Log(string msg) => progressCallback?.Invoke(msg);

                try
                {
                    // --- 阶段 1: 准备工作 (IP 嗅探与连接) ---
                    var currentState = await _queryService.GetVmStateAsync(vmName);
                    if (currentState != "2")
                    {
                        Log(Properties.Resources.Log_Gpu_StartingVm);
                        await VmPowerService.ExecuteControlActionAsync(vmName, "Start");
                        await Task.Delay(5000);
                    }

                    Log(Properties.Resources.Msg_Gpu_LinuxWaitingIp);
                    var adapters = await VmNetworkService.GetNetworkAdaptersAsync(vmName);
                    if (adapters == null || adapters.Count == 0) return Properties.Resources.Error_Gpu_NoMac;
                    string macAddress = adapters[0].MacAddress;
                    string vmIpAddress = await VmIpService.Lookup(vmName, macAddress);
                    string targetIp = Ipv4.SelectBest(!string.IsNullOrWhiteSpace(credentials.Host) ? credentials.Host : vmIpAddress);

                    if (string.IsNullOrEmpty(targetIp)) return Properties.Resources.Error_Gpu_NoIpv4;
                    credentials.Host = targetIp;

                    if (!await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, ct))
                        return Properties.Resources.Error_Gpu_SshTimeout;

                    // --- 阶段 2: 文件上传 (驱动与 WSL 库) ---
                    string remoteTempDir = "/tmp/exhyperv_deploy";
                    using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                    {
                        client.Connect();
                        client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                        client.Disconnect();
                    }

                    Log(Properties.Resources.Log_Gpu_UploadingDriverWsl);
                    string sourceDriverPath = FindGpuDriverSourcePath(string.Empty);
                    await SshService.UploadDirectoryAsync(credentials, sourceDriverPath, $"{remoteTempDir}/drivers");
                    await UploadLocalFilesAsync(credentials, $"{remoteTempDir}/lib");

                    // --- 阶段 3: 处理自选脚本 ---
                    string remoteScriptPath = $"{remoteTempDir}/{script.FileName}";

                    // 1. 重新计算代理前缀
                    string proxyEnv = string.Empty;
                    if (credentials.UseProxy && !string.IsNullOrEmpty(credentials.ProxyHost))
                    {
                        string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";
                        // 注入常用的环境变量，强制 wget/curl 走代理
                        proxyEnv = $"http_proxy='{proxyUrl}' https_proxy='{proxyUrl}' HTTP_PROXY='{proxyUrl}' HTTPS_PROXY='{proxyUrl}' ";
                    }

                    if (script.IsLocal)
                    {
                        Log(string.Format(Properties.Resources.Log_Gpu_UploadingLocalScript, script.Name));
                        await SshService.UploadFileAsync(credentials, script.SourcePathOrUrl, remoteScriptPath);
                    }
                    else
                    {
                        Log(string.Format(Properties.Resources.Log_Gpu_DownloadingScript, script.Name));
                        // 使用 sh -c 包裹，确保环境变量对后面的命令生效
                        string downloadCmd = $"{proxyEnv}sh -c \"wget -q -O {remoteScriptPath} {script.SourcePathOrUrl} || curl -fL {script.SourcePathOrUrl} -o {remoteScriptPath}\"";

                        await SshService.ExecuteSingleCommandAsync(credentials, downloadCmd, Log);
                    }
                    await SshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteScriptPath}", Log);
                    // --- 阶段 4: 状态机执行循环 ---
                    bool isSuccess = false;
                    int maxAttempts = 3;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (ct.IsCancellationRequested) return Properties.Resources.Msg_Gpu_Cancelled;

                        bool rebootNeeded = false;
                        string graphicsArg = credentials.InstallGraphics ? "true" : "false";
                        // 代理地址单引号包裹 + 转义,防 ProxyHost 含 $()/反引号注入(与上面密码同款转义);正常主机名行为等价
                        string proxyArg = credentials.UseProxy ? $"'http://{credentials.ProxyHost.Replace("'", "'\\''")}:{credentials.ProxyPort}'" : "\"\"";

                        // 使用 sudo -E 保证代理变量能传递给 apt
                        string execCmd = $"echo '{credentials.Password.Replace("'", "'\\''")}' | sudo -S -E -p '' bash {remoteScriptPath} deploy {graphicsArg} {proxyArg}";

                        Log(string.Format(Properties.Resources.Log_Gpu_ExecutingAttempt, attempt));

                        await SshService.ExecuteCommandAndCaptureOutputAsync(credentials, execCmd, line =>
                        {
                            Log(line);
                            if (line.Contains("[STATUS: SUCCESS]")) isSuccess = true;
                            if (line.Contains("[STATUS: REBOOT_REQUIRED]")) rebootNeeded = true;
                        }, TimeSpan.FromMinutes(60));

                        if (isSuccess) break;

                        if (rebootNeeded)
                        {
                            Log(Properties.Resources.Log_Gpu_RebootRequired);
                            await VmPowerService.ExecuteControlActionAsync(vmName, "Restart");
                            await Task.Delay(10000); // 等待开始关机
                            if (!await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, ct))
                                return Properties.Resources.Error_Gpu_RebootNoResponse;

                            Log(Properties.Resources.Log_Gpu_VmBackOnline);
                            continue; // 重新进入循环执行同一脚本
                        }

                        // 如果既没成功也没重启信号，通常是脚本内部报错 set -e 触发了
                        if (!isSuccess) return Properties.Resources.Error_Gpu_ScriptNoSuccess;
                    }

                    return isSuccess ? "OK" : Properties.Resources.Error_Gpu_MaxReboots;

                }
                catch (Exception ex) { return string.Format(Properties.Resources.Error_Gpu_LinuxGeneric, ex.Message); }
            });
        }
        #endregion
    }
}