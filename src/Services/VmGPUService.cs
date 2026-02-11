using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Tools; 
using Renci.SshNet;

namespace ExHyperV.Services
{
    public class VmGPUService
    {
        private class VmDiskTarget
        {
            public bool IsPhysical { get; set; }
            public string Path { get; set; }        // 如果是虚拟盘，存 VHDX 路径
            public int PhysicalDiskNumber { get; set; } // 如果是物理盘，存 Disk Number (e.g. 0, 1, 2)
        }

        public Task<List<(string Id, string InstancePath)>> GetVmGpuAdaptersAsync(string vmName)
        {
            return Task.Run(() =>
            {
                var result = new List<(string Id, string InstancePath)>();
                string scopePath = @"\\.\root\virtualization\v2";

                try
                {
                    // 1. 找到对应的虚拟机 (Msvm_ComputerSystem)
                    string query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                    using var searcher = new ManagementObjectSearcher(scopePath, query);
                    using var vmCollection = searcher.Get();

                    var computerSystem = vmCollection.Cast<ManagementObject>().FirstOrDefault();
                    if (computerSystem == null) return result;

                    // 2. 获取 VM 的系统设置 (Msvm_VirtualSystemSettingData)
                    using var relatedSettings = computerSystem.GetRelated(
                        "Msvm_VirtualSystemSettingData",
                        "Msvm_SettingsDefineState",
                        null, null, null, null, false, null);

                    var virtualSystemSetting = relatedSettings.Cast<ManagementObject>().FirstOrDefault();
                    if (virtualSystemSetting == null) return result;

                    // 3. 获取 GPU 分区配置组件 (Msvm_GpuPartitionSettingData)
                    using var gpuSettingsCollection = virtualSystemSetting.GetRelated(
                        "Msvm_GpuPartitionSettingData",
                        "Msvm_VirtualSystemSettingDataComponent",
                        null, null, null, null, false, null);

                    foreach (var gpuSetting in gpuSettingsCollection.Cast<ManagementObject>())
                    {
                        // 获取 Adapter ID (对应 PowerShell 的 Id)
                        string adapterId = gpuSetting["InstanceID"]?.ToString();
                        string instancePath = string.Empty;

                        // 获取 HostResource (这是一个字符串数组，包含指向 Msvm_PartitionableGpu 的 WMI 路径)
                        string[] hostResources = (string[])gpuSetting["HostResource"];

                        if (hostResources != null && hostResources.Length > 0)
                        {
                            // 4. 解析 HostResource，获取物理 GPU 对象
                            try
                            {
                                // 直接使用路径实例化 ManagementObject
                                using var partitionableGpu = new ManagementObject(hostResources[0]);
                                partitionableGpu.Get(); // 强制加载属性

                                // 关键修正：读取 Msvm_PartitionableGpu 的 "Name" 属性
                                // 你的截图显示 Name 属性包含 \\?\PCI#VEN... 
                                instancePath = partitionableGpu["Name"]?.ToString();
                            }
                            catch (Exception)
                            {
                                // 如果无法解析物理路径（例如驱动已卸载），保留为空或填入原始路径
                                instancePath = "Unknown/Unresolved Device";
                            }
                        }

                        if (!string.IsNullOrEmpty(adapterId))
                        {
                            result.Add((adapterId, instancePath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 记录日志或处理错误
                    System.Diagnostics.Debug.WriteLine($"WMI Query Error: {ex.Message}");
                }

                return result;
            });
        }






        private const string ScriptBaseUrl = "https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/";
        private bool IsWindows11OrGreater() => Environment.OSVersion.Version.Build >= 22000;

        // PowerShell 脚本常量 (保持原样)
        private const string GetGpuWmiInfoScript = "Get-CimInstance -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion";
        private const string GetGpuRamScript = @"
            Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0*"" -ErrorAction SilentlyContinue |
                Select-Object MatchingDeviceId,
                      @{Name='MemorySize'; Expression={
                          if ($_. ""HardwareInformation.qwMemorySize"") {
                              $_.""HardwareInformation.qwMemorySize""
                          } 
                          elseif ($_. ""HardwareInformation.MemorySize"" -and $_.""HardwareInformation.MemorySize"" -isnot [byte[]]) {
                              $_.""HardwareInformation.MemorySize""
                          }
                          else {
                              $null
                          }
                      }} |
                Where-Object { $_.MemorySize -ne $null -and $_.MemorySize -gt 0 }";

        private const string GetPartitionableGpusWin11Script = "Get-VMHostPartitionableGpu | select name";
        private const string GetPartitionableGpusWin10Script = "Get-VMPartitionableGpu | select name";

        private const string CheckHyperVModuleScript = "Get-Module -ListAvailable -Name Hyper-V";
        private const string GetVmsScript = "Hyper-V\\Get-VM | Select Id, vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace,Notes";
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

        public Task<string> GetVmStateAsync(string vmName)
        {
            return Task.Run(() =>
            {
                var result = Utils.Run($"(Get-VM -Name '{vmName}').State");
                if (result != null && result.Count > 0)
                {
                    return result[0].ToString();
                }
                return "NotFound";
            });
        }

        public Task ShutdownVmAsync(string vmName)
        {
            return Task.Run(() =>
            {
                Utils.Run($"Stop-VM -Name '{vmName}' -TurnOff");

                while (true)
                {
                    var result = Utils.Run($"(Get-VM -Name '{vmName}').State");
                    if (result != null && result.Count > 0 && result[0].ToString() == "Off")
                    {
                        break;
                    }
                    System.Threading.Thread.Sleep(500);
                }
            });
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
            throw new IOException(ExHyperV.Properties.Resources.Error_NoAvailableDriveLetters);
        }

        public Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName)
        {
            return Task.Run(() =>
            {
                VmDiskTarget diskTarget = null;
                try
                {
                    // 步骤 1: 准确识别磁盘类型
                    var diskTask = GetVmBootDiskInfoAsync(vmName);
                    diskTask.Wait();
                    diskTarget = diskTask.Result;

                    if (diskTarget == null) throw new FileNotFoundException($"无法找到虚拟机 '{vmName}' 的启动磁盘。");

                    string devicePath;

                    if (diskTarget.IsPhysical)
                    {
                        // [物理盘逻辑] 联机以供读取
                        var setupScript = $@"
                            $ErrorActionPreference = 'Stop'
                            Set-Disk -Number {diskTarget.PhysicalDiskNumber} -IsOffline $false
                            Set-Disk -Number {diskTarget.PhysicalDiskNumber} -IsReadOnly $true
                            
                            # 验证循环：等待磁盘联机
                            for ($i=0; $i -lt 10; $i++) {{
                                if ((Get-Disk -Number {diskTarget.PhysicalDiskNumber}).IsOffline -eq $false) {{ return 'OK' }}
                                Start-Sleep -Milliseconds 500
                            }}
                            throw 'Disk {diskTarget.PhysicalDiskNumber} failed to come online.'
                        ";
                        Utils.Run(setupScript);
                        devicePath = $@"\\.\PhysicalDrive{diskTarget.PhysicalDiskNumber}";
                    }
                    else
                    {
                        // [虚拟盘逻辑] 挂载并处理文件锁
                        var mountScript = $@"
                            $path = '{diskTarget.Path}'
                            Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
                            for ($i=0; $i -lt 5; $i++) {{
                                try {{
                                    $img = Mount-DiskImage -ImagePath $path -NoDriveLetter -PassThru -ErrorAction Stop
                                    if ($img) {{ return ($img | Get-Disk).Number }}
                                }} catch {{ Start-Sleep -Seconds 1 }}
                            }}
                            throw 'Failed to mount VHDX after 5 retries, file may be locked.'
                        ";
                        var mountResult = Utils.Run(mountScript);
                        if (mountResult == null || mountResult.Count == 0 || !int.TryParse(mountResult[0].ToString(), out int num))
                        {
                            throw new InvalidOperationException("无法挂载虚拟磁盘，文件可能仍被占用。");
                        }
                        devicePath = $@"\\.\PhysicalDrive{num}";
                    }

                    // 步骤 2: 解析分区
                    var diskParser = new DiskParserService();
                    return diskParser.GetPartitions(devicePath);
                }
                catch (Exception ex)
                {
                    // 将底层 PowerShell 异常传递给上层
                    Debug.WriteLine($"分区获取失败: {ex.Message}");
                    throw;
                }
                finally
                {
                    // 步骤 3: 严格清理
                    if (diskTarget != null)
                    {
                        if (diskTarget.IsPhysical)
                        {
                            // ------------------------------------------------------------------
                            // [关键修复] 将无效的组合命令拆分为两个独立的有效命令
                            // ------------------------------------------------------------------
                            // 1. 先移除只读属性
                            Utils.Run($"Set-Disk -Number {diskTarget.PhysicalDiskNumber} -IsReadOnly $false -ErrorAction SilentlyContinue");
                            // 2. 再将磁盘设为脱机，供虚拟机使用
                            Utils.Run($"Set-Disk -Number {diskTarget.PhysicalDiskNumber} -IsOffline $true -ErrorAction SilentlyContinue");
                        }
                        else if (!string.IsNullOrEmpty(diskTarget.Path))
                        {
                            Utils.Run($"Dismount-DiskImage -ImagePath '{diskTarget.Path}' -ErrorAction SilentlyContinue");
                        }
                    }
                }
            });
        }
        public string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return string.Empty;
            }
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\"))
            {
                normalizedId = normalizedId.Substring(4);
            }
            int suffixIndex = normalizedId.IndexOf("#{");
            if (suffixIndex != -1)
            {
                normalizedId = normalizedId.Substring(0, suffixIndex);
            }
            normalizedId = normalizedId.Replace('\\', '#');

            return normalizedId;
        }

        public Task<List<GPUInfo>> GetHostGpusAsync()
        {
            return Task.Run(() =>
            {
                var pciInfoProvider = new PciInfoProvider();
                pciInfoProvider.EnsureInitializedAsync().Wait();

                var gpuList = new List<GPUInfo>();
                var gpulinked = Utils.Run(GetGpuWmiInfoScript);
                if (gpulinked.Count > 0)
                {
                    foreach (var gpu in gpulinked)
                    {
                        string name = gpu.Members["name"]?.Value.ToString();
                        string instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                        string manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                        string driverVersion = gpu.Members["DriverVersion"]?.Value.ToString();
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);
                        //if (vendor == "Unknown") { continue; }
                        if (instanceId != null && !instanceId.ToUpper().StartsWith("PCI\\")) { continue; }
                        gpuList.Add(new GPUInfo(name, "True", manu, instanceId, null, null, driverVersion, vendor));
                    }
                }

                bool hasHyperV = Utils.Run(CheckHyperVModuleScript).Count > 0;
                if (!hasHyperV)
                {
                    return gpuList;
                }

                var gpuram = Utils.Run(GetGpuRamScript);
                if (gpuram.Count > 0)
                {
                    foreach (var existingGpu in gpuList)
                    {
                        var matchedGpu = gpuram.FirstOrDefault(g =>
                        {
                            string id = g.Members["MatchingDeviceId"]?.Value?.ToString().ToUpper().Substring(0, 21);
                            return !string.IsNullOrEmpty(id) && existingGpu.InstanceId.Contains(id);
                        });

                        string preram = matchedGpu?.Members["MemorySize"]?.Value?.ToString() ?? "0";
                        existingGpu.Ram = long.TryParse(preram, out long _) ? preram : "0";
                    }
                }

                string GetPartitionableGpusScript = IsWindows11OrGreater() ? GetPartitionableGpusWin11Script : GetPartitionableGpusWin10Script;
                var partitionableGpus = Utils.Run(GetPartitionableGpusScript);
                if (partitionableGpus.Count > 0)
                {
                    foreach (var gpu in partitionableGpus)
                    {
                        string pname = gpu.Members["Name"]?.Value.ToString();
                        string normalizedPNameId = NormalizeDeviceId(pname);

                        if (string.IsNullOrEmpty(normalizedPNameId)) continue;
                        var existingGpu = gpuList.FirstOrDefault(g =>
                            NormalizeDeviceId(g.InstanceId) == normalizedPNameId
                        );
                        if (existingGpu != null)
                        {
                            existingGpu.Pname = pname;
                        }
                    }
                }
                return gpuList;
            });
        }

        public Task<List<VmInstanceInfo>> GetVirtualMachinesAsync()
        {
            return Task.Run(() =>
            {
                var vmList = new List<VmInstanceInfo>();
                var vms = Utils.Run(GetVmsScript);
                if (vms.Count > 0)
                {
                    foreach (var vm in vms)
                    {
                        var gpulist = new Dictionary<string, string>();
                        string vmname = vm.Members["VMName"]?.Value?.ToString() ?? string.Empty;

                        // 获取 Guid
                        Guid vmid = Guid.TryParse(vm.Members["Id"]?.Value?.ToString(), out var g) ? g : Guid.Empty;

                        string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value?.ToString() ?? string.Empty;
                        string guest = vm.Members["GuestControlledCacheTypes"]?.Value?.ToString() ?? string.Empty;
                        string notes = vm.Members["Notes"]?.Value?.ToString() ?? string.Empty;

                        var vmgpus = Utils.Run($@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                        if (vmgpus.Count > 0)
                        {
                            foreach (var gpu in vmgpus)
                            {
                                string gpupath = gpu.Members["InstancePath"]?.Value?.ToString() ?? string.Empty;
                                string gpuid = gpu.Members["Id"]?.Value?.ToString() ?? string.Empty;
                                if (string.IsNullOrEmpty(gpupath) && !string.IsNullOrEmpty(notes))
                                {
                                    string tagPrefix = "[AssignedGPU:";
                                    int startIndex = notes.IndexOf(tagPrefix);
                                    if (startIndex != -1)
                                    {
                                        startIndex += tagPrefix.Length;
                                        int endIndex = notes.IndexOf("]", startIndex);
                                        if (endIndex != -1) gpupath = notes.Substring(startIndex, endIndex - startIndex);
                                    }
                                }
                                gpulist[gpuid] = gpupath;
                            }
                        }

                        var instance = new VmInstanceInfo(vmid, vmname)
                        {
                            HighMMIO = highmmio,
                            GuestControlled = guest,
                            Notes = notes
                        };

                        // 【建议新增】将找到的第一个 GPU 路径作为友好名称显示（或者根据路径反查名称）
                        if (gpulist.Count > 0)
                        {
                            // 这里暂时把路径赋值给 GpuName，或者你可以调用 GetHostGpusAsync 后的缓存来匹配一个好听的名字
                            instance.GpuName = gpulist.Values.FirstOrDefault();
                        }

                        vmList.Add(instance);

                    }
                }
                return vmList;
            });
        }

        // ----------------------------------------------------------------------------------
        // 核心注入逻辑：挂载 -> 定位 -> Robocopy(带进度) -> 注册表 -> 卸载
        // ----------------------------------------------------------------------------------
        // [重构] 驱动注入核心逻辑
        /// <summary>
        /// 核心注入逻辑：支持虚拟磁盘(VHDX)与物理直通硬盘
        /// 采用安全剥离与原路找回机制，确保物理磁盘注入的稳定性
        /// </summary>
        private async Task<string> InjectWindowsDriversAsync(
            string vmName,
            VmDiskTarget diskTarget,
            PartitionInfo partition,
            string gpuManu,
            string gpuInstancePath,
            Action<string> progressCallback = null)
        {
            string assignedDriveLetter = null;
            int hostDiskNumber = -1;

            // 用于物理磁盘还原的坐标快照
            string savedCtrlType = "SCSI";
            int savedCtrlNum = 0;
            int savedCtrlLoc = 0;
            bool isPhysical = diskTarget.IsPhysical;

            // 日志辅助
            void Log(string msg, bool force = false) => progressCallback?.Invoke(msg);

            try
            {
                // --- 阶段 1：环境准备与磁盘剥离 ---
                if (isPhysical)
                {
                    Log($"[物理磁盘] 正在锁定并安全剥离磁盘 {diskTarget.PhysicalDiskNumber}...", true);
                    hostDiskNumber = diskTarget.PhysicalDiskNumber;

                    // 记录坐标并从 VM 中移除（解决句柄占用核心步骤）
                    var detachScript = $@"
                $ErrorActionPreference = 'Stop'
                $vmDisk = Get-VMHardDiskDrive -VMName '{vmName}' | Where-Object {{ $_.DiskNumber -eq {hostDiskNumber} }}
                if ($vmDisk) {{
                    $out = ""$($vmDisk.ControllerType),$($vmDisk.ControllerNumber),$($vmDisk.ControllerLocation)""
                    Remove-VMHardDiskDrive -VMHardDiskDrive $vmDisk
                    return $out
                }}
                throw '在虚拟机中未找到该物理磁盘，可能已被手动移除或离线。'";

                    var detachRes = Utils.Run(detachScript);
                    if (detachRes == null || detachRes.Count == 0) return "无法剥离物理磁盘：坐标捕获失败。";

                    var parts = detachRes[0].ToString().Split(',');
                    savedCtrlType = parts[0];
                    savedCtrlNum = int.Parse(parts[1]);
                    savedCtrlLoc = int.Parse(parts[2]);

                    // 宿主机强制联机
                    Utils.Run($@"
                Set-Disk -Number {hostDiskNumber} -IsOffline $false -ErrorAction Stop
                Set-Disk -Number {hostDiskNumber} -IsReadOnly $false -ErrorAction Stop
                Update-HostStorageCache");
                }
                else
                {
                    Log($"[虚拟磁盘] 正在挂载镜像: {Path.GetFileName(diskTarget.Path)}...", true);
                    // 虚拟磁盘处理逻辑：先尝试卸载残留，再重新挂载
                    var mountScript = $@"
                $path = '{diskTarget.Path}'
                Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
                $img = Mount-DiskImage -ImagePath $path -NoDriveLetter -PassThru -ErrorAction Stop
                ($img | Get-Disk).Number";

                    var mountRes = Utils.Run(mountScript);
                    if (mountRes == null || mountRes.Count == 0 || !int.TryParse(mountRes[0].ToString(), out hostDiskNumber))
                        return "挂载虚拟磁盘失败，请检查文件是否被锁定。";
                }

                // --- 阶段 2：分区挂载与盘符分配 ---
                Log($"正在为分区 {partition.PartitionNumber} 分配宿主机盘符...", true);
                char suggestedLetter = GetFreeDriveLetter();
                var assignLetterScript = $@"
            $p = Get-Partition -DiskNumber {hostDiskNumber} | Where-Object PartitionNumber -eq {partition.PartitionNumber}
            if (-not $p) {{ throw '找不到指定分区' }}
            if ($p.DriveLetter) {{ return $p.DriveLetter }}
            Set-Partition -InputObject $p -NewDriveLetter '{suggestedLetter}' -ErrorAction Stop
            return '{suggestedLetter}'";

                var letterRes = Utils.Run(assignLetterScript);
                if (letterRes == null || letterRes.Count == 0) return "无法分配驱动器盘符。";
                assignedDriveLetter = letterRes[0].ToString().TrimEnd(':') + ":";

                // --- 阶段 3：驱动程序同步 (Robocopy) ---
                string sourceFolder = FindGpuDriverSourcePath(gpuInstancePath);
                if (string.IsNullOrEmpty(sourceFolder)) sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";

                string destBase = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");
                string destFolder = Path.Combine(destBase, new DirectoryInfo(sourceFolder).Name);

                if (!Directory.Exists(destBase)) Directory.CreateDirectory(destBase);

                Log($"正在同步驱动文件: {new DirectoryInfo(sourceFolder).Name}...", true);
                using (Process p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = "robocopy.exe",
                        Arguments = $"\"{sourceFolder}\" \"{destFolder}\" /E /R:1 /W:1 /MT:32 /NDL /NJH /NJS /NC /NS",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    p.Start();
                    await p.WaitForExitAsync();
                    // Robocopy 退出码 0-7 均视为成功
                    if (p.ExitCode >= 8) return $"文件同步失败，Robocopy 错误码: {p.ExitCode}";
                }

                // --- 阶段 4：注册表处理 ---
                if (gpuManu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    Log("正在应用 NVIDIA 注册表优化...", true);
                    string regRes = NvidiaReg(assignedDriveLetter);
                    if (regRes != "OK") return regRes;
                }

                Log("注入流程已完成，准备清理环境...", true);
                return "OK";
            }
            catch (Exception ex)
            {
                return $"注入过程中发生异常: {ex.Message}";
            }
            finally
            {
                // --- 阶段 5：环境还原与清理 ---

                // 1. 移除宿主机盘符
                if (!string.IsNullOrEmpty(assignedDriveLetter) && hostDiskNumber != -1)
                {
                    Utils.Run($"Get-Partition -DiskNumber {hostDiskNumber} | Where-Object DriveLetter -eq '{assignedDriveLetter[0]}' | Remove-PartitionAccessPath -AccessPath '{assignedDriveLetter}' -ErrorAction SilentlyContinue");
                }

                if (isPhysical)
                {
                    Log("正在将物理磁盘归还给虚拟机...", true);
                    // 宿主机脱机
                    Utils.Run($@"
                Set-Disk -Number {hostDiskNumber} -IsOffline $true -Confirm:$false -ErrorAction SilentlyContinue
                Update-HostStorageCache");

                    Thread.Sleep(1500); // 给系统 1.5 秒缓冲时间刷新 PnP 状态

                    // 原路找回坐标挂回 VM
                    var reattachScript = $@"
                Add-VMHardDiskDrive -VMName '{vmName}' `
                                    -ControllerType '{savedCtrlType}' `
                                    -ControllerNumber {savedCtrlNum} `
                                    -ControllerLocation {savedCtrlLoc} `
                                    -DiskNumber {hostDiskNumber} `
                                    -ErrorAction Stop";
                    try
                    {
                        Utils.Run(reattachScript);
                    }
                    catch (Exception ex)
                    {
                        Log($"[严重警告] 磁盘未能自动挂回 VM，请手动添加物理磁盘 {hostDiskNumber} 到 {savedCtrlType} 控制器。错误: {ex.Message}", true);
                    }
                }
                else if (!string.IsNullOrEmpty(diskTarget.Path))
                {
                    Log("卸载虚拟磁盘镜像...", true);
                    Utils.Run($"Dismount-DiskImage -ImagePath '{diskTarget.Path}' -ErrorAction SilentlyContinue");
                }
            }
        }
        private void StartSleep(int ms) => Thread.Sleep(ms);
        public Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu, PartitionInfo selectedPartition, string id, SshCredentials credentials = null, Action<string> progressCallback = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                bool isWin10 = !IsWindows11OrGreater();
                var disabledGpuInstanceIds = new List<string>();
                int partitionableGpuCount = 0;

                string NormalizeForComparison(string deviceId)
                {
                    if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
                    var normalizedId = deviceId.Replace('#', '\\').ToUpper();
                    if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
                    int suffixIndex = normalizedId.IndexOf('{');
                    if (suffixIndex != -1)
                    {
                        int lastSeparatorIndex = normalizedId.LastIndexOf('\\', suffixIndex);
                        if (lastSeparatorIndex != -1)
                        {
                            normalizedId = normalizedId.Substring(0, lastSeparatorIndex);
                        }
                    }
                    return normalizedId;
                }

                void Log(string message)
                {
                    progressCallback?.Invoke(message);
                }

                try
                {
                    Utils.AddGpuAssignmentStrategyReg();
                    Utils.ApplyGpuPartitionStrictModeFix();

                    if (selectedPartition != null)
                    {
                        var vmStateResult = Utils.Run($"(Get-VM -Name '{vmName}').State");
                        if (vmStateResult == null || vmStateResult.Count == 0 || vmStateResult[0].ToString() != "Off")
                        {
                            return string.Format(Properties.Resources.Error_VmMustBeOff, vmName);
                        }
                    }

                    if (isWin10)
                    {
                        var allHostGpus = await GetHostGpusAsync();
                        partitionableGpuCount = allHostGpus.Count(gpu => !string.IsNullOrEmpty(gpu.Pname));
                        string normalizedSelectedGpuId = NormalizeForComparison(gpuInstancePath);

                        foreach (var gpu in allHostGpus)
                        {
                            if (!gpu.InstanceId.ToUpper().StartsWith("PCI\\")) continue;

                            string normalizedCurrentGpuId = NormalizeForComparison(gpu.InstanceId);

                            if (!string.Equals(normalizedCurrentGpuId, normalizedSelectedGpuId, StringComparison.OrdinalIgnoreCase))
                            {
                                disabledGpuInstanceIds.Add(gpu.InstanceId);
                            }
                        }

                        if (disabledGpuInstanceIds.Any())
                        {
                            foreach (var disabledId in disabledGpuInstanceIds)
                            {
                                Utils.Run($"Disable-PnpDevice -InstanceId '{disabledId}' -Confirm:$false");
                            }
                            await Task.Delay(2000);
                        }
                    }

                    string addGpuCommand = isWin10
                        ? $"Add-VMGpuPartitionAdapter -VMName '{vmName}'"
                        : $"Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'";

                    string vmConfigScript;
                    if (selectedPartition == null)
                    {
                        vmConfigScript = addGpuCommand;
                    }
                    else
                    {
                        vmConfigScript = $@"
                        Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}';
                        Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmName}';
                        Set-VM -LowMemoryMappedIoSpace 1GB -VMName '{vmName}';
                        {addGpuCommand};
                        ";
                    }
                    Utils.Run(vmConfigScript);
                    if (isWin10)
                    {
                        string gpuTag = $"[AssignedGPU:{gpuInstancePath}]";
                        string updateNotesScript = $@"
                        $vm = Get-VM -Name '{vmName}';
                        $currentNotes = $vm.Notes;
                        $cleanedNotes = $currentNotes -replace '\[AssignedGPU:[^\]]+\]', '';
                        $newNotes = ($cleanedNotes.Trim() + ' ' + '{gpuTag}').Trim();
                        Set-VM -VM $vm -Notes $newNotes;
                        ";
                        Utils.Run(updateNotesScript);
                    }

                    if (selectedPartition != null)
                    {
                        if (selectedPartition.OsType == OperatingSystemType.Windows)
                        {
                            // [修复 CS1503]
                            // 不再获取字符串路径，而是获取 VmDiskTarget 对象
                            Log("正在识别虚拟机启动磁盘...");
                            var diskTarget = await GetVmBootDiskInfoAsync(vmName);
                            if (diskTarget == null)
                            {
                                return ExHyperV.Properties.Resources.Error_GetVmHardDiskPathFailed;
                            }

                            // 传入 diskTarget 对象 (修复了参数类型不匹配)
                            string injectionResult = await InjectWindowsDriversAsync(vmName, diskTarget, selectedPartition, gpuManu, id, Log);

                            if (injectionResult != "OK")
                            {
                                return injectionResult;
                            }
                        }
                        else if (selectedPartition.OsType == OperatingSystemType.Linux)
                        {
                            // Linux 逻辑保持不变，调用我们之前定义好的方法
                            // 注意：Linux 是通过网络 SSH 传输，不需要挂载物理磁盘，所以不受 VmDiskTarget 影响
                            return await ProvisionLinuxGpuAsync(vmName, id, credentials, Log, cancellationToken);
                        }
                    }

                    if (isWin10 && partitionableGpuCount > 1)
                    {
                        Utils.Run($"Start-VM -Name '{vmName}'");
                    }
                    return "OK";
                }
                catch (Exception ex)
                {
                    Log(string.Format(Properties.Resources.Error_FatalExceptionOccurred, ex.Message));
                    return string.Format(Properties.Resources.Error_OperationFailed, ex.Message);
                }
                finally
                {
                    if (disabledGpuInstanceIds.Any())
                    {
                        await Task.Delay(1000);
                        foreach (var instanceId in disabledGpuInstanceIds)
                        {
                            Utils.Run($"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false");
                        }
                    }
                    if (isWin10 && partitionableGpuCount > 1)
                    {
                        Log(Properties.Resources.Warning_Win10GpuAssignmentNotPersistent);
                    }
                }
            });
        }

        public Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId)
        {
            return Task.Run(() =>
            {
                var results = Utils.Run2($@"Remove-VMGpuPartitionAdapter -VMName '{vmName}' -AdapterId '{adapterId}' -Confirm:$false");
                if (results != null)
                {
                    string cleanupNotesScript = $@"
                    $vm = Get-VM -Name '{vmName}';
                    $currentNotes = $vm.Notes;
                    if ($currentNotes -match '\[AssignedGPU:[^\]]+\]') {{
                    $cleanedNotes = $currentNotes -replace '\[AssignedGPU:[^\]]+\]', '';
                    Set-VM -VM $vm -Notes $cleanedNotes.Trim();
                        }}
                    ";
                    Utils.Run(cleanupNotesScript);
                    return true;
                }
                return false;
            });
        }

        private string NvidiaReg(string letter)
        {
            // 为NVIDIA显卡注入注册表信息
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
                if (File.Exists(tempRegFile))
                {
                    File.Delete(tempRegFile);
                }
            }
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
            catch
            {
                return -1;
            }
        }

        private void SetFolderReadOnly(string folderPath)
        {
            var dirInfo = new DirectoryInfo(folderPath);
            dirInfo.Attributes |= FileAttributes.ReadOnly;
            foreach (var subDir in dirInfo.GetDirectories())
            {
                SetFolderReadOnly(subDir.FullName);
            }
            foreach (var file in dirInfo.GetFiles())
            {
                file.Attributes |= FileAttributes.ReadOnly;
            }
        }

        private void RemoveReadOnlyAttribute(string path)
        {
            if (Directory.Exists(path))
            {
                RemoveReadOnlyAttribute(new DirectoryInfo(path));
            }
        }

        private void RemoveReadOnlyAttribute(DirectoryInfo dirInfo)
        {
            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var subDir in dirInfo.GetDirectories())
            {
                RemoveReadOnlyAttribute(subDir);
            }
            foreach (var file in dirInfo.GetFiles())
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        public async Task<bool> IsHyperVModuleAvailableAsync()
        {
            return await Task.Run(() =>
            {
                var result = Utils.Run("(Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V).State");
                return result.Count > 0;
            });
        }

        private async Task UploadLocalFilesAsync(SshService sshService, SshCredentials credentials, string remoteDirectory)
        {
            string systemWslLibPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lxss", "lib");

            if (Directory.Exists(systemWslLibPath))
            {
                var allFiles = Directory.GetFiles(systemWslLibPath);

                foreach (var filePath in allFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    await sshService.UploadFileAsync(credentials, filePath, $"{remoteDirectory}/{fileName}");
                }
            }
        }

        private string FindGpuDriverSourcePath(string gpuInstancePath)
        {
            string sourceFolder = null;
            string fastScript = $@"
    $ErrorActionPreference = 'Stop';
    try {{
        $targetId = '{gpuInstancePath}'.Trim();
        $wmi = Get-CimInstance Win32_VideoController | Where-Object {{ $_.PNPDeviceID -like ""*$targetId*"" }} | Select-Object -First 1;
        
        if ($wmi -and $wmi.InstalledDisplayDrivers) {{
            $drivers = $wmi.InstalledDisplayDrivers -split ',';
            $repoDriver = $drivers | Where-Object {{ $_ -match 'FileRepository' }} | Select-Object -First 1;
            
            if ($repoDriver) {{
                $currentPath = Split-Path -Parent $repoDriver.Trim();
                while ($true) {{
                    if (Get-ChildItem -Path $currentPath -Filter *.inf -ErrorAction SilentlyContinue) {{
                        return $currentPath;
                    }}
                    $parentPath = Split-Path -Parent $currentPath;
                    $parentName = Split-Path -Leaf $parentPath;
                    if ($parentName -eq 'FileRepository') {{
                        return $currentPath;
                    }}
                    if ($parentPath -eq $currentPath) {{ break; }}
                    $currentPath = $parentPath;
                }}
                return (Split-Path -Parent $repoDriver.Trim());
            }}
        }}
    }} catch {{ }}";

            try
            {
                var fastRes = Utils.Run(fastScript);
                if (fastRes != null && fastRes.Count > 0 && fastRes[0] != null)
                {
                    string resultPath = fastRes[0].ToString().Trim();
                    if (!string.IsNullOrEmpty(resultPath) && Directory.Exists(resultPath))
                    {
                        sourceFolder = resultPath;
                    }
                }
            }
            catch { }
            return sourceFolder;
        }



        // ----------------------------------------------------------------------------------
        // 重构方法 1：系统环境准备
        // 职责：应用宿主机 GPU 分区策略和注册表修复
        // ----------------------------------------------------------------------------------
        public Task PrepareHostEnvironmentAsync()
        {
            return Task.Run(() =>
            {
                // 对应旧逻辑中的环境修复
                Utils.AddGpuAssignmentStrategyReg();
                Utils.ApplyGpuPartitionStrictModeFix();
            });
        }

        // ----------------------------------------------------------------------------------
        // 重构方法 2：虚拟机电源状态检查
        // 职责：验证虚拟机是否已关闭
        // ----------------------------------------------------------------------------------
        public Task<(bool IsOff, string CurrentState)> IsVmPoweredOffAsync(string vmName)
        {
            return Task.Run(() =>
            {
                var result = Utils.Run($"(Get-VM -Name '{vmName}').State");
                string state = result != null && result.Count > 0 ? result[0].ToString() : "Unknown";

                // Hyper-V 的 Off 状态通常返回 "Off"
                bool isOff = state.Equals("Off", StringComparison.OrdinalIgnoreCase);
                return (isOff, state);
            });
        }

        // ----------------------------------------------------------------------------------
        // 重构方法 3：配置系统优化 (MMIO)
        // 职责：在分配硬件前，先配置大容量内存映射空间和缓存策略
        // ----------------------------------------------------------------------------------
        public Task<bool> OptimizeVmForGpuAsync(string vmName)
        {
            return Task.Run(() =>
            {
                try
                {
                    // 对应旧逻辑中的 MMIO 设置
                    string vmConfigScript = $@"
                Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}';
                Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmName}';
                Set-VM -LowMemoryMappedIoSpace 1GB -VMName '{vmName}';
            ";
                    Utils.Run(vmConfigScript);
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        // ----------------------------------------------------------------------------------
        // 步骤4：分配显卡资源 - 真正执行创建 GPU 分区的命令
        // 职责：处理 Windows 10 兼容性逻辑并执行 Add-VMGpuPartitionAdapter
        // ----------------------------------------------------------------------------------

        public Task<(bool Success, string Message)> AssignGpuPartitionAsync(string vmName, string gpuInstancePath)
        {
            return Task.Run(async () =>
            {
                bool isWin10 = !IsWindows11OrGreater();
                var disabledGpuInstanceIds = new List<string>();

                string NormalizeForComparison(string deviceId)
                {
                    if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
                    var normalizedId = deviceId.Replace('#', '\\').ToUpper();
                    if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
                    int suffixIndex = normalizedId.IndexOf('{');
                    if (suffixIndex != -1)
                    {
                        int lastSeparatorIndex = normalizedId.LastIndexOf('\\', suffixIndex);
                        if (lastSeparatorIndex != -1) normalizedId = normalizedId.Substring(0, lastSeparatorIndex);
                    }
                    return normalizedId;
                }

                try
                {
                    // 1. Win10 兼容性处理 (禁用非目标显卡)
                    if (isWin10)
                    {
                        var allHostGpus = await GetHostGpusAsync();
                        string normalizedSelectedGpuId = NormalizeForComparison(gpuInstancePath);

                        foreach (var gpu in allHostGpus)
                        {
                            if (!gpu.InstanceId.ToUpper().StartsWith("PCI\\")) continue;
                            string normalizedCurrentGpuId = NormalizeForComparison(gpu.InstanceId);
                            if (!string.Equals(normalizedCurrentGpuId, normalizedSelectedGpuId, StringComparison.OrdinalIgnoreCase))
                            {
                                disabledGpuInstanceIds.Add(gpu.InstanceId);
                            }
                        }

                        if (disabledGpuInstanceIds.Any())
                        {
                            foreach (var disabledId in disabledGpuInstanceIds)
                            {
                                Utils.Run($"Disable-PnpDevice -InstanceId '{disabledId}' -Confirm:$false");
                            }
                            await Task.Delay(2000);
                        }
                    }

                    // 2. 执行分配命令
                    string addGpuCommand = isWin10
                        ? $"Add-VMGpuPartitionAdapter -VMName '{vmName}'"
                        : $"Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'";

                    Utils.Run(addGpuCommand);

                    // ✅ 3. 【关键增强：双重验证】
                    // 立即反查虚拟机当前的 GPU 分区适配器列表
                    var verifyResult = Utils.Run($"Get-VMGpuPartitionAdapter -VMName '{vmName}'");

                    // 如果返回列表为空，说明分配动作虽然没崩溃，但由于权限、驱动或内核原因失败了
                    if (verifyResult == null || verifyResult.Count == 0)
                    {
                        return (false, "PowerShell 命令已执行但未产生分区。请检查宿主机 GPU 是否支持分区，以及是否以管理员权限运行。");
                    }

                    // 4. 写入标签并返回成功
                    string gpuTag = $"[AssignedGPU:{gpuInstancePath}]";
                    string updateNotesScript = $@"
                $vm = Get-VM -Name '{vmName}';
                $currentNotes = $vm.Notes;
                $cleanedNotes = $currentNotes -replace '\[AssignedGPU:[^\]]+\]', '';
                $newNotes = ($cleanedNotes.Trim() + ' ' + '{gpuTag}').Trim();
                Set-VM -VM $vm -Notes $newNotes;
            ";
                    Utils.Run(updateNotesScript);

                    return (true, "OK");
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
                finally
                {
                    if (disabledGpuInstanceIds.Any())
                    {
                        await Task.Delay(1000);
                        foreach (var instanceId in disabledGpuInstanceIds)
                        {
                            Utils.Run($"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false");
                        }
                    }
                }
            });
        }
        // ----------------------------------------------------------------------------------
        // 步骤5：驱动程序同步 (Windows) - 自动化注入宿主机驱动
        // 职责：驱动 Windows 离线注入流程的入口方法
        // ----------------------------------------------------------------------------------
        private Task<VmDiskTarget> GetVmBootDiskInfoAsync(string vmName)
        {
            return Task.Run(() =>
            {
                // [关键修正] PowerShell 脚本现在直接检查 DiskNumber 属性，而不是脆弱的 Path 字符串
                var script = $@"
                    $drive = Get-VMHardDiskDrive -VMName '{vmName}' | Sort-Object ControllerNumber, ControllerLocation | Select-Object -First 1
                    if ($drive -eq $null) {{ return 'NONE' }}
                    
                    # 物理直通盘的核心特征是 .DiskNumber 属性有值
                    if ($drive.DiskNumber -ne $null) {{
                        return 'PHYSICAL:' + $drive.DiskNumber
                    }} 
                    # 虚拟盘的特征是 .Path 属性是一个文件路径
                    elseif (-not [string]::IsNullOrWhiteSpace($drive.Path)) {{
                        return 'VHD:' + $drive.Path
                    }}
                    
                    return 'UNKNOWN'
                ";

                var result = Utils.Run(script);
                if (result == null || result.Count == 0 || result[0] == null) return null;

                string raw = result[0].ToString();

                if (raw.StartsWith("PHYSICAL:"))
                {
                    if (int.TryParse(raw.Substring(9), out int num))
                    {
                        return new VmDiskTarget { IsPhysical = true, PhysicalDiskNumber = num };
                    }
                }
                else if (raw.StartsWith("VHD:"))
                {
                    return new VmDiskTarget { IsPhysical = false, Path = raw.Substring(4) };
                }

                return null;
            });
        }


        public async Task<(bool Success, string Message)> SyncWindowsDriversAsync(
            string vmName,
            string gpuInstancePath,
            string gpuManu,
            PartitionInfo selectedPartition,
            Action<string> progressCallback = null)
        {
            // 1. 基础校验
            if (selectedPartition == null || selectedPartition.OsType != OperatingSystemType.Windows)
            {
                return (false, "未选择有效的 Windows 分区。");
            }

            try
            {
                // 2. 获取虚拟机第一个虚拟硬盘路径
                var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                    return (false, "未能获取虚拟机硬盘文件路径。");

                var diskTarget = await GetVmBootDiskInfoAsync(vmName);

                if (diskTarget == null)
                    return (false, "未能获取虚拟机硬盘信息 (可能是未挂载硬盘)。");

                // [修复 CS1503] 这里的参数现在匹配了
                string result = await InjectWindowsDriversAsync(vmName, diskTarget, selectedPartition, gpuManu, gpuInstancePath, progressCallback);
                
                return result == "OK" ? (true, "OK") : (false, result);
            }
            catch (Exception ex)
            {
                return (false, $"驱动程序同步异常: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------------------------
        // 步骤6：Linux 驱动配置 (在线模式)
        // 职责：开机 -> SSH连接 -> 传输文件 -> 编译内核模块 -> 重启
        // ----------------------------------------------------------------------------------
        public Task<string> ProvisionLinuxGpuAsync(string vmName, string gpuInstancePath, SshCredentials credentials, Action<string> progressCallback, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                // 辅助函数：输出日志
                void Log(string message) => progressCallback?.Invoke(message);

                // 辅助函数：Sudo 命令包装
                Func<string, string> withSudo = (cmd) =>
                {
                    if (cmd.Trim().StartsWith("sudo ")) cmd = cmd.Trim().Substring(5);
                    string escapedPassword = credentials.Password.Replace("'", "'\\''");
                    return $"echo '{escapedPassword}' | sudo -S -E -p '' {cmd}";
                };

                var sshService = new SshService();

                try
                {
                    // 1. 确保虚拟机已启动
                    var currentState = await GetVmStateAsync(vmName);
                    if (currentState != "Running")
                    {
                        Log("正在启动虚拟机以进行 Linux 配置...");
                        Utils.Run($"Start-VM -Name '{vmName}'");
                        await Task.Delay(5000); // 等待 BIOS/UEFI 初始化
                    }

                    // 2. 获取 IP 地址
                    Log("正在等待虚拟机网络就绪并获取 IP...");
                    string getMacScript = $"(Get-VMNetworkAdapter -VMName '{vmName}').MacAddress | Select-Object -First 1";
                    var macResult = await Utils.Run2(getMacScript);
                    if (macResult == null || macResult.Count == 0) return string.Format(Properties.Resources.Error_GetVmMacAddressFailed, vmName);

                    string macAddress = System.Text.RegularExpressions.Regex.Replace(macResult[0].ToString(), "(.{2})", "$1:").TrimEnd(':');
                    string vmIpAddress = await Utils.GetVmIpAddressAsync(vmName, macAddress);

                    string targetIp = vmIpAddress.Split(',')
                        .Select(ip => ip.Trim())
                        .FirstOrDefault(ip => System.Net.IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                    if (string.IsNullOrEmpty(targetIp)) return Properties.Resources.Error_NoValidIpv4AddressFound;

                    credentials.Host = targetIp;
                    Log($"虚拟机 IP 已获取: {targetIp}，正在建立 SSH 连接...");

                    // 3. 等待 SSH 端口响应
                    if (!await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, cancellationToken))
                    {
                        return "SSH 连接超时，请检查虚拟机网络设置。";
                    }

                    // 4. 初始化远程环境
                    string homeDirectory;
                    string remoteTempDir;
                    using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                    {
                        client.Connect();
                        homeDirectory = client.RunCommand("pwd").Result.Trim();
                        remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                        client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                        client.Disconnect();
                    }
                    Log($"远程环境初始化完成，临时目录: {remoteTempDir}");

                    // 5. 配置代理 (如果有)
                    if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                    {
                        Log($"配置 APT 和环境变量代理 ({credentials.ProxyHost}:{credentials.ProxyPort})...");
                        string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";
                        string aptContent = $"Acquire::http::Proxy \"{proxyUrl}\";\nAcquire::https::Proxy \"{proxyUrl}\";\n";
                        string envContent = $"\nexport http_proxy=\"{proxyUrl}\"\nexport https_proxy=\"{proxyUrl}\"\nexport no_proxy=\"localhost,127.0.0.1\"\n";

                        await sshService.WriteTextFileAsync(credentials, aptContent, $"{homeDirectory}/99proxy");
                        await sshService.WriteTextFileAsync(credentials, envContent, $"{homeDirectory}/proxy_env");

                        await sshService.ExecuteSingleCommandAsync(credentials, $"sudo mv {homeDirectory}/99proxy /etc/apt/apt.conf.d/99proxy", Log);
                        await sshService.ExecuteSingleCommandAsync(credentials, $"sudo sh -c 'cat {homeDirectory}/proxy_env >> /etc/environment'", Log);
                    }

                    // 6. 上传宿主机驱动文件
                    Log("正在定位并上传宿主机 GPU 驱动...");
                    string sourceDriverPath = FindGpuDriverSourcePath(gpuInstancePath); // 调用类中原有的私有方法
                    if (string.IsNullOrEmpty(sourceDriverPath))
                    {
                        Log("警告：无法精确定位驱动，使用全量 DriverStore (速度较慢)...");
                        sourceDriverPath = @"C:\Windows\System32\DriverStore\FileRepository";
                    }

                    string sourceFolderName = new DirectoryInfo(sourceDriverPath).Name;
                    await sshService.UploadDirectoryAsync(credentials, sourceDriverPath, $"{remoteTempDir}/drivers/{sourceFolderName}");

                    // 7. 上传 WSL 库文件
                    Log("正在上传 WSL 依赖库...");
                    await UploadLocalFilesAsync(sshService, credentials, $"{remoteTempDir}/lib"); // 调用类中原有的私有方法

                    // 8. 下载并执行配置脚本
                    Log("正在下载配置脚本...");
                    var scripts = new List<string> { "install_dxgkrnl.sh", "configure_system.sh" };
                    if (credentials.InstallGraphics) scripts.Add("setup_graphics.sh");

                    foreach (var script in scripts)
                    {
                        string cmd = $"wget -O {remoteTempDir}/{script} {ScriptBaseUrl}{script}";
                        await sshService.ExecuteSingleCommandAsync(credentials, cmd, Log);
                    }
                    await sshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteTempDir}/*.sh", Log);

                    // 9. 编译 dxgkrnl
                    Log("正在编译安装 dxgkrnl 内核模块 (这可能需要一些时间)...");
                    var dxgResult = await sshService.ExecuteCommandAndCaptureOutputAsync(credentials, withSudo($"{remoteTempDir}/install_dxgkrnl.sh"), Log, TimeSpan.FromMinutes(60));

                    if (dxgResult.Output.Contains("STATUS: REBOOT_REQUIRED"))
                    {
                        Log("内核已更新，正在重启虚拟机以应用更改...");
                        try { await sshService.ExecuteSingleCommandAsync(credentials, withSudo("reboot"), Log); } catch { }

                        // 这里返回特殊状态，由上层决定是否等待重启后再次调用本方法继续后续步骤，
                        // 或者在这里写循环等待逻辑（原代码是写在一起的）。
                        // 建议：为了保持方法纯粹，返回状态让上层处理重连比较好，但为了兼容原逻辑的"一键式"，这里可以抛出异常或返回特定字符串。
                        return "REBOOT_REQUIRED";
                    }

                    if (!dxgResult.Output.Contains("STATUS: SUCCESS"))
                    {
                        throw new Exception("dxgkrnl 编译脚本执行失败，请检查日志。");
                    }

                    // 10. 配置图形和系统
                    if (credentials.InstallGraphics)
                    {
                        Log("正在配置 Mesa 3D...");
                        await sshService.ExecuteSingleCommandAsync(credentials, withSudo($"{remoteTempDir}/setup_graphics.sh"), Log, TimeSpan.FromMinutes(20));
                    }

                    Log("正在完成系统最终配置...");
                    string configArgs = credentials.InstallGraphics ? "enable_graphics" : "no_graphics";
                    await sshService.ExecuteSingleCommandAsync(credentials, withSudo($"{remoteTempDir}/configure_system.sh {configArgs}"), Log);

                    // 11. 最终重启
                    Log("配置完成，正在重启虚拟机...");
                    try { await sshService.ExecuteSingleCommandAsync(credentials, withSudo("reboot"), Log); } catch { }

                    return "OK";
                }
                catch (OperationCanceledException)
                {
                    return "Operation Cancelled";
                }
                catch (Exception ex)
                {
                    return $"Linux 配置失败: {ex.Message}";
                }
            });
        }
    }
}