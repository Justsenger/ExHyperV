using System.Collections.Concurrent;
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
        private readonly AsyncLocal<ConcurrentQueue<string>?> _linkWarnings = new();
        private readonly AsyncLocal<GpuDriverPackagePlan?> _activeDriverPackagePlan = new();

        private enum FilePromotionMode
        {
            PreserveExisting,
            WhenNewer,
            Overwrite
        }

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

            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);

            int suffixIndex = normalizedId.IndexOf("{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);

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

            // 设备列表 + 完整 InstanceId + 厂商 + 驱动版本：走原生 CfgMgr 的"在位"枚举(Win32Api.GetPresentDisplayAdapters)。
            //    替代偶发数秒慢的 Win32_VideoController(它实探显示驱动、GPU 空闲会被唤醒)；FILTER_PRESENT 天然排除幻影设备。
            //    已在系统上用原生 CM_* 实测核对：仅列在位 GPU、DriverVersion 取自正确 DEVPKEY，与 Win32_VideoController 数据一致。
            var displayAdapters = await Task.Run(() => Win32Api.GetPresentDisplayAdapters());
            foreach (var (instanceId, name, manu, driverVersion) in displayAdapters)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId, manu);
                gpuList.Add(new GpuInfo
                {
                    Name = name,
                    Manu = string.IsNullOrEmpty(manu) ? vendor : manu,
                    InstanceId = instanceId,
                    DriverVersion = driverVersion,
                    Vendor = vendor,
                    MemoryBytes = GpuMemoryResolver.ReadBytes(instanceId, manu, vendor)
                });
            }

            // Msvm_PartitionableGpu
            var partResp = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_PartitionableGpu",
                obj => obj["Name"]?.ToString() ?? "");

            if (partResp.HasData)
            {
                foreach (var partitionableGpuPath in partResp.Data)
                {
                    if (string.IsNullOrEmpty(partitionableGpuPath)) continue;
                    string normalizedPartitionableGpuPath = NormalizeDeviceId(partitionableGpuPath);
                    var existingGpu = gpuList.FirstOrDefault(g =>
                        NormalizeDeviceId(g.InstanceId) == normalizedPartitionableGpuPath);
                    if (existingGpu != null) existingGpu.PartitionableGpuPath = partitionableGpuPath;
                }
            }

            return gpuList;
        }
        public async Task<List<(string Id, string InstancePath)>> GetVmGpuAdaptersAsync(string vmName)
        {
            var result = await TryGetVmGpuAdaptersAsync(vmName);
            return result.Adapters;
        }

        public async Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> TryGetVmGpuAdaptersAsync(string vmName)
        {
            var result = new List<(string Id, string InstancePath)>();
            string scopePath = @"\\.\root\virtualization\v2";
            try
            {
                // Win10 早期 Msvm_GpuPartitionSettingData.HostResource 读不回 → 无法按分区对应物理 GPU。
                // 取主机首张(通常也是唯一)可分区 GPU 的 Name 兜底显示参数：单卡准确；多卡是引擎启动时自选、
                // 本就无法精确对应的固有局限(禁用其他卡强选的老思路已不在)。Win11 走 HostResource 精确路径、到不了这兜底。
                var partGpuResp = await WmiApi.QueryAsync(
                    "SELECT Name FROM Msvm_PartitionableGpu",
                    obj => obj["Name"]?.ToString() ?? "");
                string firstPartitionableGpu = partGpuResp.Data?.FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";

                string query = $"SELECT * FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(vmName)}";
                using var searcher = new ManagementObjectSearcher(scopePath, query);
                using var vmCollection = searcher.Get();
                var computerSystem = vmCollection.Cast<ManagementObject>().FirstOrDefault();
                if (computerSystem == null) return (false, result);

                using var relatedSettings = computerSystem.GetRelated(
                    "Msvm_VirtualSystemSettingData",
                    "Msvm_SettingsDefineState",
                    null, null, null, null, false, null);
                var virtualSystemSetting = relatedSettings.Cast<ManagementObject>().FirstOrDefault();
                if (virtualSystemSetting == null) return (false, result);

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

                    // HostResource 空/Unknown(Win10 早期) → 兜底用首张可分区 GPU 的 Name(单卡准确)
                    if (string.IsNullOrEmpty(instancePath) || instancePath.Contains("Unknown"))
                        instancePath = firstPartitionableGpu;

                    if (!string.IsNullOrEmpty(adapterId))
                        result.Add((adapterId, instancePath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI Query Error: {ex.Message}");
                return (false, new List<(string Id, string InstancePath)>());
            }
            return (true, result);
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

        /// <summary>检查写合并缓存和高位 MMIO 是否满足当前 GPU-PV 配置。</summary>
        public async Task<bool> CheckVmForGpuAsync(string vmName)
        {
            try
            {
                // 读裸 WMI 的真实属性：HighMmioGapSize(MB) + GuestControlledCacheTypes。
                // 旧实现读 HighMemoryMappedIoSpace/HighMemoryMappedIoBaseAddress(字节)——那是 PowerShell Get-VM 对象的属性，
                // PowerShell 的 HighMemoryMappedIoSpace 属性不属于该 WMI 类，不能用于此处检测。
                var r = await WmiApi.QueryFirstAsync(
                    $"SELECT HighMmioGapSize, GuestControlledCacheTypes FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => (
                        HighMmioMb: Convert.ToUInt64(obj["HighMmioGapSize"] ?? 0),
                        Cache: Convert.ToBoolean(obj["GuestControlledCacheTypes"] ?? false)));

                if (!r.HasData) return false;
                if (!r.Data.Cache) return false;

                // 与 ConfigureMmio 会写入的目标比对(同一 ComputeMmioPlan、主机自适应)：已 >= 目标高位 MMIO 间隙即视为达标、无需重配。
                ulong targetMb = VmMmioService.ComputeMmioPlan()?.HighSizeMb ?? VmMmioService.DefaultHighSizeMb; // 读不到主机上限时回退默认 256G（同一常量）
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
        /// <summary>
        /// 分配 GPU 分区。<paramref name="sharePercent"/> 为份额上限百分比（1-100），
        /// 按比例缩放模板的 Max/Optimal 显存、编解码与计算字段；100 表示使用模板原值（整卡上限）。
        /// 份额限制的是"最多能用多少"，不是预留量：设 50% 只意味着高负载时性能上限约减半，
        /// 空闲时仍可使用整卡，多台虚拟机共享同一张卡时靠它避免互相挤占。
        /// </summary>
        public async Task<(bool Success, string Message)> AssignGpuPartitionAsync(
            string vmName,
            string gpuInstancePath,
            int sharePercent = 100)
        {
            try
            {
                // 查 Msvm_PartitionableGpu 拿 WMI 对象路径
                var gpuResp = await WmiApi.QueryAsync(
                    "SELECT * FROM Msvm_PartitionableGpu",
                    obj => new { WmiPath = obj.Path.Path, Name = obj["Name"]?.ToString() ?? "" });
                if (!gpuResp.HasData) return (false, Properties.Resources.Error_Gpu_NoPartition);
                var matched = gpuResp.Data.FirstOrDefault(g =>
                    string.Equals(g.Name, gpuInstancePath, StringComparison.OrdinalIgnoreCase));
                if (matched == null) return (false, Properties.Resources.Error_Gpu_NoPartition);
                string gpuWmiPath = matched.WmiPath;

                // 查 ResourcePool → AllocationCapabilities → Default / Maximum 模板
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var poolSearcher = new ManagementObjectSearcher(ms,
                    new ObjectQuery("SELECT * FROM Msvm_ResourcePool WHERE ResourceType = 32770 AND ResourceSubType = 'Microsoft:Hyper-V:GPU Partition' AND Primordial = TRUE"));
                using var poolCollection = poolSearcher.Get();
                using var pool = poolCollection.Cast<ManagementObject>().FirstOrDefault();
                if (pool == null) return (false, Properties.Resources.Error_Gpu_NoPartition);

                using var capsCollection = pool.GetRelated("Msvm_AllocationCapabilities");
                using var caps = capsCollection.Cast<ManagementObject>().FirstOrDefault();
                if (caps == null) return (false, Properties.Resources.Error_Gpu_NoPartition);

                // Default 模板用于 AddResourceSettings；Maximum 模板提供份额上限（Default 的份额字段为 null）。
                using var templateCollection = caps.GetRelated("Msvm_GpuPartitionSettingData");
                var templates = templateCollection.Cast<ManagementObject>().ToList();
                try
                {
                    var template = templates.FirstOrDefault(t =>
                        t["InstanceID"]?.ToString()?.EndsWith("\\Default", StringComparison.OrdinalIgnoreCase) == true);
                    var maximumTemplate = templates.FirstOrDefault(t =>
                        t["InstanceID"]?.ToString()?.EndsWith("\\Maximum", StringComparison.OrdinalIgnoreCase) == true);
                    if (template == null) return (false, Properties.Resources.Error_Gpu_NoPartition);

                    // 设置 HostResource 为 WMI 路径，清空 InstanceID
                    template["InstanceID"] = null;
                    template["HostResource"] = new string[] { gpuWmiPath };
                    ApplyPartitionShare(template, maximumTemplate, sharePercent);
                    string gpuXml = template.GetText(TextFormat.CimDtd20);

                    // AddResourceSettings：与 DDA 共用同一提交通道（目标配置解析也在共享层完成）
                    var result = await ResourceAssignmentService.AddForVmAsync(vmName, gpuXml);
                    if (!result.Success)
                        return (false, result.Error.Length > 0 ? result.Error : Properties.Resources.Error_Vm_GetSettings);

                    // 验证
                    var vmGuidResp = await WmiApi.QueryFirstAsync(
                        $"SELECT * FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(vmName)}",
                        obj => obj["Name"]?.ToString() ?? "");
                    string vmGuid = vmGuidResp.Data ?? "";

                    var verifyResp = await WmiApi.QueryFirstAsync(
                        $"SELECT * FROM Msvm_GpuPartitionSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
                        obj => obj["InstanceID"]?.ToString() ?? "");
                    if (!verifyResp.HasData) return (false, Properties.Resources.Error_Gpu_NoPartition);

                    return (true, "OK");
                }
                finally
                {
                    foreach (var t in templates) t.Dispose();
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
        /// <summary>
        /// 按份额百分比缩放分区模板的 Max/Optimal 字段。
        /// 上限必须取自 AllocationCapabilities 的 Maximum 定义模板：实测 Default 模板这 8 个字段全为 null，
        /// 直接读 Default 会让份额静默失效（50% 与 100% 效果相同）。
        /// 取不到上限时明确报错终止，不回退默认值、不静默成功。
        /// Min 字段保持模板原值，避免把下限压到无法保证基本显示输出的程度。
        /// </summary>
        private static void ApplyPartitionShare(
            ManagementObject template,
            ManagementObject? maximumTemplate,
            int sharePercent)
        {
            int clamped = Math.Clamp(sharePercent, 1, 100);
            if (clamped >= 100) return;
            if (maximumTemplate == null)
                throw new InvalidOperationException(Properties.Resources.GpuPv_Err_ShareTemplateMissing);

            double factor = clamped / 100.0;
            string[] scaledProperties =
            {
                "MaxPartitionVRAM", "OptimalPartitionVRAM",
                "MaxPartitionEncode", "OptimalPartitionEncode",
                "MaxPartitionDecode", "OptimalPartitionDecode",
                "MaxPartitionCompute", "OptimalPartitionCompute",
            };

            int applied = 0;
            foreach (string property in scaledProperties)
            {
                object? raw = null;
                try { raw = maximumTemplate[property]; }
                catch (ManagementException) { continue; }
                if (raw == null) continue;

                ulong value;
                try { value = Convert.ToUInt64(raw); }
                catch (Exception) { continue; }
                if (value == 0) continue;

                template[property] = (ulong)Math.Max(1, value * factor);
                applied++;
            }

            if (applied == 0)
                throw new InvalidOperationException(Properties.Resources.GpuPv_Err_ShareCeilingMissing);
        }

        public async Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId)
        {
            try
            {
                var adapterResp = await ResourceAssignmentService.FindSettingPathAsync(
                    "Msvm_GpuPartitionSettingData", adapterId);
                if (!adapterResp.HasData) return false;

                var result = await ResourceAssignmentService.RemoveAsync(adapterResp.Data!);
                if (!result.Success) return false;

                // Notes 清理
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

        /// <summary>注入标记：清理只认这个文件，避免误删来宾原有驱动。</summary>
        internal const string InjectionMarkerRelativePath = @"Windows\System32\HostDriverStore\.gpupv-inject.json";

        /// <summary>
        /// 清理本工具写入来宾的显卡驱动（离线挂载 + 只认注入标记）。
        /// 标记缺失时只列出候选目录并返回失败，绝不自动删除。
        /// </summary>
        public async Task<(bool Success, string Message)> CleanupWindowsDriversAsync(
            string vmName,
            PartitionInfo selectedPartition,
            Action<string> progressCallback = null)
        {
            if (selectedPartition == null) return (false, Properties.Resources.Error_Common_NoPartitionSelected);

            void Log(string msg) => progressCallback?.Invoke(msg);

            string assignedDriveLetter = null;
            int hostDiskNumber = -1;
            string savedCtrlType = "SCSI";
            int savedCtrlNum = 0;
            int savedCtrlLoc = 0;
            bool isPhysical = selectedPartition.IsPhysicalDisk;
            bool detachSuccess = false;

            try
            {
                if (isPhysical)
                {
                    hostDiskNumber = int.Parse(selectedPartition.DiskPath);
                    Log(string.Format(Properties.Resources.Msg_Gpu_DismountingDisk, hostDiskNumber));
                    var detachResult = await VmStorageService.DetachPhysicalDiskAsync(vmName, hostDiskNumber);
                    if (!detachResult.Success) return (false, Properties.Resources.Error_Gpu_DiskNotFound);
                    savedCtrlType = detachResult.CtrlType;
                    savedCtrlNum = detachResult.CtrlNum;
                    savedCtrlLoc = detachResult.CtrlLoc;
                    detachSuccess = true;

                    await HostDiskService.SetDiskOfflineStatusAsync(hostDiskNumber, false);
                    await HostDiskService.SetDiskReadOnlyAsync(hostDiskNumber, false);
                }
                else
                {
                    var mountResult = await VmStorageService.MountVhdxAsync(selectedPartition.DiskPath);
                    if (!mountResult.Success) return (false, Properties.Resources.Error_Gpu_MountVhdFailed);
                    hostDiskNumber = mountResult.DiskNumber;
                }

                Log(string.Format(Properties.Resources.Msg_Gpu_AssignTempDrive, hostDiskNumber, selectedPartition.PartitionNumber));
                char suggestedLetter = GetFreeDriveLetter();
                var assignResult = await VmStorageService.AssignPartitionDriveLetterAsync(
                    hostDiskNumber, selectedPartition.PartitionNumber, suggestedLetter);
                if (!assignResult.Success) return (false, Properties.Resources.GpuPv_Err_AssignDriveLetter);
                assignedDriveLetter = $"{suggestedLetter}:\\";

                string windowsDir = Path.Combine(assignedDriveLetter, "Windows", "System32");
                if (!Directory.Exists(windowsDir))
                {
                    return (false, string.Format(Properties.Resources.GpuPv_Err_NotWindowsPartition, assignedDriveLetter));
                }

                string markerPath = Path.Combine(assignedDriveLetter, InjectionMarkerRelativePath);
                string repositoryPath = Path.Combine(windowsDir, "HostDriverStore", "FileRepository");

                if (!File.Exists(markerPath))
                {
                    return (false,
                        Properties.Resources.GpuPv_Err_NoInjectionMarker +
                        Properties.Resources.GpuPv_Log_Candidates + DescribeCleanupCandidates(repositoryPath));
                }

                Log(Properties.Resources.GpuPv_Log_MarkerFound);
                var markerPackages = ReadMarkerPackages(markerPath);
                int removedPackages = 0;
                if (markerPackages.Count > 0 && Directory.Exists(repositoryPath))
                {
                    foreach (string packageName in markerPackages)
                    {
                        if (!string.Equals(Path.GetFileName(packageName), packageName, StringComparison.Ordinal)) continue;
                        string packagePath = Path.Combine(repositoryPath, packageName);
                        if (!Directory.Exists(packagePath)) continue;

                        RemoveReadOnlyAttribute(packagePath);
                        Directory.Delete(packagePath, true);
                        removedPackages++;
                        Log(string.Format(Properties.Resources.GpuPv_Log_PackageRemoved, packageName));
                    }

                    if (!Directory.EnumerateFileSystemEntries(repositoryPath).Any())
                        Directory.Delete(repositoryPath);
                }
                else if (Directory.Exists(repositoryPath))
                {
                    // 旧版标记没有包列表：HostDriverStore\FileRepository 是 GPU-PV 专用目录，整体删除
                    RemoveReadOnlyAttribute(repositoryPath);
                    Directory.Delete(repositoryPath, true);
                    Log(Properties.Resources.GpuPv_Log_RepositoryRemoved);
                }
                else
                {
                    Log(Properties.Resources.GpuPv_Log_RepositoryMissing);
                }

                File.Delete(markerPath);
                Log(Properties.Resources.GpuPv_Log_MarkerRemoved);
                return (true,
                    string.Format(Properties.Resources.GpuPv_Msg_CleanupSummary, removedPackages) +
                    Properties.Resources.GpuPv_Msg_CleanupScope);
            }
            catch (Exception ex)
            {
                return (false, string.Format(Properties.Resources.GpuPv_Err_CleanupFailed, ex.Message));
            }
            finally
            {
                if (isPhysical && hostDiskNumber != -1 && detachSuccess)
                {
                    Log(Properties.Resources.Msg_Gpu_Remounting);
                    try
                    {
                        if (!string.IsNullOrEmpty(assignedDriveLetter))
                        {
                            await VmStorageService.RemovePartitionAccessPathAsync(
                                hostDiskNumber, selectedPartition.PartitionNumber, assignedDriveLetter[0]);
                        }
                        await HostDiskService.SetDiskOfflineStatusAsync(hostDiskNumber, true);
                        await Task.Delay(1000);
                        await VmStorageService.AddDriveAsync(
                            vmName, savedCtrlType, savedCtrlNum, savedCtrlLoc,
                            "HardDisk", pathOrNumber: hostDiskNumber.ToString(), isPhysical: true);
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format(Properties.Resources.Error_Gpu_RemountFailed, ex.Message));
                    }
                }
                else if (!isPhysical && !string.IsNullOrEmpty(selectedPartition.DiskPath))
                {
                    Log(Properties.Resources.Msg_Gpu_Unmounting);
                    await VmStorageService.DismountVhdxAsync(selectedPartition.DiskPath);
                }
            }
        }

        private static string DescribeCleanupCandidates(string repositoryPath)
        {
            if (!Directory.Exists(repositoryPath))
                return Properties.Resources.GpuPv_Log_NoRepository;

            try
            {
                int subDirectories = Directory.GetDirectories(repositoryPath).Length;
                int files = Directory.GetFiles(repositoryPath).Length;
                return string.Format(Properties.Resources.GpuPv_Log_RepositoryStats, repositoryPath, subDirectories, files);
            }
            catch (Exception ex)
            {
                return string.Format(Properties.Resources.GpuPv_Log_RepositoryReadFailed, repositoryPath, ex.Message);
            }
        }

        /// <summary>读取注入标记里记录的驱动包目录名；旧版标记没有该字段时返回空列表。</summary>
        private static List<string> ReadMarkerPackages(string markerPath)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(markerPath));
                if (document.RootElement.TryGetProperty("driverPackages", out var element) &&
                    element.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    return element.EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GPU Marker] 读取注入标记失败: {ex.Message}");
            }
            return new List<string>();
        }

        private static void WriteInjectionMarker(
            string assignedDriveLetter,
            string gpuInstancePath,
            string gpuManu,
            GpuDriverPackagePlan packagePlan,
            Action<string> log)
        {
            try
            {
                string markerPath = Path.Combine(assignedDriveLetter, InjectionMarkerRelativePath);
                string payload = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        tool = "hyver-v-mgr",
                        feature = "GPU-PV",
                        version = 1,
                        createdUtc = DateTime.UtcNow.ToString("o"),
                        gpuInstancePath,
                        gpuManu,
                        vendor = packagePlan.Vendor.ToString(),
                        fileRepository = @"Windows\System32\HostDriverStore\FileRepository",
                        driverPackages = packagePlan.PackageNames,
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(markerPath, payload, new System.Text.UTF8Encoding(false));
                log(Properties.Resources.GpuPv_Log_MarkerWritten);
            }
            catch (Exception ex)
            {
                log(string.Format(Properties.Resources.GpuPv_Log_MarkerWriteFailed, ex.Message));
            }
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
            var linkWarnings = new ConcurrentQueue<string>();
            _linkWarnings.Value = linkWarnings;


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
                    return string.Format(
                        Properties.Resources.Error_Gpu_InjectFailed,
                        Properties.Resources.Error_Gpu_AssignDriveLetterFailed);
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

                GpuDriverPackagePlan packagePlan = await Task.Run(() =>
                    GpuDriverPackageResolver.Resolve(gpuInstancePath, gpuManu));
                _activeDriverPackagePlan.Value = packagePlan;
                foreach (string warning in packagePlan.Warnings) Log(warning);
                string destFolder = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");

                Directory.CreateDirectory(destFolder);

                Log(Properties.Resources.Msg_Gpu_SyncingFiles);
                await CopyDriverPackagesAsync(
                    packagePlan.HostFileRepository,
                    destFolder,
                    packagePlan.PackageNames,
                    Log);

                // 同步重活（注册表文件提取 + 各厂商 Promote*：内部对每个文件 spawn cmd.exe 并同步 WaitForExit 几十~上百次）
                // 挪到后台线程——否则作为 robocopy await 之后的续体跑在 UI 线程上，会冻结主界面（转圈/窗口都卡死）。
                // Log 回调仍在 UI 续体里执行（这些 Task.Run 之间的代码在 UI 线程），更新 task.Description 安全、无需 Dispatcher。
                await Task.Run(() => PromoteRegistryDefinedFiles(assignedDriveLetter, packagePlan)); // 只读宿主注册表，用于挑选需要带到来宾的文件

                try
                {
                    if (packagePlan.Vendor == GpuDriverVendor.Nvidia)
                    {
                        // GPU-PV 的来宾驱动完全由文件组成：写入 HostDriverStore 并链接到 System32 即可，
                        // 不做任何离线注册表写入（参考实现同样不写注册表）。
                        await Task.Run(() => PromoteNvidiaFiles(assignedDriveLetter));
                    }
                    else if (packagePlan.Vendor == GpuDriverVendor.Intel)
                    {
                        await Task.Run(() => PromoteIntelGpuFiles(assignedDriveLetter));
                    }
                    else if (packagePlan.Vendor == GpuDriverVendor.Amd)
                    {
                        await Task.Run(() => PromoteAmdGpuFiles(assignedDriveLetter, packagePlan));
                    }
                    else if (packagePlan.Vendor == GpuDriverVendor.Qualcomm)
                    {
                        await Task.Run(() => PromoteQualcommGpuFiles(assignedDriveLetter));
                    }
                }
                catch (Exception ex)
                {
                    Log($"[GPU Optional] Vendor-specific promotion was incomplete: {ex.Message}");
                }

                try
                {
                    await CopyVendorProgramFoldersAsync(packagePlan.Vendor, assignedDriveLetter, Log);
                }
                catch (Exception ex)
                {
                    Log($"[GPU Optional] Vendor software directories were not copied completely: {ex.Message}");
                }

                // 写入清理标记：后续「清理驱动」只认这个文件，避免误删来宾原有驱动
                WriteInjectionMarker(assignedDriveLetter, gpuInstancePath, gpuManu, packagePlan, Log);

                return "OK";
            }
            catch (Exception ex) { return string.Format(Properties.Resources.Error_Gpu_InjectFailed, ex.Message); }
            finally
            {
                while (linkWarnings.TryDequeue(out string warning))
                {
                    try { Log(warning); }
                    catch { }
                }
                _linkWarnings.Value = null;
                _activeDriverPackagePlan.Value = null;

                if (isPhysical && hostDiskNumber != -1 && detachSuccess)
                {
                    Log(Properties.Resources.Msg_Gpu_Remounting);
                    try
                    {
                        if (!string.IsNullOrEmpty(assignedDriveLetter))
                        {
                            bool accessPathRemoved = await VmStorageService.RemovePartitionAccessPathAsync(
                                hostDiskNumber,
                                partition.PartitionNumber,
                                assignedDriveLetter[0]);
                            if (!accessPathRemoved)
                                Log($"[GPU Cleanup] Temporary access path {assignedDriveLetter} could not be removed explicitly.");
                        }
                        await HostDiskService.SetDiskOfflineStatusAsync(hostDiskNumber, true);
                        await Task.Delay(1000);

                        var remountResult = await VmStorageService.AddDriveAsync(
                            vmName, savedCtrlType, savedCtrlNum, savedCtrlLoc,
                            "HardDisk", pathOrNumber: hostDiskNumber.ToString(),
                            isPhysical: true);
                        if (!remountResult.Success)
                            throw new IOException($"Physical disk {hostDiskNumber} could not be reattached: {remountResult.Message}");
                        Log(Properties.Resources.Msg_Gpu_RemountSuccess);

                    }
                    catch (Exception ex)
                    {
                        Log(string.Format(Properties.Resources.Error_Gpu_RemountFailed, ex.Message));
                        throw;
                    }
                }
                else if (!string.IsNullOrEmpty(diskTarget?.Path))
                {
                    Log(Properties.Resources.Msg_Gpu_Unmounting);
                    await VmStorageService.DismountVhdxAsync(diskTarget.Path);
                }
            }
        }

        /// <summary>
        /// 只把当前显卡实际引用的驱动包目录复制到来宾 HostDriverStore\FileRepository：
        /// 包目录名（INF 内容哈希）保持不变，目录层级与文件属性原样保留。
        /// 与参考实现一致，不再镜像宿主整个 FileRepository，避免把其它显卡/旧版本的包也带进来。
        /// </summary>
        private static async Task CopyDriverPackagesAsync(
            string hostFileRepository,
            string guestFileRepository,
            IReadOnlyList<string> packageNames,
            Action<string> log)
        {
            if (!Directory.Exists(hostFileRepository))
                throw new DirectoryNotFoundException($"Host DriverStore FileRepository was not found: {hostFileRepository}");

            Directory.CreateDirectory(guestFileRepository);

            foreach (string packageName in packageNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string source = Path.Combine(hostFileRepository, packageName);
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException($"Driver package was not found: {source}");

                string target = Path.Combine(guestFileRepository, packageName);
                if (Directory.Exists(target)) RemoveReadOnlyAttribute(target);

                log($"[GPU DriverStore] {source} -> {target}");
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "robocopy.exe",
                    Arguments = $"\"{source}\" \"{target}\" /E /R:1 /W:1 /MT:32 /XJ /NDL /NJH /NJS /NC /NS",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }) ?? throw new InvalidOperationException("Unable to start robocopy.exe.");
                await process.WaitForExitAsync();
                // Robocopy 0..7 are success states; 8 and above mean at least one copy failure.
                if (process.ExitCode >= 8)
                    throw new IOException(
                        $"Copying driver package {packageName} failed with robocopy exit code {process.ExitCode}.");

                await Task.Run(() => SetFolderReadOnly(target));
            }
        }

        private void PromoteRegistryDefinedFiles(
            string assignedDriveLetter,
            GpuDriverPackagePlan packagePlan)
        {
            string classGuidPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var classKey = baseKey.OpenSubKey(classGuidPath);
                if (classKey == null) return;

                void ProcessAdapterKey(Microsoft.Win32.RegistryKey adapterKey)
                {
                    ProcessPromotionRegistryKey(
                        adapterKey,
                        "CopyToVmWhenNewer",
                        assignedDriveLetter,
                        "System32",
                        packagePlan.PrimaryPackageNames);
                    ProcessPromotionRegistryKey(
                        adapterKey,
                        "CopyToVmOverwrite",
                        assignedDriveLetter,
                        "System32",
                        packagePlan.PrimaryPackageNames);

                    if (Directory.Exists(Path.Combine(assignedDriveLetter, "Windows", "SysWOW64")))
                    {
                        ProcessPromotionRegistryKey(
                            adapterKey,
                            "CopyToVmWhenNewerWow64",
                            assignedDriveLetter,
                            "SysWOW64",
                            packagePlan.PrimaryPackageNames);
                        ProcessPromotionRegistryKey(
                            adapterKey,
                            "CopyToVmOverwriteWow64",
                            assignedDriveLetter,
                            "SysWOW64",
                            packagePlan.PrimaryPackageNames);
                    }
                }

                string selectedSubKeyName = packagePlan.DisplayClassSubKeyName;
                if (!string.IsNullOrEmpty(selectedSubKeyName))
                {
                    using var selectedKey = classKey.OpenSubKey(selectedSubKeyName);
                    if (selectedKey != null)
                    {
                        ProcessAdapterKey(selectedKey);
                        return;
                    }
                }

                throw new InvalidOperationException("The selected display-class registry key could not be opened.");
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

        private void PromoteAmdGpuFiles(
            string assignedDriveLetter,
            GpuDriverPackagePlan packagePlan)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            IReadOnlyCollection<string> selectedPackageNames = packagePlan.PrimaryPackageNames;
            IReadOnlyCollection<string> openClPackageNames = packagePlan.AmdOpenClPackageNames;
            IReadOnlyCollection<string> amdWinPackageNames = packagePlan.AmdWinPackageNames;

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

            // DDA before/after parity: display-package top-level runtime entries.
            LinkSingleFile(assignedDriveLetter, "amdmiracast.dll", "amdmiracast.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "amf-mft-mjpeg-decoder64.dll", "amf-mft-mjpeg-decoder64.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atidemgy.dll", "atidemgy.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atimuixx.dll", "atimuixx.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);

            // The OpenCL/HIP companion package is declared by the active display INF and has the same DriverVer.
            // Never fall back to a different amdocl generation when multiple driver versions coexist.
            LinkSingleFile(assignedDriveLetter, "amd_comgr_2.dll", "amd_comgr_2.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "amdhip64_6.dll", "amdhip64_6.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "amdmmcl.dll", "amdmmcl.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "amdmmcl6.dll", "amdmmcl6.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "clinfo.exe", "clinfo.exe", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "hiprt02000_amd.hipfb", "hiprt02000_amd.hipfb", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "hiprt0200064.dll", "hiprt0200064.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "oro_compiled_kernels.hipfb", "oro_compiled_kernels.hipfb", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);

            // AMD Windows support components. RapidFire requires the Microsoft VC++ runtime at load time;
            // that external prerequisite is intentionally not installed by GPU driver injection.
            LinkSingleFile(assignedDriveLetter, "amdlogum.exe", "amdlogum.exe", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "dgtrayicon.exe", "dgtrayicon.exe", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "Rapidfire64.dll", "Rapidfire64.dll", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "RapidFireServer64.dll", "RapidFireServer64.dll", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);

            // 资源与数据
            LinkSingleFile(assignedDriveLetter, "atiapfxx.blb", "atiapfxx.blb", s32);
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", s32);
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", s32);
            LinkSingleFile(assignedDriveLetter, "AMDKernelEvents.mc", "AMDKernelEvents.man", s32); 
            LinkSingleFile(assignedDriveLetter, "detoured64.dll", "detoured.dll", s32);

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
            LinkSingleFile(assignedDriveLetter, "amdsacli32.dll", "amdsacli32.dll", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "amf-mft-mjpeg-decoder32.dll", "amf-mft-mjpeg-decoder32.dll", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atiadlxy.dll", "atiadlxy.dll", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atieah32.exe", "atieah32.exe", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "amd_comgr32.dll", "amd_comgr32.dll", sw64, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "Rapidfire.dll", "Rapidfire.dll", sw64, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "RapidFireServer.dll", "RapidFireServer.dll", sw64, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);

            // 资源
            LinkSingleFile(assignedDriveLetter, "atiapfxx.blb", "atiapfxx.blb", sw64);
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", sw64);
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", sw64);

            // --- 3. Vulkan Loader 与工具 ---
            // amdvlk*.dll 是 AMD ICD，不是系统 Vulkan Loader；这里严格按 AMD INF 的映射提升。
            LinkSingleFile(assignedDriveLetter, "vulkan64.dll", "vulkan-1.dll", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkan64.dll", "vulkan-1-999-0-0-0.dll", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo64.exe", "vulkaninfo.exe", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo64.exe", "vulkaninfo-1-999-0-0-0.exe", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkan32.dll", "vulkan-1.dll", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkan32.dll", "vulkan-1-999-0-0-0.dll", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo32.exe", "vulkaninfo.exe", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo32.exe", "vulkaninfo-1-999-0-0-0.exe", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
        }

        private void PromoteQualcommGpuFiles(string assignedDriveLetter)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            string sc32 = "SyChpe32";

            // --- 1. System32 (原生 ARM64 组件) ---
            LinkSingleFile(assignedDriveLetter, "OpenCL.dll", "OpenCL.dll", s32);
            LinkSingleFile(assignedDriveLetter, "qcdxkmsuc8380.mbn", "qcdxkmsuc8380.mbn", s32);
            LinkSingleFile(assignedDriveLetter, "qcdxkmsucpurwa.mbn", "qcdxkmsucpurwa.mbn", s32);
            LinkSingleFile(assignedDriveLetter, "qchdcpumd8380.dll", "qchdcpumd8380.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1.dll", "vulkan-1.dll", s32, FilePromotionMode.WhenNewer);
            LinkSingleFile(assignedDriveLetter, "vulkan-1.dll", "vulkan-1-999-0-0-0.dll", s32, FilePromotionMode.WhenNewer);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo.exe", "vulkaninfo.exe", s32, FilePromotionMode.WhenNewer);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo.exe", "vulkaninfo-1-999-0-0-0.exe", s32, FilePromotionMode.WhenNewer);

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
            LinkSingleFile(assignedDriveLetter, "qcegpchpe.dll", "qcegpx86.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcgpuchpecompilercore.dll", "qcgpux86compilercore.DLL", sc32);

            // MFT promotion remains gated on guest video-context support.
        }
        private void ProcessPromotionRegistryKey(
            Microsoft.Win32.RegistryKey adapterKey,
            string subKeyName,
            string assignedDriveLetter,
            string targetSubDir,
            IReadOnlyCollection<string> preferredPackageNames)
        {
            using var promotionKey = adapterKey.OpenSubKey(subKeyName);
            if (promotionKey == null) return;

            FilePromotionMode promotionMode = subKeyName.Contains("Overwrite", StringComparison.OrdinalIgnoreCase)
                ? FilePromotionMode.Overwrite
                : FilePromotionMode.WhenNewer;

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

                if (!string.IsNullOrEmpty(sourceSearch) && !string.IsNullOrEmpty(targetLinkName))
                {
                    LinkSingleFile(
                        assignedDriveLetter,
                        sourceSearch,
                        targetLinkName,
                        targetSubDir,
                        promotionMode,
                        preferredPackageNames);
                }
            }
        }

        private void LinkSingleFile(
            string assignedDriveLetter,
            string sourceName,
            string targetName,
            string targetSubDir,
            FilePromotionMode promotionMode = FilePromotionMode.PreserveExisting,
            IReadOnlyCollection<string>? preferredPackageNames = null,
            bool requirePreferredPackage = false)
        {
            try
            {
                string guestRepo = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");
                string hostDestDir = Path.Combine(assignedDriveLetter, "Windows", targetSubDir);
                string hostLinkPath = ResolveGuestPromotionTarget(hostDestDir, targetName);

                // 完整 FileRepository 虽然已复制，但厂商固定映射只能在当前显卡的精确包集合中查找；
                // CopyToVm 调用会显式传入主包，AMD 的特定组件也会显式传入对应伴随包。
                // 这同时阻止旧版或其他显卡遗留在来宾 DriverStore 中的同名文件被误用，
                // Qualcomm 的 ARM64/x86/CHPE 子目录则随主包完整保留。
                GpuDriverPackagePlan? activePlan = _activeDriverPackagePlan.Value;
                IReadOnlyCollection<string>? effectivePackageNames =
                    preferredPackageNames ?? activePlan?.PackageNames;
                bool allowRepositoryFallback = activePlan == null && !requirePreferredPackage;

                var foundFiles = FindDriverSourceFiles(
                    guestRepo,
                    sourceName,
                    effectivePackageNames,
                    allowRepositoryFallback);
                if (foundFiles.Count == 0)
                {
                    if (promotionMode != FilePromotionMode.PreserveExisting || requirePreferredPackage)
                    {
                        string warning = $"[GPU Link] Required promotion source not found: {sourceName}.";
                        Debug.WriteLine(warning);
                        _linkWarnings.Value?.Enqueue(warning);
                    }
                    return;
                }
                if (foundFiles.Count > 1)
                {
                    string warning = $"[GPU Link] Promotion source is ambiguous: {sourceName}.";
                    Debug.WriteLine(warning);
                    _linkWarnings.Value?.Enqueue(warning);
                    return;
                }

                FileInfo sourceFile = foundFiles[0];
                string hostSourceFile = sourceFile.FullName;
                string relativeSourcePath = Path.GetRelativePath(
                    Path.GetFullPath(assignedDriveLetter),
                    Path.GetFullPath(hostSourceFile));

                if (Path.IsPathRooted(relativeSourcePath) ||
                    relativeSourcePath.Equals("..", StringComparison.Ordinal) ||
                    relativeSourcePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    throw new IOException($"Driver source is outside the mounted guest volume: {hostSourceFile}");
                }

                string guestInternalTarget = Path.Combine(@"C:\", relativeSourcePath);

                string hostLinkDirectory = Path.GetDirectoryName(hostLinkPath)
                    ?? throw new IOException($"Invalid driver promotion target: {targetName}");
                Directory.CreateDirectory(hostLinkDirectory);
                if (targetSubDir.Equals("System32", StringComparison.OrdinalIgnoreCase) ||
                    targetSubDir.Contains("SyChpe32", StringComparison.OrdinalIgnoreCase) ||
                    targetSubDir.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteCommand($"cmd /c takeown /f \"{hostDestDir}\" /a");
                    ExecuteCommand($"cmd /c icacls \"{hostDestDir}\" /grant *S-1-5-32-544:F");
                }

                bool isReparsePoint = false;
                bool targetPathExists = File.Exists(hostLinkPath) || Directory.Exists(hostLinkPath);
                bool targetIsDirectory = Directory.Exists(hostLinkPath);
                string? backupPath = null;
                string? previousLinkTarget = null;
                try
                {
                    isReparsePoint = File.GetAttributes(hostLinkPath).HasFlag(FileAttributes.ReparsePoint);
                    targetPathExists = true;
                }
                catch { }

                if (targetPathExists)
                {
                    if (targetIsDirectory && !isReparsePoint)
                    {
                        string warning = $"[GPU Link] Refusing to replace directory {hostLinkPath}.";
                        Debug.WriteLine(warning);
                        _linkWarnings.Value?.Enqueue(warning);
                        return;
                    }

                    bool shouldReplace = promotionMode switch
                    {
                        FilePromotionMode.Overwrite => true,
                        FilePromotionMode.WhenNewer => IsSourceNewerThanDestination(
                            sourceFile,
                            hostLinkPath,
                            isReparsePoint,
                            assignedDriveLetter),
                        // 保留原逻辑：普通来宾文件不动，但刷新旧版创建的错误链接。
                        _ => isReparsePoint
                    };

                    if (!shouldReplace) return;

                    if (isReparsePoint)
                    {
                        try { previousLinkTarget = new FileInfo(hostLinkPath).LinkTarget; }
                        catch { }

                        int deleteExitCode = ExecuteCommand($"cmd /c del /f /q \"{hostLinkPath}\"");
                        if (deleteExitCode != 0)
                        {
                            string warning = $"[GPU Link] Unable to replace {hostLinkPath}: del exited with code {deleteExitCode}.";
                            Debug.WriteLine(warning);
                            _linkWarnings.Value?.Enqueue(warning);
                            return;
                        }
                    }
                    else
                    {
                        backupPath = $"{hostLinkPath}.exhyperv-backup-{Guid.NewGuid():N}";
                        try
                        {
                            File.Move(hostLinkPath, backupPath);
                        }
                        catch (Exception ex)
                        {
                            string warning = $"[GPU Link] Unable to back up {hostLinkPath}: {ex.Message}";
                            Debug.WriteLine(warning);
                            _linkWarnings.Value?.Enqueue(warning);
                            return;
                        }
                    }
                }

                int exitCode = ExecuteCommand($"cmd /c mklink \"{hostLinkPath}\" \"{guestInternalTarget}\"");
                if (exitCode != 0)
                {
                    try
                    {
                        File.GetAttributes(hostLinkPath);
                        ExecuteCommand($"cmd /c del /f /q \"{hostLinkPath}\"");
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        try
                        {
                            File.Move(backupPath, hostLinkPath);
                        }
                        catch (Exception ex)
                        {
                            string rollbackWarning = $"[GPU Link] Rollback failed for {hostLinkPath}: {ex.Message}";
                            Debug.WriteLine(rollbackWarning);
                            _linkWarnings.Value?.Enqueue(rollbackWarning);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(previousLinkTarget))
                    {
                        int restoreExitCode = ExecuteCommand($"cmd /c mklink \"{hostLinkPath}\" \"{previousLinkTarget}\"");
                        if (restoreExitCode != 0)
                        {
                            string rollbackWarning = $"[GPU Link] Unable to restore previous link {hostLinkPath}: mklink exited with code {restoreExitCode}.";
                            Debug.WriteLine(rollbackWarning);
                            _linkWarnings.Value?.Enqueue(rollbackWarning);
                        }
                    }

                    string warning = $"[GPU Link] {targetName} -> {guestInternalTarget}: mklink exited with code {exitCode}.";
                    Debug.WriteLine(warning);
                    _linkWarnings.Value?.Enqueue(warning);
                    return;
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    try
                    {
                        File.SetAttributes(backupPath, FileAttributes.Normal);
                        File.Delete(backupPath);
                    }
                    catch (Exception ex)
                    {
                        string warning = $"[GPU Link] Link created, but backup cleanup failed for {backupPath}: {ex.Message}";
                        Debug.WriteLine(warning);
                        _linkWarnings.Value?.Enqueue(warning);
                    }
                }
            }
            catch (Exception ex)
            {
                string warning = $"[GPU Link] {sourceName}: {ex.Message}";
                Debug.WriteLine(warning);
                _linkWarnings.Value?.Enqueue(warning);
            }
        }

        private static string ResolveGuestPromotionTarget(string destinationRoot, string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                throw new IOException("Driver promotion target is empty.");

            string normalizedTarget = targetName.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedTarget))
                throw new IOException($"Driver promotion target must be relative: {targetName}");

            string[] targetParts = normalizedTarget.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (targetParts.Length == 0 ||
                targetParts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new IOException($"Invalid driver promotion target: {targetName}");
            }

            string fullRoot = Path.GetFullPath(destinationRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullTarget = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(targetParts)));
            if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Driver promotion target escapes the guest directory: {targetName}");

            return fullTarget;
        }

        private static List<FileInfo> FindDriverSourceFiles(
            string guestRepo,
            string sourceName,
            IReadOnlyCollection<string>? preferredPackageNames,
            bool allowRepositoryFallback = true)
        {
            if (!Directory.Exists(guestRepo) || string.IsNullOrWhiteSpace(sourceName)) return new List<FileInfo>();

            string normalizedSource = sourceName.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedSource))
                throw new IOException($"Driver source must be relative to FileRepository: {sourceName}");

            string[] sourceParts = normalizedSource.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (sourceParts.Length == 0 || sourceParts.Any(part => part == "." || part == ".."))
                throw new IOException($"Invalid DriverStore source path: {sourceName}");

            var matches = new List<FileInfo>();

            void SearchPackageDirectories(IEnumerable<string> packageDirectories)
            {
                string relativeDirectory = Path.GetDirectoryName(normalizedSource) ?? string.Empty;
                string filePattern = Path.GetFileName(normalizedSource);

                foreach (string packageDirectory in packageDirectories)
                {
                    try
                    {
                        string searchRoot = Path.Combine(packageDirectory, relativeDirectory);
                        if (!Directory.Exists(searchRoot)) continue;
                        SearchOption searchOption = sourceParts.Length == 1
                            ? SearchOption.AllDirectories
                            : SearchOption.TopDirectoryOnly;
                        matches.AddRange(new DirectoryInfo(searchRoot).GetFiles(filePattern, searchOption));
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }

            if (preferredPackageNames != null && preferredPackageNames.Count > 0)
            {
                var preferredDirectories = preferredPackageNames
                    .Where(name => !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name)
                    .Select(name => Path.Combine(guestRepo, name));
                SearchPackageDirectories(preferredDirectories);
            }

            if (matches.Count == 0 && allowRepositoryFallback)
            {
                if (sourceParts.Length > 1)
                {
                    SearchPackageDirectories(Directory.EnumerateDirectories(guestRepo, "*", SearchOption.TopDirectoryOnly));
                }
                else
                {
                    matches.AddRange(new DirectoryInfo(guestRepo).GetFiles(normalizedSource, SearchOption.AllDirectories));
                }
            }

            return matches
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsSourceNewerThanDestination(
            FileInfo sourceFile,
            string destinationPath,
            bool destinationIsReparsePoint,
            string assignedDriveLetter)
        {
            try
            {
                string timestampPath = destinationPath;
                if (destinationIsReparsePoint)
                {
                    string? linkTarget = new FileInfo(destinationPath).LinkTarget;
                    if (string.IsNullOrWhiteSpace(linkTarget)) return true;

                    if (Path.IsPathFullyQualified(linkTarget))
                    {
                        string? targetRoot = Path.GetPathRoot(linkTarget);
                        if (string.IsNullOrEmpty(targetRoot)) return true;
                        string relativeTarget = Path.GetRelativePath(targetRoot, linkTarget);
                        timestampPath = Path.Combine(assignedDriveLetter, relativeTarget);
                    }
                    else
                    {
                        timestampPath = Path.GetFullPath(
                            Path.Combine(Path.GetDirectoryName(destinationPath) ?? assignedDriveLetter, linkTarget));
                    }

                    // 链接仍指向旧包或旧版错误源时必须刷新，不能只比较时间戳。
                    if (!Path.GetFullPath(timestampPath).Equals(
                            Path.GetFullPath(sourceFile.FullName),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (!File.Exists(timestampPath)) return true;

                string extension = Path.GetExtension(destinationPath);
                if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    FileVersionInfo sourceVersion = FileVersionInfo.GetVersionInfo(sourceFile.FullName);
                    FileVersionInfo destinationVersion = FileVersionInfo.GetVersionInfo(timestampPath);
                    int versionComparison = CompareFileVersions(sourceVersion, destinationVersion);
                    if (versionComparison != 0) return versionComparison > 0;
                }

                return sourceFile.LastWriteTimeUtc > File.GetLastWriteTimeUtc(timestampPath);
            }
            catch
            {
                // 无法确认旧目标版本时刷新链接，避免保留悬空或旧格式链接。
                return true;
            }
        }

        private static int CompareFileVersions(FileVersionInfo left, FileVersionInfo right)
        {
            int comparison = left.FileMajorPart.CompareTo(right.FileMajorPart);
            if (comparison != 0) return comparison;
            comparison = left.FileMinorPart.CompareTo(right.FileMinorPart);
            if (comparison != 0) return comparison;
            comparison = left.FileBuildPart.CompareTo(right.FileBuildPart);
            return comparison != 0
                ? comparison
                : left.FilePrivatePart.CompareTo(right.FilePrivatePart);
        }

        private static void SetFolderReadOnly(string folderPath)
        {
            var directory = new DirectoryInfo(folderPath);
            directory.Attributes |= FileAttributes.ReadOnly;

            foreach (var subDirectory in directory.GetDirectories())
            {
                SetFolderReadOnly(subDirectory.FullName);
            }

            foreach (var file in directory.GetFiles())
            {
                file.Attributes |= FileAttributes.ReadOnly;
            }
        }

        private static void RemoveReadOnlyAttribute(string folderPath)
        {
            var directory = new DirectoryInfo(folderPath);
            directory.Attributes &= ~FileAttributes.ReadOnly;

            foreach (var subDirectory in directory.GetDirectories())
            {
                RemoveReadOnlyAttribute(subDirectory.FullName);
            }

            foreach (var file in directory.GetFiles())
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        private static async Task CopyVendorProgramFoldersAsync(
            GpuDriverVendor vendor,
            string assignedDriveLetter,
            Action<string> log)
        {
            string[] directoryNames = vendor switch
            {
                GpuDriverVendor.Nvidia => new[] { "NVIDIA Corporation", "NVIDIA" },
                GpuDriverVendor.Amd => new[] { "AMD", "ATI Technologies" },
                GpuDriverVendor.Intel => new[] { "Intel", "Intel Corporation" },
                GpuDriverVendor.Qualcomm => new[] { "Qualcomm", "Qualcomm Incorporated" },
                _ => Array.Empty<string>()
            };

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };

            string[] sourceFolders = roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .SelectMany(root => directoryNames.Select(name => Path.Combine(root, name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var sourcePath in sourceFolders)
            {
                if (!Directory.Exists(sourcePath)) continue;
                string root = Path.GetPathRoot(sourcePath)
                    ?? throw new IOException($"Unable to determine the root of vendor directory: {sourcePath}");
                string relativePath = Path.GetRelativePath(root, sourcePath);

                string targetPath = Path.Combine(assignedDriveLetter, relativePath);

                log?.Invoke(string.Format(Properties.Resources.Log_Gpu_Syncing, sourcePath, targetPath));

                Directory.CreateDirectory(targetPath);
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "robocopy.exe",
                    Arguments = $"\"{sourcePath}\" \"{targetPath}\" /E /R:1 /W:1 /MT:32 /XJ /NDL /NJH /NJS /NC /NS",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }) ?? throw new InvalidOperationException("Unable to start robocopy.exe.");
                await process.WaitForExitAsync();
                if (process.ExitCode >= 8)
                    throw new IOException(
                        $"Copying vendor program directory {sourcePath} failed with robocopy exit code {process.ExitCode}.");
            }
        }

        #endregion

        #region Linux 驱动环境与脚本部署
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

            // 从应用维护的在线索引加载商店版本允许使用的部署脚本。
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
                        item.Name = string.Format(Properties.Resources.VmGPUService_LogOnline, item.Name);
                        item.SourceUrl = $"{ScriptBaseUrl}{item.FileName}";
                        allScripts.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Remote script index fetch failed: {ex.Message}");
            }

            return allScripts
                .OrderBy(x => x.Name)
                .ToList();
        }

        // 支持重启循环的部署函数
        public Task<string> ProvisionLinuxGpuAsync(
            string vmName,
            string gpuInstancePath,
            string gpuManufacturer,
            LinuxScriptItem script,
            SshCredentials credentials,
            Action<string> progressCallback,
            CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                void Log(string msg) => progressCallback?.Invoke(msg);

                try
                {
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

                    string remoteTempDir = "/tmp/exhyperv_deploy";
                    using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                    {
                        client.Connect();
                        using var command = client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                        client.Disconnect();
                    }

                    Log(Properties.Resources.Log_Gpu_UploadingDriverWsl);
                    GpuDriverPackagePlan packagePlan = await Task.Run(() =>
                        GpuDriverPackageResolver.Resolve(gpuInstancePath, gpuManufacturer));
                    string sourceDriverPath = packagePlan.HostFileRepository;
                    await SshService.UploadDirectoryAsync(credentials, sourceDriverPath, $"{remoteTempDir}/drivers");
                    await UploadLocalFilesAsync(credentials, $"{remoteTempDir}/lib");

                    string remoteScriptPath = $"{remoteTempDir}/{script.FileName}";

                    string proxyEnv = string.Empty;
                    if (credentials.UseProxy && !string.IsNullOrEmpty(credentials.ProxyHost))
                    {
                        string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";
                        proxyEnv = $"http_proxy='{proxyUrl}' https_proxy='{proxyUrl}' HTTP_PROXY='{proxyUrl}' HTTPS_PROXY='{proxyUrl}' ";
                    }

                    Log(string.Format(Properties.Resources.Log_Gpu_DownloadingScript, script.Name));
                    string downloadCmd = $"{proxyEnv}sh -c \"wget -q -O {remoteScriptPath} {script.SourceUrl} || curl -fL {script.SourceUrl} -o {remoteScriptPath}\"";

                    await SshService.ExecuteSingleCommandAsync(credentials, downloadCmd, Log);
                    await SshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteScriptPath}", Log);
                    bool isSuccess = false;
                    int maxAttempts = 3;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (ct.IsCancellationRequested) return Properties.Resources.Msg_Gpu_Cancelled;

                        bool rebootNeeded = false;
                        string graphicsArg = credentials.InstallGraphics ? "true" : "false";
                        // 代理地址单引号包裹 + 转义,防 ProxyHost 含 $()/反引号注入(与上面密码同款转义);正常主机名行为等价
                        string proxyArg = credentials.UseProxy ? $"'http://{credentials.ProxyHost.Replace("'", "'\\''")}:{credentials.ProxyPort}'" : "\"\"";

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
