using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.Management.Infrastructure;

namespace ExHyperV.Services
{
    public interface IStorageService
    {
        Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName);
        Task<List<HostDiskInfo>> GetHostDisksAsync();
        Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline);
        Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "默认");
        Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive);
        Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newIsoPath);
        Task<double> GetVhdSizeGbAsync(string path);
        Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType);
    }

    public class StorageService : IStorageService
    {
        public async Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                var resultList = new List<VmStorageControllerInfo>();
                string namespaceName = @"root\virtualization\v2";
                string hostNamespace = @"root\cimv2";

                Dictionary<string, int>? hvDiskMap = null;
                Dictionary<int, HostDiskInfoCache>? osDiskMap = null;

                try
                {
                    using (var session = CimSession.Create(null))
                    {
                        var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                        var vm = session.QueryInstances(namespaceName, "WQL", vmQuery).FirstOrDefault();
                        if (vm == null) return resultList;

                        var settings = session.EnumerateAssociatedInstances(
                            namespaceName, vm, "Msvm_SettingsDefineState", "Msvm_VirtualSystemSettingData",
                            "ManagedElement", "SettingData").FirstOrDefault();

                        if (settings == null) return resultList;

                        var rasd = session.EnumerateAssociatedInstances(namespaceName, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_ResourceAllocationSettingData", "GroupComponent", "PartComponent").ToList();
                        var sasd = session.EnumerateAssociatedInstances(namespaceName, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_StorageAllocationSettingData", "GroupComponent", "PartComponent").ToList();

                        var allResources = new List<CimInstance>(rasd.Count + sasd.Count);
                        allResources.AddRange(rasd);
                        allResources.AddRange(sasd);

                        var controllers = new List<CimInstance>();
                        var childrenMap = new Dictionary<string, List<CimInstance>>();
                        var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);

                        foreach (var res in allResources)
                        {
                            int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                            if (rt == 5 || rt == 6)
                            {
                                controllers.Add(res);
                                continue;
                            }

                            var parentPath = res.CimInstanceProperties["Parent"]?.Value?.ToString();
                            if (!string.IsNullOrEmpty(parentPath))
                            {
                                var match = parentRegex.Match(parentPath);
                                if (match.Success)
                                {
                                    string parentId = match.Groups[1].Value.Replace("\\\\", "\\");
                                    if (!childrenMap.ContainsKey(parentId)) childrenMap[parentId] = new List<CimInstance>();
                                    childrenMap[parentId].Add(res);
                                }
                            }
                        }

                        controllers = controllers.OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value)
                                                 .ThenBy(c => c.CimInstanceProperties["Address"]?.Value).ToList();

                        var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

                        foreach (var ctrl in controllers)
                        {
                            string ctrlId = ctrl.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                            int ctrlTypeVal = Convert.ToInt32(ctrl.CimInstanceProperties["ResourceType"]?.Value);
                            string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                            int ctrlNum = int.Parse(ctrl.CimInstanceProperties["Address"]?.Value?.ToString() ?? "0");

                            var vmCtrlInfo = new VmStorageControllerInfo
                            {
                                VMName = vmName,
                                ControllerType = ctrlType,
                                ControllerNumber = ctrlNum,
                                AttachedDrives = new List<AttachedDriveInfo>()
                            };

                            if (childrenMap.ContainsKey(ctrlId))
                            {
                                foreach (var slot in childrenMap[ctrlId])
                                {
                                    int resType = Convert.ToInt32(slot.CimInstanceProperties["ResourceType"]?.Value);
                                    if (resType != 16 && resType != 17) continue;

                                    string address = slot.CimInstanceProperties["AddressOnParent"]?.Value?.ToString() ?? "0";
                                    int location = int.TryParse(address, out int loc) ? loc : 0;

                                    CimInstance? media = null;
                                    string slotId = slot.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";

                                    if (childrenMap.ContainsKey(slotId))
                                    {
                                        media = childrenMap[slotId].FirstOrDefault(m => {
                                            int t = Convert.ToInt32(m.CimInstanceProperties["ResourceType"]?.Value);
                                            return t == 31 || t == 16 || t == 22;
                                        });
                                    }

                                    var slotHostRes = slot.CimInstanceProperties["HostResource"]?.Value as string[];
                                    if (media == null && slotHostRes != null && slotHostRes.Length > 0)
                                    {
                                        media = slot;
                                    }

                                    var driveInfo = new AttachedDriveInfo
                                    {
                                        ControllerLocation = location,
                                        DriveType = (resType == 16) ? "DvdDrive" : "HardDisk",
                                        DiskType = "Empty"
                                    };

                                    if (media != null)
                                    {
                                        var hostRes = media.CimInstanceProperties["HostResource"]?.Value as string[];
                                        string rawPath = (hostRes != null && hostRes.Length > 0) ? hostRes[0] : "";

                                        if (!string.IsNullOrWhiteSpace(rawPath))
                                        {
                                            bool isPhysicalHardDisk = rawPath.IndexOf("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                      rawPath.IndexOf("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase) >= 0;
                                            bool isPhysicalCdRom = rawPath.IndexOf("CDROM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                   rawPath.IndexOf("Msvm_OpticalDrive", StringComparison.OrdinalIgnoreCase) >= 0;

                                            if (isPhysicalHardDisk)
                                            {
                                                driveInfo.DiskType = "Physical";
                                                driveInfo.PathOrDiskNumber = "Physical Drive";

                                                try
                                                {
                                                    if (hvDiskMap == null)
                                                    {
                                                        var allHvDisks = session.QueryInstances(namespaceName, "WQL", "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive");
                                                        hvDiskMap = new Dictionary<string, int>();
                                                        foreach (var d in allHvDisks)
                                                        {
                                                            string did = d.CimInstanceProperties["DeviceID"]?.Value?.ToString() ?? "";
                                                            did = did.Replace("\\\\", "\\");
                                                            int dnum = Convert.ToInt32(d.CimInstanceProperties["DriveNumber"]?.Value);
                                                            if (!string.IsNullOrEmpty(did)) hvDiskMap[did] = dnum;
                                                        }

                                                        var allOsDisks = session.QueryInstances(hostNamespace, "WQL", "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive");
                                                        osDiskMap = new Dictionary<int, HostDiskInfoCache>();
                                                        foreach (var d in allOsDisks)
                                                        {
                                                            int idx = Convert.ToInt32(d.CimInstanceProperties["Index"]?.Value);
                                                            long size = Convert.ToInt64(d.CimInstanceProperties["Size"]?.Value);
                                                            osDiskMap[idx] = new HostDiskInfoCache
                                                            {
                                                                Model = d.CimInstanceProperties["Model"]?.Value?.ToString(),
                                                                SerialNumber = d.CimInstanceProperties["SerialNumber"]?.Value?.ToString()?.Trim(),
                                                                SizeGB = Math.Round(size / (1024.0 * 1024.0 * 1024.0), 2)
                                                            };
                                                        }
                                                    }

                                                    var devMatch = deviceIdRegex.Match(rawPath);
                                                    if (devMatch.Success)
                                                    {
                                                        string devId = devMatch.Groups[1].Value.Replace("\\\\", "\\");
                                                        if (hvDiskMap != null && hvDiskMap.TryGetValue(devId, out int dNum))
                                                        {
                                                            driveInfo.DiskNumber = dNum;
                                                            driveInfo.PathOrDiskNumber = $"PhysicalDisk{dNum}";

                                                            if (osDiskMap != null && osDiskMap.TryGetValue(dNum, out var hostInfo))
                                                            {
                                                                driveInfo.DiskModel = hostInfo.Model;
                                                                driveInfo.SerialNumber = hostInfo.SerialNumber;
                                                                driveInfo.DiskSizeGB = hostInfo.SizeGB;
                                                            }
                                                        }
                                                    }
                                                    else if (rawPath.ToUpper().Contains("PHYSICALDRIVE"))
                                                    {
                                                        driveInfo.PathOrDiskNumber = rawPath;
                                                    }
                                                }
                                                catch { }
                                            }
                                            else if (isPhysicalCdRom)
                                            {
                                                driveInfo.DiskType = "Physical";
                                                driveInfo.PathOrDiskNumber = rawPath;
                                                driveInfo.DiskModel = "Passthrough Optical Drive";
                                            }
                                            else
                                            {
                                                driveInfo.DiskType = "Virtual";
                                                string cleanPath = rawPath.Trim('"');
                                                driveInfo.PathOrDiskNumber = cleanPath;
                                                try
                                                {
                                                    if (File.Exists(cleanPath))
                                                    {
                                                        var fi = new FileInfo(cleanPath);
                                                        driveInfo.DiskSizeGB = Math.Round(fi.Length / (1024.0 * 1024.0 * 1024.0), 2);
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                    vmCtrlInfo.AttachedDrives.Add(driveInfo);
                                }
                            }
                            vmCtrlInfo.AttachedDrives = vmCtrlInfo.AttachedDrives.OrderBy(d => d.ControllerLocation).ToList();
                            resultList.Add(vmCtrlInfo);
                        }
                    }
                }
                catch { }

                return resultList;
            });
        }

        private class HostDiskInfoCache
        {
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGB { get; set; }
        }

        public async Task<List<HostDiskInfo>> GetHostDisksAsync()
        {
            string script = @"
            $used = (Get-VM | Get-VMHardDiskDrive).DiskNumber
            $bootDiskNum = (Get-Partition | Where-Object { $_.IsBoot -eq $true }).DiskNumber | Select-Object -First 1
            Get-Disk | Where-Object {
                $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' -and
                $_.IsSystem -eq $false -and $_.IsBoot -eq $false -and
                $used -notcontains $_.Number
            } | Select-Object Number, FriendlyName, @{N='SizeGB';E={[math]::round($_.Size/1GB, 2)}}, IsOffline, IsSystem, OperationalStatus |
            ConvertTo-Json -Compress";

            var json = await ExecutePowerShellAsync(script);
            if (string.IsNullOrEmpty(json)) return new List<HostDiskInfo>();
            if (json.StartsWith("{") && !json.StartsWith("[")) json = "[" + json + "]";
            return JsonSerializer.Deserialize<List<HostDiskInfo>>(json) ?? new List<HostDiskInfo>();
        }

        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            string status = isOffline ? "$true" : "$false";
            return await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline {status}");
        }

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "默认")
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";

            string script = $@"
    $ErrorActionPreference = 'Stop'
    $vmName = '{vmName}'
    $ctype = '{controllerType}'; $cnum = {controllerNumber}; $loc = {location}
    $driveType = '{driveType}'
    $path = {psPath}

    $v = Get-VM -Name $vmName
    if (-not $v) {{ throw ""找不到虚拟机 $vmName"" }}
    $state = [int]$v.State

    if ($ctype -eq 'SCSI') {{
        $scsiCtrls = Get-VMScsiController -VMName $vmName | Sort-Object ControllerNumber
        $maxExisting = if ($scsiCtrls) {{ ($scsiCtrls | Select-Object -Last 1).ControllerNumber }} else {{ -1 }}
        if ($cnum -gt $maxExisting) {{
            if ($state -eq 2) {{ throw ""虚拟机正在运行，无法动态创建 SCSI 控制器 $cnum。"" }}
            for ($i = $maxExisting + 1; $i -le $cnum; $i++) {{
                Add-VMScsiController -VMName $vmName
            }}
        }}
    }}

    $oldDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc -ErrorAction SilentlyContinue
    $oldDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc | Where-Object {{ $_.ControllerType -eq $ctype }}

    if ($driveType -eq 'HardDisk') {{
        if ($state -eq 2 -and $ctype -eq 'IDE') {{ throw ""IDE 控制器不支持在虚拟机运行状态下更换硬盘。"" }}

        if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc }}
        if ($oldDvd) {{ Remove-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc }}

        if ('{isNew.ToString().ToLower()}' -eq 'true') {{
            $vhdParams = @{{ Path = $path; SizeBytes = {sizeGb}GB; {vhdType} = $true }}
            if ('{sectorFormat}' -eq '512n') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 512 }}
            elseif ('{sectorFormat}' -eq '512e') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
            elseif ('{sectorFormat}' -eq '4kn') {{ $vhdParams.LogicalSectorSizeBytes = 4096; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
            if ('{blockSize}' -ne '默认') {{ $vhdParams.BlockSizeBytes = '{blockSize}' }}
            if ('{vhdType}' -eq 'Differencing') {{
                $vhdParams.Remove('SizeBytes'); $vhdParams.Remove('Dynamic'); $vhdParams.Remove('Fixed')
                $vhdParams.ParentPath = '{parentPath}'
            }}
            New-VHD @vhdParams
        }}

        $p = @{{ VMName=$vmName; ControllerType=$ctype; ControllerNumber=$cnum; ControllerLocation=$loc }}
        if ('{isPhysical.ToString().ToLower()}' -eq 'true') {{ $p.DiskNumber=$path }} else {{ $p.Path=$path }}
        Add-VMHardDiskDrive @p
    }} else {{
        if ($oldDisk) {{ 
            if ($state -eq 2) {{ throw ""运行状态下无法将硬盘位更换为光驱位。"" }}
            Remove-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc 
        }}
        if ($oldDvd) {{ Set-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -Path $path }}
        else {{ Add-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -Path $path }}
    }}
    Write-Output ""RESULT:$ctype,$cnum,$loc""";

            try
            {
                var results = await Utils.Run2(script);
                string lastLine = results.LastOrDefault()?.ToString() ?? "";
                if (lastLine.StartsWith("RESULT:"))
                {
                    var parts = lastLine.Substring(7).Split(',');
                    return (true, "Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, controllerType, controllerNumber, location);
            }
        }
        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive)
        {
            string script = $@"
            $ErrorActionPreference = 'Stop'
            $vmName = '{vmName}'
            $cnum = {drive.ControllerNumber}
            $loc = {drive.ControllerLocation}

            $vmWmi = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter ""ElementName = '$vmName'""
            $state = [int]$vmWmi.EnabledState

            if ('{drive.DriveType}' -eq 'DvdDrive') {{
                $dvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -ErrorAction SilentlyContinue
                
                if (-not $dvd) {{ return ""SUCCESS:AlreadyRemoved"" }}

                if ($state -eq 3) {{
                    Remove-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc
                    return ""SUCCESS:Removed""
                }} 
                else {{
                    if (-not [string]::IsNullOrWhiteSpace($dvd.Path)) {{
                        $dvd | Set-VMDvdDrive -Path $null
                        return ""SUCCESS:Ejected""
                    }} else {{
                        throw ""虚拟机运行中，无法移除空光驱硬件。""
                    }}
                }}
            }} else {{
                Remove-VMHardDiskDrive -VMName $vmName -ControllerType '{drive.ControllerType}' -ControllerNumber $cnum -ControllerLocation $loc
                if ('{drive.DiskType}' -eq 'Physical' -and {drive.DiskNumber} -ne -1) {{
                    Start-Sleep -Milliseconds 500
                    Set-Disk -Number {drive.DiskNumber} -IsOffline $false -ErrorAction SilentlyContinue
                }}
            }}";

            return await RunCommandAsync(script);
        }

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newIsoPath)
        {
            string pathArgument = string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'";
            string script = $@"
            $ErrorActionPreference = 'Stop'
            $dvd = Get-VMDvdDrive -VMName '{vmName}' | Where-Object {{ 
                $_.ControllerNumber -eq {controllerNumber} -and 
                $_.ControllerLocation -eq {controllerLocation}
            }} | Select-Object -First 1

            if ($dvd) {{
                $dvd | Set-VMDvdDrive -Path {pathArgument}
            }} else {{
                throw '找不到指定位置的光驱设备'
            }}";
            return await RunCommandAsync(script);
        }

        public async Task<double> GetVhdSizeGbAsync(string path)
        {
            string script = $"Get-VHD -Path '{path}' | Select-Object -ExpandProperty Size";
            try
            {
                var res = await Utils.Run2(script);
                if (res != null && res.Count > 0 && long.TryParse(res[0].ToString().Trim(), out long bytes))
                {
                    return Math.Round(bytes / 1073741824.0, 2);
                }
            }
            catch { }
            return 0;
        }

        public async Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            string script = $@"
    $v = Get-VM -Name '{vmName}'
    $ctype = 'IDE'; $cnum = 0; $loc = 0;
    $found = $false

    if ($v.Generation -eq 2 -or ($v.Generation -eq 1 -and $v.State -eq 'Running')) {{
        $ctype = 'SCSI'
        # 获取所有已存在的 SCSI 控制器并按编号排序
        $controllers = Get-VMScsiController -VMName '{vmName}' | Sort-Object ControllerNumber
        
        # 1. 尝试在现有控制器中寻找空位
        foreach ($ctrl in $controllers) {{
            $cn = $ctrl.ControllerNumber
            $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType SCSI -ControllerNumber $cn).ControllerLocation + `
                    (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $cn).ControllerLocation
            
            for ($i=0; $i -lt 64; $i++) {{
                if ($used -notcontains $i) {{
                    $cnum = $cn; $loc = $i; $found = $true; break
                }}
            }}
            if ($found) {{ break }}
        }}

        # 2. 如果现有控制器都满了，且控制器数量未达上限(4个)，则建议使用下一个控制器编号
        if (-not $found) {{
            $existingNums = $controllers.ControllerNumber
            for ($cn=0; $cn -lt 4; $cn++) {{
                if ($existingNums -notcontains $cn) {{
                    $cnum = $cn; $loc = 0; $found = $true; break
                }}
            }}
        }}
    }} else {{
        # IDE 逻辑保持不变 (最多2个控制器，每个2个位置)
        $ctype = 'IDE'
        for ($c=0; $c -lt 2; $c++) {{
            $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation + `
                    (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation
            for ($i=0; $i -lt 2; $i++) {{
                if ($used -notcontains $i) {{
                    $cnum=$c; $loc=$i; $found=$true; break
                }}
            }}
            if ($found) {{ break }}
        }}
    }}
    
    # 返回格式：类型,控制器编号,插槽位置
    ""$ctype,$cnum,$loc""";

            var res = await ExecutePowerShellAsync(script);
            var parts = res.Trim().Split(',');
            if (parts.Length == 3) return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
            return ("SCSI", 0, 0);
        }
        private async Task<string> ExecutePowerShellAsync(string script)
        {
            try
            {
                var results = await Utils.Run2(script);
                if (results == null || results.Count == 0) return string.Empty;
                return string.Join(Environment.NewLine, results.Select(r => r?.ToString() ?? ""));
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            try
            {
                await Utils.Run2(script);
                return (true, "Success");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}