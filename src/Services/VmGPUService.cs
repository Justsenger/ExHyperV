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
    public class VmGPUService
    {
        #region 内部模型与常量
        private class VmDiskTarget
        {
            public bool IsPhysical { get; set; }
            public string Path { get; set; }        // 虚拟文件的VHDX 路径
            public int PhysicalDiskNumber { get; set; } // 物理硬盘的 Disk Number (e.g. 0, 1, 2)
        }
        private const string ScriptBaseUrl = "https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/";

        // PowerShell 脚本常量
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
        #endregion

        #region 环境与底层工具集
        private bool IsWindows11OrGreater() => Environment.OSVersion.Version.Build >= 22000;

        public Task PrepareHostEnvironmentAsync()
        {
            return Task.Run(() =>
            {
                Utils.AddGpuAssignmentStrategyReg();
                Utils.ApplyGpuPartitionStrictModeFix();
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
            throw new IOException(ExHyperV.Properties.Resources.Error_NoAvailableDriveLetters);
        }
        #endregion

        #region 硬件信息与虚拟机查询
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
                        string name = gpu.Members["name"]?.Value?.ToString();
                        string instanceId = gpu.Members["PNPDeviceID"]?.Value?.ToString();
                        string manu = gpu.Members["AdapterCompatibility"]?.Value?.ToString();
                        string driverVersion = gpu.Members["DriverVersion"]?.Value?.ToString();
                        if (name == null || instanceId == null || manu == null || driverVersion == null) continue;
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);
                        if (instanceId != null && !instanceId.ToUpper().StartsWith("PCI\\") && !instanceId.ToUpper().Contains("ACPI")) continue;
                        gpuList.Add(new GPUInfo(name, "True", manu, instanceId, null, null, driverVersion, vendor));
                    }
                }

                bool hasHyperV = Utils.Run(CheckHyperVModuleScript).Count > 0;
                if (!hasHyperV) return gpuList;

                var gpuram = Utils.Run(GetGpuRamScript);
                if (gpuram.Count > 0)
                {
                    foreach (var existingGpu in gpuList)
                    {
                        var matchedGpu = gpuram.FirstOrDefault(g =>
                        {
                            string rawId = g.Members["MatchingDeviceId"]?.Value?.ToString().ToUpper();
                            if (string.IsNullOrEmpty(rawId)) return false;
                            // 查找核心硬件 ID 字段，不依赖固定长度
                            return existingGpu.InstanceId.ToUpper().Contains(rawId);
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
                        var existingGpu = gpuList.FirstOrDefault(g => NormalizeDeviceId(g.InstanceId) == normalizedPNameId);
                        if (existingGpu != null) existingGpu.Pname = pname;
                    }
                }
                return gpuList;
            });
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

                                // 读取 Msvm_PartitionableGpu 的 "Name" 属性
                                // Name 属性包含 \\?\PCI#VEN... 
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

        public Task<(bool IsOff, string CurrentState)> IsVmPoweredOffAsync(string vmName)
        {
            return Task.Run(() =>
            {
                var result = Utils.Run($"(Get-VM -Name '{vmName}').State");
                string state = result != null && result.Count > 0 ? result[0].ToString() : "Unknown";
                bool isOff = state.Equals("Off", StringComparison.OrdinalIgnoreCase);
                return (isOff, state);
            });
        }

        public Task<bool> OptimizeVmForGpuAsync(string vmName)
        {
            return Task.Run(() =>
            {
                try
                {
                    string vmConfigScript = $@"
                Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}';
                Set-VM -HighMemoryMappedIoSpace 64GB -VMName '{vmName}';
                Set-VM -LowMemoryMappedIoSpace 1GB -VMName '{vmName}';
            ";
                    Utils.Run(vmConfigScript);
                    return true;
                }
                catch { return false; }
            });
        }
        #endregion

        #region 磁盘与分区操作
        private Task<List<VmDiskTarget>> GetAllVmHardDrivesAsync(string vmName)
        {
            return Task.Run(() =>
            {
                var script = $@"
            $drives = Get-VMHardDiskDrive -VMName '{vmName}' | Sort-Object ControllerNumber, ControllerLocation
            if ($drives -eq $null) {{ return @() }}
            
            $results = @()
            foreach ($drive in $drives) {{
                if ($drive.DiskNumber -ne $null) {{
                    $results += 'PHYSICAL:' + $drive.DiskNumber
                }} 
                elseif (-not [string]::IsNullOrWhiteSpace($drive.Path)) {{
                    $results += 'VHD:' + $drive.Path
                }}
            }}
            return $results";

                var result = Utils.Run(script);
                var list = new List<VmDiskTarget>();
                if (result == null) return list;

                foreach (var raw in result)
                {
                    string s = raw.ToString();
                    if (s.StartsWith("PHYSICAL:"))
                    {
                        if (int.TryParse(s.Substring(9), out int num))
                            list.Add(new VmDiskTarget { IsPhysical = true, PhysicalDiskNumber = num });
                    }
                    else if (s.StartsWith("VHD:"))
                    {
                        list.Add(new VmDiskTarget { IsPhysical = false, Path = s.Substring(4) });
                    }
                }
                return list;
            });
        }

        public async Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                var allPartitions = new List<PartitionInfo>();
                var diskTargetsTask = GetAllVmHardDrivesAsync(vmName);
                diskTargetsTask.Wait();
                var diskTargets = diskTargetsTask.Result;

                foreach (var target in diskTargets)
                {
                    int hostDiskNumber = -1;
                    try
                    {
                        if (target.IsPhysical)
                        {
                            var setupScript = $@"
                        Set-Disk -Number {target.PhysicalDiskNumber} -IsOffline $false -ErrorAction SilentlyContinue
                        Set-Disk -Number {target.PhysicalDiskNumber} -IsReadOnly $true -ErrorAction SilentlyContinue
                        Start-Sleep -Milliseconds 500
                        (Get-Disk -Number {target.PhysicalDiskNumber}).Number";
                            var res = Utils.Run(setupScript);
                            if (res != null && res.Count > 0) hostDiskNumber = target.PhysicalDiskNumber;
                        }
                        else
                        {
                            var mountScript = $@"
                        $path = '{target.Path}'
                        Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
                        $img = Mount-DiskImage -ImagePath $path -NoDriveLetter -PassThru -ErrorAction Stop
                        ($img | Get-Disk).Number";
                            var mountResult = Utils.Run(mountScript);
                            if (mountResult != null && mountResult.Count > 0)
                                int.TryParse(mountResult[0].ToString(), out hostDiskNumber);
                        }

                        if (hostDiskNumber != -1)
                        {
                            var diskParser = new DiskParserService();
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
                            Utils.Run($"Set-Disk -Number {target.PhysicalDiskNumber} -IsReadOnly $false -ErrorAction SilentlyContinue");
                            Utils.Run($"Set-Disk -Number {target.PhysicalDiskNumber} -IsOffline $true -ErrorAction SilentlyContinue");
                        }
                        else if (!string.IsNullOrEmpty(target.Path))
                        {
                            Utils.Run($"Dismount-DiskImage -ImagePath '{target.Path}' -ErrorAction SilentlyContinue");
                        }
                    }
                }
                return allPartitions;
            });
        }
        #endregion

        #region GPU 分配与解绑
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

                    string addGpuCommand = isWin10
                        ? $"Add-VMGpuPartitionAdapter -VMName '{vmName}'"
                        : $"Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'";

                    Utils.Run(addGpuCommand);

                    var verifyResult = Utils.Run($"Get-VMGpuPartitionAdapter -VMName '{vmName}'");
                    if (verifyResult == null || verifyResult.Count == 0)
                    {
                        return (false, Properties.Resources.Error_Gpu_NoPartition);
                    }

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
                catch (Exception ex) { return (false, ex.Message); }
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
                        if (lastSeparatorIndex != -1) normalizedId = normalizedId.Substring(0, lastSeparatorIndex);
                    }
                    return normalizedId;
                }

                void Log(string message) => progressCallback?.Invoke(message);

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
                        Set-VM -HighMemoryMappedIoSpace 64GB -VMName '{vmName}';
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
                            Log(Properties.Resources.Msg_Gpu_PreparingDisk);
                            var diskTarget = new VmDiskTarget
                            {
                                IsPhysical = selectedPartition.IsPhysicalDisk,
                                Path = selectedPartition.IsPhysicalDisk ? null : selectedPartition.DiskPath,
                                PhysicalDiskNumber = selectedPartition.IsPhysicalDisk ? int.Parse(selectedPartition.DiskPath) : 0
                            };

                            string injectionResult = await InjectWindowsDriversAsync(vmName, diskTarget, selectedPartition, gpuManu, id, Log);

                            if (injectionResult != "OK") return injectionResult;
                        }
                        else if (selectedPartition.OsType == OperatingSystemType.Linux)
                        {
                            return "OK";
                        }
                    }

                    if (isWin10 && partitionableGpuCount > 1) Utils.Run($"Start-VM -Name '{vmName}'");
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

            void Log(string msg) => progressCallback?.Invoke(msg);

            try
            {
                if (isPhysical)
                {
                    Log(string.Format(Properties.Resources.Msg_Gpu_DismountingDisk, diskTarget.PhysicalDiskNumber));
                    hostDiskNumber = diskTarget.PhysicalDiskNumber;
                    var detachScript = $@"
        $ErrorActionPreference = 'Stop'
        $vmDisk = Get-VMHardDiskDrive -VMName '{vmName}' | Where-Object {{ $_.DiskNumber -eq {hostDiskNumber} }}
        if ($vmDisk) {{
            $out = ""$($vmDisk.ControllerType),$($vmDisk.ControllerNumber),$($vmDisk.ControllerLocation)""
            Remove-VMHardDiskDrive -VMHardDiskDrive $vmDisk -ErrorAction Stop
            $out
        }} else {{ throw 'DiskNotFoundInVm' }}";

                    var detachRes = Utils.Run(detachScript);
                    if (detachRes == null || detachRes.Count == 0) return Properties.Resources.Error_Gpu_DiskNotFound;

                    var parts = detachRes[0].ToString().Split(',');
                    savedCtrlType = parts[0];
                    savedCtrlNum = int.Parse(parts[1]);
                    savedCtrlLoc = int.Parse(parts[2]);

                    Utils.Run($@"Set-Disk -Number {hostDiskNumber} -IsOffline $false -ErrorAction Stop");
                    Utils.Run($@"Set-Disk -Number {hostDiskNumber} -IsReadOnly $false -ErrorAction Stop");
                    Utils.Run("Update-HostStorageCache");
                }
                else
                {
                    Log(string.Format(Properties.Resources.Msg_Gpu_MountingVhd, Path.GetFileName(diskTarget.Path)));
                    var mountRes = Utils.Run($@"
        Dismount-DiskImage -ImagePath '{diskTarget.Path}' -ErrorAction SilentlyContinue
        $img = Mount-DiskImage -ImagePath '{diskTarget.Path}' -NoDriveLetter -PassThru -ErrorAction Stop
        ($img | Get-Disk).Number");

                    if (mountRes == null || !int.TryParse(mountRes[0].ToString(), out hostDiskNumber))
                        return Properties.Resources.Error_Gpu_MountVhdFailed;
                }

                Log(string.Format(Properties.Resources.Msg_Gpu_AssignTempDrive, hostDiskNumber, partition.PartitionNumber));

                char suggestedLetter = GetFreeDriveLetter();
                var assignRes = Utils.Run($@"
$p = Get-Partition -DiskNumber {hostDiskNumber} | Where-Object PartitionNumber -eq {partition.PartitionNumber}
Set-Partition -InputObject $p -NewDriveLetter '{suggestedLetter}' -ErrorAction Stop
'{suggestedLetter}'");

                assignedDriveLetter = assignRes[0].ToString().TrimEnd(':') + ":";

                var checkStatus = Utils.Run($@"
$drive = '{assignedDriveLetter[0]}'
$v = Get-BitLockerVolume -MountPoint ""$($drive):"" -ErrorAction SilentlyContinue
$gV = Get-Volume -DriveLetter $drive -ErrorAction SilentlyContinue

$isBL = $v -ne $null
$fs = if ($gV) {{ $gV.FileSystem }} else {{ '' }}
$prot = if ($v) {{ [string]$v.ProtectionStatus }} else {{ '' }}

if ($isBL -and ([string]::IsNullOrWhiteSpace($fs) -or $prot -eq 'Unknown')) {{ return 'LOCKED' }}
return 'OK'
");

                if (checkStatus != null && checkStatus.Count > 0 && checkStatus[0].ToString() == "LOCKED")
                {
                    return Properties.Resources.Error_Gpu_BitLocker;
                }

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

                PromoteRegistryDefinedFiles(assignedDriveLetter); // 微软注册表文件提取

                if (gpuManu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    Log(Properties.Resources.Msg_Gpu_InjectingReg);
                    NvidiaReg(assignedDriveLetter);
                    PromoteNvidiaFiles(assignedDriveLetter);
                }
                else if (gpuManu.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    PromoteIntelGpuFiles(assignedDriveLetter);
                }
                else if (gpuManu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuManu.Contains("Advanced", StringComparison.OrdinalIgnoreCase))
                {
                    PromoteAmdGpuFiles(assignedDriveLetter);
                }


                return "OK";
            }
            catch (Exception ex) { return string.Format(Properties.Resources.Error_Gpu_InjectFailed, ex.Message); }
            finally
            {
                if (isPhysical && hostDiskNumber != -1)
                {
                    Log(Properties.Resources.Msg_Gpu_Remounting);
                    try
                    {
                        Utils.Run($@"
            Get-Partition -DiskNumber {hostDiskNumber} | Where-Object DriveLetter -ne $null | ForEach-Object {{
                Remove-PartitionAccessPath -DiskNumber $_.DiskNumber -PartitionNumber $_.PartitionNumber -AccessPath ""$($_.DriveLetter):"" -ErrorAction SilentlyContinue
            }}");
                        var offlineScript = $@"
            $n = {hostDiskNumber}
            Set-Disk -Number $n -IsOffline $true -ErrorAction Stop
            for($i=0; $i -lt 10; $i++) {{
                if ((Get-Disk -Number $n).IsOffline) {{ return 'OK' }}
                Start-Sleep -Milliseconds 500
            }}
            throw '磁盘脱机超时'";

                        Utils.Run(offlineScript);
                        Thread.Sleep(1000);

                        var reattachScript = $@"
            Add-VMHardDiskDrive -VMName '{vmName}' `
                                -ControllerType '{savedCtrlType}' `
                                -ControllerNumber {savedCtrlNum} `
                                -ControllerLocation {savedCtrlLoc} `
                                -DiskNumber {hostDiskNumber} `
                                -ErrorAction Stop";
                        Utils.Run(reattachScript);
                        Log(Properties.Resources.Msg_Gpu_RemountSuccess);
                    }
                    catch (Exception ex) { Log(string.Format(Properties.Resources.Error_Gpu_RemountFailed, ex.Message)); }
                }
                else if (!string.IsNullOrEmpty(diskTarget?.Path))
                {
                    Log(Properties.Resources.Msg_Gpu_Unmounting);
                    Utils.Run($"Dismount-DiskImage -ImagePath '{diskTarget.Path}' -ErrorAction SilentlyContinue");
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
                        // --- 管理与基础 ---
                        LinkSingleFile(assignedDriveLetter, "nvidia-smi.exe", "nvidia-smi.exe", "System32");
                        LinkSingleFile(assignedDriveLetter, "nvml_loader.dll", "nvml.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "nvapi64.dll", "nvapi64.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "nvcpl.dll", "nvcpl.dll", "System32");

                        // --- 计算接口 (CUDA / OpenCL) ---
                        LinkSingleFile(assignedDriveLetter, "nvcuda_loader64.dll", "nvcuda.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "nvcudadebugger.dll", "nvcudadebugger.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "OpenCL64.dll", "OpenCL.dll", "System32");

                        // --- 视频与光流 (NVOF / NVENC / NVDEC) ---
                        LinkSingleFile(assignedDriveLetter, "nvofapi64.dll", "nvofapi64.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "nvEncodeAPI64.dll", "nvEncodeAPI64.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "nvcuvid64.dll", "nvcuvid.dll", "System32");

                        // --- 图形增强 (Vulkan / DLSS / FBC) ---
                        LinkSingleFile(assignedDriveLetter, "vulkan-1-x64.dll", "vulkan-1.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "vulkan-1-x64.dll", "vulkan-1-999-0-0-0.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "vulkaninfo-x64.exe", "vulkaninfo.exe", "System32");
                        LinkSingleFile(assignedDriveLetter, "_nvngx.dll", "_nvngx.dll", "System32"); // DLSS 核心
                        LinkSingleFile(assignedDriveLetter, "NvFBC64.dll", "NvFBC64.dll", "System32");
                        LinkSingleFile(assignedDriveLetter, "NvIFR64.dll", "NvIFR64.dll", "System32");
            
        }

        private void PromoteIntelGpuFiles(string assignedDriveLetter)
        {
            // 1. Vulkan 核心 (必须重命名，去掉 -64)
            LinkSingleFile(assignedDriveLetter, "vulkan-1-64.dll", "vulkan-1.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "vulkan-1-64.dll", "vulkan-1-999-0-0-0.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-64.exe", "vulkaninfo.exe", "System32");

            // 2. 视频加速核心 (Intel Media SDK / VPL)
            LinkSingleFile(assignedDriveLetter, "mfx_loader_dll_hw64.dll", "libmfxhw64.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "vpl_dispatcher_64.dll", "libvpl.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "mfxplugin64_hw.dll", "mfxplugin64_hw.dll", "System32");

            // 3. 计算接口 (OneAPI / Level Zero)
            LinkSingleFile(assignedDriveLetter, "ze_loader.dll", "ze_loader.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "ze_intel_gpu_raytracing.dll", "ze_intel_gpu_raytracing.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "ze_tracing_layer.dll", "ze_tracing_layer.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "ze_validation_layer.dll", "ze_validation_layer.dll", "System32");

            // 4. Intel 显卡专用 API 接口
            LinkSingleFile(assignedDriveLetter, "intel_gfx_api-x64.dll", "intel_gfx_api-x64.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "ControlLib.dll", "ControlLib.dll", "System32");
        }

        private void PromoteAmdGpuFiles(string assignedDriveLetter)
        {
            // --- 1. 基础渲染与计算核心 (AMD 通用) ---
            // 这些是 3D 应用和视频解码必须的
            LinkSingleFile(assignedDriveLetter, "atidxx64.dll", "atidxx64.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "atig6txx.dll", "atig6txx.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "atig6dev.dll", "atig6dev.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "amdxx64.dll", "amdxx64.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "amdihk64.dll", "amdihk64.dll", "System32");

            // --- 2. 扫描出的匹配项 (System32 根目录) ---
            LinkSingleFile(assignedDriveLetter, "atimuixx.dll", "atimuixx.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "atisamu64.dll", "atisamu64.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", "System32");
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", "System32");
            LinkSingleFile(assignedDriveLetter, "EEURestart.exe", "EEURestart.exe", "System32");
            LinkSingleFile(assignedDriveLetter, "GameManager64.dll", "GameManager64.dll", "System32");

            // --- 3. 特殊重命名项 ---
            LinkSingleFile(assignedDriveLetter, "detoured64.dll", "detoured.dll", "System32");

            // --- 4. AMD 专用子目录组件 (amdkmpfd) ---
            string amdSubDir = Path.Combine("System32", "AMD", "amdkmpfd");
            LinkSingleFile(assignedDriveLetter, "amdkmpfd.ctz", "amdkmpfd.ctz", amdSubDir);
            LinkSingleFile(assignedDriveLetter, "amdkmpfd.itz", "amdkmpfd.itz", amdSubDir);
            LinkSingleFile(assignedDriveLetter, "amdkmpfd.stz", "amdkmpfd.stz", amdSubDir);

            // --- 5. Vulkan 支持 ---
            LinkSingleFile(assignedDriveLetter, "amdvlk64.dll", "amdvlk64.dll", "System32");
            LinkSingleFile(assignedDriveLetter, "amdvlk64.dll", "vulkan-1.dll", "System32");
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

                // 如果目标子目录不存在（例如 System32\AMD\amdkmpfd），先创建它
                if (!Directory.Exists(hostDestDir))
                {
                    Directory.CreateDirectory(hostDestDir);
                }

                var foundFiles = new DirectoryInfo(guestRepo)
                                    .GetFiles(sourceName, SearchOption.AllDirectories)
                                    .OrderByDescending(f => f.LastWriteTime)
                                    .ToList();

                if (foundFiles.Count == 0) return;

                string hostSourceFile = foundFiles[0].FullName;
                // 注意：mklink 需要虚拟机内部的绝对路径，即 C:\Windows\...
                string guestInternalTarget = hostSourceFile.Replace(assignedDriveLetter, "C:");
                string hostLinkPath = Path.Combine(hostDestDir, targetName);

                if (File.Exists(hostLinkPath)) File.Delete(hostLinkPath);

                // 执行 mklink
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
        #endregion

        #region Linux 驱动环境与脚本部署
        private string FindGpuDriverSourcePath(string gpuInstancePath)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
            if (Directory.Exists(path)) return path;
            return @"C:\Windows\System32\DriverStore\FileRepository";
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
                    // 【修改点】：添加“本地”标识
                    item.Name = $"[本地] {item.Name}";
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
                        // 【修改点】：添加“在线”标识
                        item.Name = $"[在线] {item.Name}";
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
                var sshService = new SshService();

                try
                {
                    // --- 阶段 1: 准备工作 (IP 嗅探与连接) ---
                    var currentState = await GetVmStateAsync(vmName);
                    if (currentState != "Running")
                    {
                        Log("[ExHyperV] Starting VM...");
                        Utils.Run($"Start-VM -Name '{vmName}'");
                        await Task.Delay(5000);
                    }

                    Log(Properties.Resources.Msg_Gpu_LinuxWaitingIp);
                    string getMacScript = $"(Get-VMNetworkAdapter -VMName '{vmName}').MacAddress | Select-Object -First 1";
                    var macResult = await Utils.Run2(getMacScript);
                    if (macResult == null || macResult.Count == 0) return "Failed to get VM MAC Address";

                    string macAddress = Regex.Replace(macResult[0].ToString(), "(.{2})", "$1:").TrimEnd(':');
                    string vmIpAddress = await Utils.GetVmIpAddressAsync(vmName, macAddress);
                    string targetIp = Utils.SelectBestIpv4Address(!string.IsNullOrWhiteSpace(credentials.Host) ? credentials.Host : vmIpAddress);

                    if (string.IsNullOrEmpty(targetIp)) return "No valid IPv4 address found.";
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

                    Log("Uploading Driver and WSL Libraries...");
                    string sourceDriverPath = FindGpuDriverSourcePath(string.Empty);
                    await sshService.UploadDirectoryAsync(credentials, sourceDriverPath, $"{remoteTempDir}/drivers");
                    await UploadLocalFilesAsync(sshService, credentials, $"{remoteTempDir}/lib");

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
                        Log($"Uploading local script: {script.Name}");
                        await sshService.UploadFileAsync(credentials, script.SourcePathOrUrl, remoteScriptPath);
                    }
                    else
                    {
                        Log($"Downloading remote script inside VM: {script.Name}");
                        // 使用 sh -c 包裹，确保环境变量对后面的命令生效
                        string downloadCmd = $"{proxyEnv}sh -c \"wget -q -O {remoteScriptPath} {script.SourcePathOrUrl} || curl -fL {script.SourcePathOrUrl} -o {remoteScriptPath}\"";

                        await sshService.ExecuteSingleCommandAsync(credentials, downloadCmd, Log);
                    }
                    await sshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteScriptPath}", Log);
                    // --- 阶段 4: 状态机执行循环 ---
                    bool isSuccess = false;
                    int maxAttempts = 3;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (ct.IsCancellationRequested) return "Cancelled";

                        bool rebootNeeded = false;
                        string graphicsArg = credentials.InstallGraphics ? "true" : "false";
                        string proxyArg = credentials.UseProxy ? $"\"http://{credentials.ProxyHost}:{credentials.ProxyPort}\"" : "\"\"";

                        // 使用 sudo -E 保证代理变量能传递给 apt
                        string execCmd = $"echo '{credentials.Password.Replace("'", "'\\''")}' | sudo -S -E -p '' bash {remoteScriptPath} deploy {graphicsArg} {proxyArg}";

                        Log($"[Attempt {attempt}] Executing script...");

                        await sshService.ExecuteCommandAndCaptureOutputAsync(credentials, execCmd, line =>
                        {
                            Log(line);
                            if (line.Contains("[STATUS: SUCCESS]")) isSuccess = true;
                            if (line.Contains("[STATUS: REBOOT_REQUIRED]")) rebootNeeded = true;
                        }, TimeSpan.FromMinutes(60));

                        if (isSuccess) break;

                        if (rebootNeeded)
                        {
                            Log("!!! VM Reboot required. Restarting now...");
                            Utils.Run($"Restart-VM -Name '{vmName}' -Force");
                            await Task.Delay(10000); // 等待开始关机
                            if (!await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, ct))
                                return "VM failed to come back online after reboot.";

                            Log("VM is back online. Resuming deployment...");
                            continue; // 重新进入循环执行同一脚本
                        }

                        // 如果既没成功也没重启信号，通常是脚本内部报错 set -e 触发了
                        if (!isSuccess) return "Script execution failed (no success signal).";
                    }

                    return isSuccess ? "OK" : "Maximum reboot attempts reached.";

                }
                catch (Exception ex) { return $"Error: {ex.Message}"; }
            });
        }
        #endregion
    }
}