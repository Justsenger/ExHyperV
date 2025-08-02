// /Services/GpuPartitionService.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class GpuPartitionService : IGpuPartitionService
    {
        public Task<List<GPUInfo>> GetHostGpusAsync()
        {
            return Task.Run(() =>
            {
                var pciInfoProvider = new PciInfoProvider();
                pciInfoProvider.EnsureInitializedAsync().Wait();

                List<GPUInfo> gpuList = new List<GPUInfo>();

                var gpulinked = Utils.Run("Get-WmiObject -Class Win32_VideoController | select PNPDeviceID,name,AdapterCompatibility,DriverVersion");
                if (gpulinked.Count > 0)
                {
                    foreach (var gpu in gpulinked)
                    {
                        string name = gpu.Members["name"]?.Value.ToString();
                        string instanceId = gpu.Members["PNPDeviceID"]?.Value.ToString();
                        string Manu = gpu.Members["AdapterCompatibility"]?.Value.ToString();
                        string DriverVersion = gpu.Members["DriverVersion"]?.Value.ToString();
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);
                        if (vendor == "Unknown") { continue; }
                        gpuList.Add(new GPUInfo(name, "True", Manu, instanceId, null, null, DriverVersion, vendor));
                    }
                }

                bool hyperv = Utils.Run("Get-Module -ListAvailable -Name Hyper-V").Count > 0;
                if (!hyperv)
                {
                    return gpuList;
                }

                string script = $@"
Get-ItemProperty -Path ""HKLM:\SYSTEM\ControlSet001\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\0*"" -ErrorAction SilentlyContinue |
    Select-Object MatchingDeviceId,
          @{{Name='MemorySize'; Expression={{
              if ($_. ""HardwareInformation.qwMemorySize"") {{
                  $_.""HardwareInformation.qwMemorySize""
              }} 
              elseif ($_. ""HardwareInformation.MemorySize"" -and $_.""HardwareInformation.MemorySize"" -isnot [byte[]]) {{
                  $_.""HardwareInformation.MemorySize""
              }}
              else {{
                  $null
              }}
          }}}} |
    Where-Object {{ $_.MemorySize -ne $null -and $_.MemorySize -gt 0 }}
";
                var gpuram = Utils.Run(script);
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

                var result3 = Utils.Run("Get-VMHostPartitionableGpu | select name");
                if (result3.Count > 0)
                {
                    foreach (var gpu in result3)
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
                List<VMInfo> vmList = new List<VMInfo>();
                var vms = Utils.Run("Get-VM | Select vmname,LowMemoryMappedIoSpace,GuestControlledCacheTypes,HighMemoryMappedIoSpace");

                if (vms.Count > 0)
                {
                    foreach (var vm in vms)
                    {
                        Dictionary<string, string> gpulist = new Dictionary<string, string>();
                        string vmname = vm.Members["VMName"]?.Value.ToString();
                        string highmmio = vm.Members["HighMemoryMappedIoSpace"]?.Value.ToString();
                        string guest = vm.Members["GuestControlledCacheTypes"]?.Value.ToString();

                        var vmgpus = Utils.Run($@"Get-VMGpuPartitionAdapter -VMName '{vmname}' | Select InstancePath,Id");
                        if (vmgpus.Count > 0)
                        {
                            foreach (var gpu in vmgpus)
                            {
                                string gpupath = gpu.Members["InstancePath"]?.Value.ToString();
                                string gpuid = gpu.Members["Id"]?.Value.ToString();
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
                try
                {
                    var vmStateResult = Utils.Run($"(Get-VM -Name '{vmName}').State");
                    if (vmStateResult == null || vmStateResult.Count == 0) return $"错误: 无法获取虚拟机 '{vmName}' 的状态。";
                    if (vmStateResult[0].ToString() != "Off")
                    {
                        return "running";
                    }

                    string vmConfigScript = $@"
                        Set-VM -GuestControlledCacheTypes $true -VMName '{vmName}'
                        Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmName}'
                        Set-VM -LowMemoryMappedIoSpace 128MB -VMName '{vmName}'
                        Add-VMGpuPartitionAdapter -VMName '{vmName}' -InstancePath '{gpuInstancePath}'
                        Set-VMGpuPartitionAdapter -VMName '{vmName}' -MinPartitionVRAM 80000000 -MaxPartitionVRAM 100000000 -OptimalPartitionVRAM 100000000 -MinPartitionEncode 80000000 -MaxPartitionEncode 100000000 -OptimalPartitionEncode 100000000 -MinPartitionDecode 80000000 -MaxPartitionDecode 100000000 -OptimalPartitionDecode 100000000 -MinPartitionCompute 80000000 -MaxPartitionCompute 100000000 -OptimalPartitionCompute 100000000
                    ";
                    if (Utils.Run(vmConfigScript) == null)
                    {
                        return "错误：GPU分区参数设定失败。";
                    }

                    var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmName}')[0].Path");
                    if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                    {
                        return $"错误: 无法获取虚拟机 '{vmName}' 的硬盘路径。";
                    }
                    harddiskpath = harddiskPathResult[0].ToString();

                    var mountScript = @$"
                        $regPath = ""HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer""; $regKey = ""NoDriveTypeAutoRun"";
                        $originalValue = Get-ItemProperty -Path $regPath -Name $regKey -ErrorAction SilentlyContinue;
                        try {{
                            if (-not (Test-Path $regPath)) {{ New-Item -Path $regPath -Force | Out-Null }};
                            Set-ItemProperty -Path $regPath -Name $regKey -Value 255 -Type DWord -Force;
                            $VHD = Mount-VHD -Path '{harddiskpath}' -PassThru -ErrorAction Stop;
                            Start-Sleep -Seconds 1;
                            $VHD | Get-Disk | Get-Partition | Where-Object {{ -not $_.DriveLetter }} | Add-PartitionAccessPath -AssignDriveLetter | Out-Null;
                            $volumes = $VHD | Get-Disk | Get-Partition | Get-Volume;
                            foreach ($volume in $volumes) {{
                                if ($volume.DriveLetter -and (Test-Path ""$($volume.DriveLetter):\Windows\System32\config\SYSTEM"")) {{
                                    Write-Output $volume.DriveLetter;
                                    break;
                                }}
                            }}
                        }} finally {{
                            if ($originalValue) {{ Set-ItemProperty -Path $regPath -Name $regKey -Value $originalValue.$regKey -Force; }}
                            else {{ Remove-ItemProperty -Path $regPath -Name $regKey -Force -ErrorAction SilentlyContinue; }}
                        }}";

                    var letterResult = Utils.Run(mountScript);
                    if (letterResult == null || letterResult.Count == 0)
                    {
                        Utils.Run($"Dismount-VHD -Path '{harddiskpath}' -ErrorAction SilentlyContinue");
                        return $"错误: 挂载硬盘 '{harddiskpath}' 或查找其中的系统分区失败。";
                    }
                    string letter = letterResult[0].ToString();

                    string sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
                    string destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

                    if (!Directory.Exists(destinationFolder)) { Directory.CreateDirectory(destinationFolder); }

                    var process = new Process
                    {
                        StartInfo = {
                            FileName = "robocopy",
                            Arguments = $"\"{sourceFolder}\" \"{destinationFolder}\" /MIR /NP /NJH /NFL /NDL",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    process.WaitForExit();

                    SetFolderReadOnly(destinationFolder);

                    if (gpuManu.Contains("NVIDIA"))
                    {
                        NvidiaReg(letter + ":");
                    }

                    return "OK";
                }
                catch (Exception ex)
                {
                    return $"错误: 发生意外的系统异常 - {ex.Message}";
                }
                finally
                {
                    if (!string.IsNullOrEmpty(harddiskpath))
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

        private void SetFolderReadOnly(string folderPath)
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(folderPath);
            directoryInfo.Attributes |= FileAttributes.ReadOnly;

            foreach (var subDir in directoryInfo.GetDirectories())
            {
                SetFolderReadOnly(subDir.FullName);
            }
            foreach (var file in directoryInfo.GetFiles())
            {
                file.Attributes |= FileAttributes.ReadOnly;
            }
        }

        private void NvidiaReg(string letter)
        {
            string localKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm";
            string tempRegFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvlddmkm.reg");
            Utils.Run($@"reg export ""{localKeyPath}"" ""{tempRegFile}"" /y");
            string systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";
            Utils.Run($@"reg load HKLM\OfflineSystem ""{systemHiveFile}""");
            string originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
            string targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
            string regContent = File.ReadAllText(tempRegFile);
            regContent = regContent.Replace(originalText, targetText);
            regContent = regContent.Replace("DriverStore", "HostDriverStore");
            File.WriteAllText(tempRegFile, regContent);
            Utils.Run($@"reg import ""{tempRegFile}""");
            Utils.Run("reg unload HKLM\\OfflineSystem");
        }
    }
}