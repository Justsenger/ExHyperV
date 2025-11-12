using System.Diagnostics;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;
using System.Text.Json;

namespace ExHyperV.Services
{
    public class GpuPartitionService : IGpuPartitionService
    {

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
        private const string GetPartitionableGpusScript = "Get-VMHostPartitionableGpu | select name";
        private const string CheckHyperVModuleScript = "Get-Module -ListAvailable -Name Hyper-V";
        private const string GetVmsScript = "Hyper-V\\Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace";

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
            throw new IOException("没有可用的盘符");
        }
        public Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName)
        {
            return Task.Run(() =>
            {
                string harddiskpath = null;
                int? diskNumber = null;

                try
                {
                    var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                    if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                    {
                        throw new FileNotFoundException($"无法找到虚拟机 '{vmName}' 的虚拟硬盘文件。");
                    }
                    harddiskpath = harddiskPathResult[0].ToString();

                    var mountScript = $@"
                $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -NoDriveLetter -PassThru;
                ($diskImage | Get-Disk).Number;
            ";
                    var mountResult = Utils.Run(mountScript);

                    if (mountResult == null || mountResult.Count == 0 || !int.TryParse(mountResult[0].ToString(), out int num))
                    {
                        throw new InvalidOperationException("挂载虚拟磁盘或获取其磁盘编号失败。");
                    }
                    diskNumber = num;
                    string devicePath = $@"\\.\PhysicalDrive{diskNumber}";
                    var diskParser = new DiskParserService();
                    List<PartitionInfo> initialPartitions = diskParser.GetPartitions(devicePath);
                    return initialPartitions;
                }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException("需要管理员权限才能读取磁盘分区信息，请以管理员身份重新启动应用程序。");
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
        private string NormalizeDeviceId(string deviceId)
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
                        if (instanceId != null && !instanceId.ToUpper().StartsWith("PCI\\")){continue; }
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

        public Task<List<VMInfo>> GetVirtualMachinesAsync()
        {
            return Task.Run(() =>
            {
                var vmList = new List<VMInfo>();
                var vms = Utils.Run(GetVmsScript);
                if (vms.Count > 0)
                {
                    foreach (var vm in vms)
                    {
                        var gpulist = new Dictionary<string, string>();
                        string vmname = vm.Members["VMName"]?.Value?.ToString() ?? string.Empty;
                        string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value?.ToString() ?? string.Empty;
                        string guest = vm.Members["GuestControlledCacheTypes"]?.Value?.ToString() ?? string.Empty;

                        var vmgpus = Utils.Run($@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                        if (vmgpus.Count > 0)
                        {
                            foreach (var gpu in vmgpus)
                            {
                                string gpupath = gpu.Members["InstancePath"]?.Value?.ToString() ?? string.Empty;
                                string gpuid = gpu.Members["Id"]?.Value?.ToString() ?? string.Empty;
                                gpulist[gpuid] = gpupath;
                            }
                        }
                        vmList.Add(new VMInfo(vmname, null, highmmio, guest, gpulist));
                    }
                }
                return vmList;
            });
        }
        private bool IsWindows11OrGreater() => Environment.OSVersion.Version.Build >= 22000;
        private async Task<string> InjectWindowsDriversAsync(string vmName, string harddiskpath, PartitionInfo partition, string gpuManu)
        {
            string assignedDriveLetter = null;

            try
            {
                char suggestedLetter = GetFreeDriveLetter();

                var script = $@"
            $diskImage = Mount-DiskImage -ImagePath '{harddiskpath}' -PassThru | Get-Disk;
            $partitionToMount = Get-Partition -DiskNumber $diskImage.Number | Where-Object {{ $_.PartitionNumber -eq {partition.PartitionNumber} }};
            
            try {{
                $partitionToMount | Set-Partition -NewDriveLetter '{suggestedLetter}' -ErrorAction Stop;
            }} catch {{
            }}
            
            (Get-Partition -DiskNumber $diskImage.Number | Where-Object {{ $_.PartitionNumber -eq {partition.PartitionNumber} }}).DriveLetter
        ";

                var letterResult = Utils.Run(script);

                if (letterResult == null || letterResult.Count == 0 || string.IsNullOrEmpty(letterResult[0].ToString()))
                {
                    return string.Format(Properties.Resources.Error_FailedToFindSystemPartition, harddiskpath);
                }

                string actualLetter = letterResult[0].ToString();
                assignedDriveLetter = $"{actualLetter}:";

                string system32Path = Path.Combine(assignedDriveLetter, "Windows", "System32");
                if (!Directory.Exists(system32Path))
                {
                    return string.Format(
                        "所选分区似乎不是一个有效的Windows系统分区。",
                        partition.PartitionNumber,
                        assignedDriveLetter,
                        system32Path
                    );

                }

                string letter = assignedDriveLetter.TrimEnd(':');
                string sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
                string destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

                if (Directory.Exists(destinationFolder))
                {
                    try { RemoveReadOnlyAttribute(destinationFolder); }
                    catch (Exception ex) { return string.Format(Properties.Resources.Error_RemoveOldDriverFolderReadOnlyFailed, ex.Message); }
                }
                else
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                Process robocopyProcess = new()
                {
                    StartInfo = {
                FileName = "robocopy",
                Arguments = $"\"{sourceFolder}\" \"{destinationFolder}\" /E /R:2 /W:5 /NP /NJH /NFL /NDL",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
                };
                robocopyProcess.Start();
                await robocopyProcess.WaitForExitAsync();

                if (robocopyProcess.ExitCode >= 8)
                {
                    return string.Format(Properties.Resources.Error_RobocopyDriverCopyFailed, robocopyProcess.ExitCode);
                }

                SetFolderReadOnly(destinationFolder);

                if (gpuManu.Contains("NVIDIA"))
                {
                    string regResult = NvidiaReg(letter + ":");
                    if (regResult != "OK")
                    {
                        return regResult;
                    }
                }

                return "OK";
            }
            finally
            {
                if (!string.IsNullOrEmpty(harddiskpath))
                {
                    var cleanupScript = $@"
                $diskImage = Get-DiskImage -ImagePath '{harddiskpath}';
                if ($diskImage -and $diskImage.Attached) {{
                    if ('{assignedDriveLetter}') {{
                         try {{
                            Remove-PartitionAccessPath -DiskNumber $diskImage.Number -PartitionNumber {partition.PartitionNumber} -AccessPath '{assignedDriveLetter}\' -ErrorAction Stop;
                         }} catch {{}}
                    }}
                    Dismount-DiskImage -ImagePath '{harddiskpath}';
                }}
            ";
                    Utils.Run(cleanupScript);
                }
            }
        }
        public Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu, PartitionInfo selectedPartition)
        {
            return Task.Run(async () =>
            {
                var disabledGpuInstanceIds = new List<string>();

                try
                {
                    Utils.AddGpuAssignmentStrategyReg();
                    var vmStateResult = Utils.Run($"(Hyper-V\\Get-VM -Name '{vmName}').State");
                    if (vmStateResult == null || vmStateResult.Count == 0)
                    {
                        return string.Format(Properties.Resources.GetVmState_Error, vmName);
                    }
                    if (vmStateResult[0].ToString() != "Off")
                    {
                        return ExHyperV.Properties.Resources.Running;
                    }

                    if (!IsWindows11OrGreater())
                    {
                        var allHostGpus = await GetHostGpusAsync();
                        foreach (var gpu in allHostGpus)
                        {
                            if (gpu.InstanceId != gpuInstancePath && gpu.InstanceId.ToUpper().StartsWith("PCI\\"))
                            {
                                Utils.Run($"Disable-PnpDevice -InstanceId '{gpu.InstanceId}' -Confirm:$false");
                                disabledGpuInstanceIds.Add(gpu.InstanceId);
                            }
                        }
                        await Task.Delay(3000);
                    }

                    string addGpuCommand = IsWindows11OrGreater()
                        ? $"Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'"
                        : $"Add-VMGpuPartitionAdapter -VMName '{vmName}'";

                    string vmConfigScript = $@"
                Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}'
                Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmName}'
                Set-VM -LowMemoryMappedIoSpace 1GB -VMName '{vmName}'
                {addGpuCommand}
            ";
                    Utils.Run(vmConfigScript);

                    var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                    if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                    {
                        return string.Format(Properties.Resources.Error_CannotGetVmHardDiskPath, vmName);
                    }
                    string harddiskpath = harddiskPathResult[0].ToString();

                    if (selectedPartition.OsType == OperatingSystemType.Windows)
                    {
                        return await InjectWindowsDriversAsync(vmName, harddiskpath, selectedPartition, gpuManu);
                    }
                    else if (selectedPartition.OsType == OperatingSystemType.Linux)
                    {
                        return "SSH_REQUIRED";
                    }
                    else
                    {
                        return $"错误：不支持为所选分区类型（{selectedPartition.TypeDescription}）注入驱动程序。";
                    }
                }
                catch (Exception ex)
                {
                    return string.Format(Properties.Resources.Error_UnexpectedSystemException, ex.Message);
                }
                finally
                {
                    if (disabledGpuInstanceIds.Any())
                    {
                        foreach (var instanceId in disabledGpuInstanceIds)
                        {
                            Utils.Run($"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false");
                        }
                    }
                }
            });
        }
        public Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId)
        {
            return Task.Run(() =>
            {
                var results = Utils.Run2($@"Remove-VMGpuPartitionAdapter -VMName '{vmName}' -AdapterId '{adapterId}' -Confirm:$false");
                return results != null;
            });
        }

        private string NvidiaReg(string letter)
        {
            // 为NVIDIA显卡注入注册表信息。
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
    }
}