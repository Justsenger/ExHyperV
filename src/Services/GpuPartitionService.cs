using System.Diagnostics;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;

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
                        if (vendor == "Unknown") { continue; }
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
                        var existingGpu = gpuList.FirstOrDefault(g => pname.ToUpper().Contains(g.InstanceId.Replace("\\", "#")));
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

        public Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu)
        {
            return Task.Run(() =>
            {
                string harddiskpath = null;
                bool isVhdMounted = false;
                try
                {
                    var vmStateResult = Utils.Run($"(Hyper-V\\Get-VM -Name '{vmName}').State");
                    if (vmStateResult == null || vmStateResult.Count == 0) return string.Format(Properties.Resources.GetVmState_Error, vmName);
                    if (vmStateResult[0].ToString() != "Off") return ExHyperV.Properties.Resources.Running;

                    string vmConfigScript = $@"
                        Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}'
                        Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmName}'
                        Set-VM -LowMemoryMappedIoSpace 1GB -VMName '{vmName}'
                        Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'
                    ";
                    Utils.Run(vmConfigScript);

                    var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                    if (harddiskPathResult == null || harddiskPathResult.Count == 0) return string.Format(Properties.Resources.Error_CannotGetVmHardDiskPath, vmName);
                    harddiskpath = harddiskPathResult[0].ToString();

                    var mountResult = Utils.Run2($"Mount-VHD -Path '{harddiskpath}' -PassThru");
                    if (mountResult == null) return string.Format(Properties.Resources.Error_FailedToMountHardDisk, harddiskpath);
                    isVhdMounted = true;

                    var findSystemDriveScript = @$"
                            $harddiskpath = '{harddiskpath}';
                            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew();
                            while ($stopwatch.Elapsed.TotalSeconds -lt 5) {{
                                try {{
                                    (Get-VHD -Path $harddiskpath) | Get-Disk | Get-Partition | Where-Object {{ $_.Type -ne 'Recovery' -and $_.Type -ne 'System' -and -not $_.DriveLetter }} | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue | Out-Null;
                                }} catch {{}}

                                $volumes = (Get-VHD -Path $harddiskpath) | Get-Disk | Get-Partition | Get-Volume;
                                foreach ($volume in $volumes) {{
                                    if ($volume.DriveLetter -and (Test-Path ""$($volume.DriveLetter):\Windows\System32\config\SYSTEM"")) {{
                                        Write-Output $volume.DriveLetter;
                                        return;
                                    }}
                                }}
                                Start-Sleep -Milliseconds 500;
                            }}
                        ";
                    var letterResult = Utils.Run(findSystemDriveScript);
                    if (letterResult == null || letterResult.Count == 0) return string.Format(Properties.Resources.Error_FailedToFindSystemPartition, harddiskpath);
                    string letter = letterResult[0].ToString();

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
                    robocopyProcess.WaitForExit();

                    if (robocopyProcess.ExitCode >= 8) return string.Format(Properties.Resources.Error_RobocopyDriverCopyFailed, robocopyProcess.ExitCode);

                    SetFolderReadOnly(destinationFolder);

                    if (gpuManu.Contains("NVIDIA"))
                    {
                        string regResult = NvidiaReg(letter + ":");
                        if (regResult != "OK") return regResult;
                    }

                    return "OK";
                }
                catch (Exception ex)
                {
                    return string.Format(Properties.Resources.Error_UnexpectedSystemException, ex.Message);
                }
                finally
                {
                    if (isVhdMounted && !string.IsNullOrEmpty(harddiskpath))
                    {
                        Utils.Run($"Dismount-VHD -Path '{harddiskpath}' -ErrorAction SilentlyContinue");
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
            // 为NVIDIA显卡注入注册表信息，模拟DDA的驱动注入过程。
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