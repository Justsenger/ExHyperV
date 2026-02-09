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
                string harddiskpath = null;
                try
                {
                    // 1. 获取硬盘路径
                    var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                    if (harddiskPathResult == null || harddiskPathResult.Count == 0 || harddiskPathResult[0] == null)
                    {
                        throw new FileNotFoundException(string.Format(Properties.Resources.Error_VmHardDiskNotFound, vmName));
                    }
                    harddiskpath = harddiskPathResult[0].ToString();

                    // ✅ 【新增】防御性卸载：防止上次残留导致挂载失败
                    Utils.Run($"Dismount-DiskImage -ImagePath '{harddiskpath}' -ErrorAction SilentlyContinue");
                    // 稍微等待系统释放句柄
                    Thread.Sleep(500);

                    // 2. 挂载并获取磁盘编号
                    var mountScript = $@"
                $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -NoDriveLetter -PassThru;
                if ($diskImage) {{ ($diskImage | Get-Disk).Number }}
            ";
                    var mountResult = Utils.Run(mountScript);

                    if (mountResult == null || mountResult.Count == 0 || mountResult[0] == null || !int.TryParse(mountResult[0].ToString(), out int num))
                    {
                        throw new InvalidOperationException(ExHyperV.Properties.Resources.Error_MountVhdOrGetDiskNumberFailed);
                    }

                    string devicePath = $@"\\.\PhysicalDrive{num}";
                    var diskParser = new DiskParserService();
                    return diskParser.GetPartitions(devicePath);
                }
                catch (Exception ex)
                {
                    // 捕获所有可能的异常并记录详细信息，防止 NullReference 向上蔓延
                    Debug.WriteLine($"分区获取失败: {ex.Message}");
                    throw;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(harddiskpath))
                    {
                        Utils.Run($"Dismount-DiskImage -ImagePath '{harddiskpath}' -ErrorAction SilentlyContinue");
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
        private async Task<string> InjectWindowsDriversAsync(
            string vmName,
            string harddiskpath,
            PartitionInfo partition,
            string gpuManu,
            string gpuInstancePath,
            Action<string> progressCallback = null)
        {
            string assignedDriveLetter = null;
            DateTime lastLogTime = DateTime.MinValue;
            void Log(string msg, bool force = false)
            {
                if (progressCallback == null) return;
                if (force || (DateTime.Now - lastLogTime).TotalMilliseconds > 150)
                {
                    progressCallback.Invoke(msg);
                    lastLogTime = DateTime.Now;
                }
            }

            try
            {
                // ✅ 【新增】防御性卸载：确保开始前磁盘是干净的
                Log("正在初始化磁盘环境...", true);
                Utils.Run($"Dismount-DiskImage -ImagePath '{harddiskpath}' -ErrorAction SilentlyContinue");
                await Task.Delay(800);

                // --- 1. 挂载分区 ---
                Log("正在挂载虚拟硬盘系统分区...", true);
                char suggestedLetter = GetFreeDriveLetter();
                var mountScript = $@"
            $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -PassThru | Get-Disk;
            $partitionToMount = Get-Partition -DiskNumber $diskImage.Number | Where-Object {{ $_.PartitionNumber -eq {partition.PartitionNumber} }};
            if ($partitionToMount.DriveLetter) {{ return $partitionToMount.DriveLetter }}
            try {{
                $partitionToMount | Set-Partition -NewDriveLetter '{suggestedLetter}' -ErrorAction Stop;
                return '{suggestedLetter}'
            }} catch {{
                return ($partitionToMount | Get-Partition).DriveLetter
            }}";

                var letterResult = Utils.Run(mountScript);
                if (letterResult == null || letterResult.Count == 0 || letterResult[0] == null) return "无法挂载虚拟磁盘：挂载命令未返回结果。";
                assignedDriveLetter = letterResult[0].ToString().TrimEnd(':') + ":";

                // --- 2. 路径准备 ---
                string system32Path = Path.Combine(assignedDriveLetter, "Windows", "System32");
                if (!Directory.Exists(system32Path)) return "挂载失败：未在目标分区找到 System32 目录。";

                string sourceFolder = FindGpuDriverSourcePath(gpuInstancePath);
                if (string.IsNullOrEmpty(sourceFolder)) sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";

                string destinationBase = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");
                string destinationFolder = sourceFolder.EndsWith("FileRepository") ? destinationBase : Path.Combine(destinationBase, new DirectoryInfo(sourceFolder).Name);

                if (!Directory.Exists(destinationBase)) Directory.CreateDirectory(destinationBase);

                // --- 3. [优化] 预计算总数以实现整体进度 ---
                Log("正在分析同步任务...", true);
                int totalFiles = 0;
                await Task.Run(() => {
                    totalFiles = Directory.EnumerateFiles(sourceFolder, "*.*", SearchOption.AllDirectories).Count();
                });

                // --- 4. [优化] 执行 Robocopy 并通过行数计算整体百分比 ---
                Log($"开始同步驱动 ({totalFiles} 个文件)...", true);
                Process robocopyProcess = new()
                {
                    StartInfo = {
                FileName = "robocopy",
                // 使用 /MT:32 开启32线程，/NDL /NJH /NJS 隐藏冗余信息
                Arguments = $"\"{sourceFolder}\" \"\"{destinationFolder}\"\" /E /R:1 /W:1 /MT:32 /NDL /NJH /NJS /NC /NS",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
                };

                int processedFiles = 0;
                robocopyProcess.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    Interlocked.Increment(ref processedFiles);

                    // 计算整体进度
                    double percent = (double)processedFiles / Math.Max(1, totalFiles) * 100.0;
                    if (percent > 100) percent = 100;
                    Log($"进度: {percent:F1}% ({processedFiles}/{totalFiles})");
                };

                robocopyProcess.Start();
                robocopyProcess.BeginOutputReadLine();
                await robocopyProcess.WaitForExitAsync();

                if (robocopyProcess.ExitCode >= 8) return $"Robocopy 错误，代码: {robocopyProcess.ExitCode}";

                // --- 5. NVIDIA 注册表注入 ---
                if (gpuManu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    Log("正在注入 NVIDIA 驱动注册表项...", true);
                    NvidiaReg(assignedDriveLetter);
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"驱动安装失败: {ex.Message}";
            }
            finally
            {
                if (!string.IsNullOrEmpty(harddiskpath))
                {
                    Log("正在卸载虚拟磁盘并清理环境...", true);
                    Utils.Run($"Dismount-DiskImage -ImagePath '{harddiskpath}' -ErrorAction SilentlyContinue");
                }
            }
        }

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

                // 替代原有的 ShowMessageOnUIThread
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
                            var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                            if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                            {
                                return ExHyperV.Properties.Resources.Error_GetVmHardDiskPathFailed;
                            }
                            string harddiskpath = harddiskPathResult[0].ToString();
                            string injectionResult = await InjectWindowsDriversAsync(vmName, harddiskpath, selectedPartition, gpuManu, id);
                            if (injectionResult != "OK")
                            {
                                return injectionResult;
                            }
                        }
                        else if (selectedPartition.OsType == OperatingSystemType.Linux)
                        {
                            var sshService = new SshService();

                            Func<string, string> withSudo = (cmd) =>
                            {
                                // 移除命令开头可能存在的 sudo，以防重复
                                if (cmd.Trim().StartsWith("sudo "))
                                {
                                    cmd = cmd.Trim().Substring(5);
                                }
                                // 对密码中的单引号进行转义，防止shell注入
                                string escapedPassword = credentials.Password.Replace("'", "'\\''");
                                return $"echo '{escapedPassword}' | sudo -S -E -p '' {cmd}";
                            };

                            try
                            {
                                var currentState = await GetVmStateAsync(vmName);
                                if (currentState != "Running")
                                {
                                    Utils.Run($"Start-VM -Name '{vmName}'");
                                }
                                string getMacScript = $"(Get-VMNetworkAdapter -VMName '{vmName}').MacAddress | Select-Object -First 1";
                                var macResult = await Utils.Run2(getMacScript);
                                if (macResult == null || macResult.Count == 0 || string.IsNullOrEmpty(macResult[0]?.ToString()))
                                {
                                    return string.Format(Properties.Resources.Error_GetVmMacAddressFailed, vmName);
                                }
                                string macAddressWithoutColons = macResult[0].ToString();
                                string macAddressWithColons = System.Text.RegularExpressions.Regex.Replace(macAddressWithoutColons, "(.{2})", "$1:").TrimEnd(':');

                                string vmIpAddress = string.Empty;
                                var stopwatch = Stopwatch.StartNew();
                                vmIpAddress = await Utils.GetVmIpAddressAsync(vmName, macAddressWithColons);
                                stopwatch.Stop();

                                string targetIp = vmIpAddress.Split(',').Select(ip => ip.Trim()).FirstOrDefault(ip => System.Net.IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                                // 重构：不再弹出登录框，而是检查传入的 credentials
                                if (credentials == null)
                                {
                                    return "SSH credentials not provided for Linux VM.";
                                }

                                if (!string.IsNullOrEmpty(targetIp))
                                {
                                    credentials.Host = targetIp;
                                }
                                if (string.IsNullOrEmpty(credentials.Host))
                                {
                                    return string.Format(Properties.Resources.Error_NoValidIpv4AddressFound, "Unknown");
                                }

                            }
                            catch (Exception ex)
                            {
                                Log(string.Format(Properties.Resources.Error_PreparationFailed, ex.Message));
                                return string.Format(Properties.Resources.Error_PreparationFailed, ex.Message);
                            }

                            // 重构：移除 ExecutionProgressWindow 逻辑，使用 Log 回调

                            // 部署循环，原代码支持 Retry，现在改为单次执行，如有异常直接返回错误
                            try
                            {
                                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException();

                                Log(ExHyperV.Properties.Resources.LinuxDeploy_Step1);
                                string homeDirectory;
                                string remoteTempDir;

                                using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                                {
                                    client.Connect();
                                    Log(ExHyperV.Properties.Resources.Log_SshConnectionSuccess);
                                    var pwdResult = client.RunCommand("pwd");
                                    homeDirectory = pwdResult.Result.Trim();
                                    if (string.IsNullOrEmpty(homeDirectory))
                                    {
                                        throw new Exception(ExHyperV.Properties.Resources.Error_GetLinuxHomeDirectoryFailed);
                                    }
                                    Log(string.Format(Properties.Resources.Log_LinuxHomeDirectoryFound, homeDirectory));

                                    remoteTempDir = $"{homeDirectory}/exhyperv_deploy";
                                    client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                                    Log(string.Format(Properties.Resources.Log_TempDeployDirectoryCreated, remoteTempDir));
                                    client.Disconnect();
                                }
                                Log(ExHyperV.Properties.Resources.Log_RemoteEnvInitializationComplete);

                                if (!string.IsNullOrEmpty(credentials.ProxyHost) && credentials.ProxyPort.HasValue)
                                {
                                    Log(ExHyperV.Properties.Resources.LinuxDeploy_Step2);
                                    Log(string.Format(Properties.Resources.Log_ProxyServerInfo, credentials.ProxyHost, credentials.ProxyPort));
                                    string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";
                                    string aptProxyContent = $"Acquire::http::Proxy \"{proxyUrl}\";\nAcquire::https::Proxy \"{proxyUrl}\";\n";
                                    string envProxyContent = $"\nexport http_proxy=\"{proxyUrl}\"\nexport https_proxy=\"{proxyUrl}\"\nexport no_proxy=\"localhost,127.0.0.1\"\n";
                                    string remoteAptProxyFile = $"{homeDirectory}/99proxy";
                                    string remoteEnvProxyFile = $"{homeDirectory}/proxy_env";
                                    await sshService.WriteTextFileAsync(credentials, aptProxyContent, remoteAptProxyFile);
                                    await sshService.WriteTextFileAsync(credentials, envProxyContent, remoteEnvProxyFile);
                                    var proxyCommands = new List<string>
                                    {
                                        $"sudo mv {remoteAptProxyFile} /etc/apt/apt.conf.d/99proxy",
                                        $"sudo sh -c 'cat {remoteEnvProxyFile} >> /etc/environment'",
                                        $"rm {remoteEnvProxyFile}",
                                        $"export http_proxy={proxyUrl}",
                                        $"export https_proxy={proxyUrl}"
                                    };
                                    foreach (var cmd in proxyCommands)
                                    {
                                        await sshService.ExecuteSingleCommandAsync(credentials, cmd, Log, TimeSpan.FromSeconds(30));
                                    }
                                    Log(ExHyperV.Properties.Resources.Log_ProxyConfigurationComplete);
                                }

                                Log(ExHyperV.Properties.Resources.LinuxDeploy_Step3);
                                string driverStoreBase = @"C:\Windows\System32\DriverStore\FileRepository";
                                string preciseDriverPath = FindGpuDriverSourcePath(id);
                                string sourceDriverPath = preciseDriverPath; // 默认为精准路径

                                if (string.IsNullOrEmpty(preciseDriverPath))
                                {
                                    Log(ExHyperV.Properties.Resources.Log_GpuDriverNotFoundFallback);
                                    sourceDriverPath = driverStoreBase; // 回退到全量拷贝
                                }
                                else
                                {
                                    Log(string.Format(Properties.Resources.Log_PreciseDriverPathLocated, new DirectoryInfo(preciseDriverPath).Name));
                                }

                                Log(ExHyperV.Properties.Resources.LinuxDeploy_Step3_Status_Import);
                                string sourceFolderName = new DirectoryInfo(sourceDriverPath).Name;
                                string remoteDestinationPath = $"{remoteTempDir}/drivers/{sourceFolderName}";
                                await sshService.UploadDirectoryAsync(credentials, sourceDriverPath, remoteDestinationPath);
                                Log(ExHyperV.Properties.Resources.Log_HostDriverImportComplete);
                                await UploadLocalFilesAsync(sshService, credentials, $"{remoteTempDir}/lib");
                                Log(ExHyperV.Properties.Resources.Log_LocalLibrariesCheckComplete);

                                Log(ExHyperV.Properties.Resources.LinuxDeploy_Step4);

                                var commandsToExecute = new List<Tuple<string, TimeSpan?>>();
                                bool enableGraphics = credentials.InstallGraphics;

                                // 1. 下载脚本
                                var scriptsToDownload = new List<string>
                                {
                                    $"wget -O {remoteTempDir}/install_dxgkrnl.sh {ScriptBaseUrl}install_dxgkrnl.sh",
                                    enableGraphics ? $"wget -O {remoteTempDir}/setup_graphics.sh {ScriptBaseUrl}setup_graphics.sh" : null,
                                    $"wget -O {remoteTempDir}/configure_system.sh {ScriptBaseUrl}configure_system.sh"
                                }.Where(s => s != null).ToList();

                                foreach (var scriptCmd in scriptsToDownload)
                                {
                                    await sshService.ExecuteSingleCommandAsync(credentials, scriptCmd, Log, TimeSpan.FromMinutes(2));
                                }
                                await sshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteTempDir}/*.sh", Log, TimeSpan.FromSeconds(10));
                                Log(ExHyperV.Properties.Resources.Log_DxgkrnlModuleCompiling);
                                string dxgkrnlCommand = withSudo($"{remoteTempDir}/install_dxgkrnl.sh");
                                var dxgkrnlResult = await sshService.ExecuteCommandAndCaptureOutputAsync(credentials, dxgkrnlCommand, Log, TimeSpan.FromMinutes(60));
                                if (dxgkrnlResult.Output.Contains("STATUS: REBOOT_REQUIRED"))
                                {
                                    Log(ExHyperV.Properties.Resources.Log_KernelUpdateRebootRequired);
                                    Log(ExHyperV.Properties.Resources.Status_RebootingVm);

                                    try
                                    {
                                        await sshService.ExecuteSingleCommandAsync(credentials, withSudo("reboot"), Log, TimeSpan.FromSeconds(10));
                                    }
                                    catch (Exception) { }
                                    Log(ExHyperV.Properties.Resources.Log_WaitingForVmToComeOnline);
                                    bool isVmUp = await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, cancellationToken);
                                    if (!isVmUp) throw new Exception(ExHyperV.Properties.Resources.Error_VmDidNotComeBackOnline);

                                    Log(ExHyperV.Properties.Resources.Log_VmReconnectedRestartingDeploy);
                                    // 注意：这里移除了 continue 循环结构。
                                    // 实际场景下，如果需要重启后继续，调用方需要根据返回状态再次调用此方法，或者在这里递归调用。
                                    // 为保持代码简单，这里抛出特定异常或建议重试。
                                    return "REBOOT_REQUIRED_RETRY";
                                }

                                if (!dxgkrnlResult.Output.Contains("STATUS: SUCCESS"))
                                {
                                    throw new Exception(ExHyperV.Properties.Resources.Error_KernelModuleScriptFailed);
                                }

                                Log(ExHyperV.Properties.Resources.Log_KernelModuleInstallSuccess);

                                if (enableGraphics)
                                {
                                    Log(ExHyperV.Properties.Resources.Log_ConfiguringMesa);
                                    await sshService.ExecuteSingleCommandAsync(credentials, withSudo($"{remoteTempDir}/setup_graphics.sh"), Log, TimeSpan.FromMinutes(20));
                                }

                                Log(ExHyperV.Properties.Resources.Log_ConfiguringSystem);
                                string configArgs = enableGraphics ? "enable_graphics" : "no_graphics";
                                await sshService.ExecuteSingleCommandAsync(credentials, withSudo($"{remoteTempDir}/configure_system.sh {configArgs}"), Log, TimeSpan.FromMinutes(5));

                                Log(ExHyperV.Properties.Resources.LinuxDeploy_Step5);
                                try
                                {
                                    await sshService.ExecuteSingleCommandAsync(credentials, "sudo reboot", Log, TimeSpan.FromSeconds(5));
                                }
                                catch { }

                                return "OK";
                            }
                            catch (OperationCanceledException)
                            {
                                return ExHyperV.Properties.Resources.Info_OperationCancelled;
                            }
                            catch (Exception ex)
                            {
                                string errorMsg = string.Format(Properties.Resources.Error_DeploymentFailed, ex.Message);
                                Log(string.Format(Properties.Resources.Log_ErrorBlockHeader, errorMsg));
                                return errorMsg;
                            }
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
                        // 移除了UI弹窗：Utils.Show(Properties.Resources.Warning_Win10GpuAssignmentNotPersistent);
                        // 可以选择记录日志
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

                string harddiskpath = harddiskPathResult[0].ToString();

                // 3. 调用核心注入逻辑
                string result = await InjectWindowsDriversAsync(vmName, harddiskpath, selectedPartition, gpuManu, gpuInstancePath, progressCallback);

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