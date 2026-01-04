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
using DiscUtils.Iso9660;

namespace ExHyperV.Services
{
    public interface IStorageService
    {
        Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName);
        Task<List<HostDiskInfo>> GetHostDisksAsync();
        Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline);
        Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null, IsoFileSystemType? isoFileSystem = null);
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
                        foreach (var res in allResources)
                        {
                            int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                            if (rt == 5 || rt == 6)
                            {
                                controllers.Add(res);
                            }
                        }

                        var childrenMap = new Dictionary<string, List<CimInstance>>();
                        var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);

                        foreach (var res in allResources)
                        {
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

                        controllers = controllers.OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value).ToList();

                        int scsiCounter = 0;
                        int ideCounter = 0;

                        var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

                        foreach (var ctrl in controllers)
                        {
                            string ctrlId = ctrl.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                            int ctrlTypeVal = Convert.ToInt32(ctrl.CimInstanceProperties["ResourceType"]?.Value);
                            string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";

                            int ctrlNum = (ctrlType == "SCSI") ? scsiCounter++ : ideCounter++;

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
            return await Task.Run(() =>
            {
                var result = new List<HostDiskInfo>();
                var usedDiskNumbers = new HashSet<int>();

                try
                {
                    using (var session = CimSession.Create(null))
                    {
                        // 1. 快速获取所有被 Hyper-V 占用的物理磁盘编号
                        var vmUsedDisks = session.QueryInstances(@"root\virtualization\v2", "WQL",
                            "SELECT DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber IS NOT NULL");

                        foreach (var disk in vmUsedDisks)
                        {
                            if (int.TryParse(disk.CimInstanceProperties["DriveNumber"]?.Value?.ToString(), out int num))
                            {
                                usedDiskNumbers.Add(num);
                            }
                        }

                        // 2. 获取宿主机上所有物理磁盘
                        var allHostDisks = session.QueryInstances(@"Root\Microsoft\Windows\Storage", "WQL",
                            "SELECT Number, FriendlyName, Size, IsOffline, IsSystem, IsBoot, BusType, OperationalStatus FROM MSFT_Disk");

                        foreach (var disk in allHostDisks)
                        {
                            // 安全地获取属性值
                            var number = Convert.ToInt32(disk.CimInstanceProperties["Number"]?.Value ?? -1);
                            if (number == -1) continue;

                            var busType = Convert.ToUInt16(disk.CimInstanceProperties["BusType"]?.Value ?? 0);
                            var isSystem = Convert.ToBoolean(disk.CimInstanceProperties["IsSystem"]?.Value ?? false);
                            var isBoot = Convert.ToBoolean(disk.CimInstanceProperties["IsBoot"]?.Value ?? false);

                            // 3. 应用筛选逻辑
                            // 排除USB盘 (BusType 7), 系统盘, 引导盘, 以及已被VM占用的盘
                            if (busType == 7 || isSystem || isBoot || usedDiskNumbers.Contains(number))
                            {
                                continue;
                            }

                            // 属性映射
                            long sizeBytes = Convert.ToInt64(disk.CimInstanceProperties["Size"]?.Value ?? 0);
                            var opStatusArray = disk.CimInstanceProperties["OperationalStatus"]?.Value as ushort[];
                            // 假设 HostDiskInfo.OperationalStatus 是 string 类型
                            string opStatus = (opStatusArray != null && opStatusArray.Length > 0) ? opStatusArray[0].ToString() : "Unknown";

                            result.Add(new HostDiskInfo
                            {
                                Number = number,
                                FriendlyName = disk.CimInstanceProperties["FriendlyName"]?.Value?.ToString() ?? string.Empty,
                                SizeGB = Math.Round(sizeBytes / (1024.0 * 1024.0 * 1024.0), 2),
                                IsOffline = Convert.ToBoolean(disk.CimInstanceProperties["IsOffline"]?.Value ?? false),
                                IsSystem = isSystem,
                                OperationalStatus = opStatus
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 可以在这里记录详细错误日志，以便调试
                    Console.WriteLine($"[ERROR] GetHostDisksAsync failed: {ex.Message}");
                }

                return result;
            });
        }

        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline) => await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline ${isOffline.ToString().ToLower()}");

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null, IsoFileSystemType? isoFileSystem = null)
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";

            if (driveType == "DvdDrive" && isNew && !string.IsNullOrWhiteSpace(isoSourcePath))
            {
                await Task.Run(() =>
                {
                    string label = string.IsNullOrWhiteSpace(isoVolumeLabel) ? new DirectoryInfo(isoSourcePath).Name : isoVolumeLabel;
                    var targetDir = Path.GetDirectoryName(pathOrNumber);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    var builder = new CDBuilder
                    {
                        UseJoliet = true,
                        VolumeIdentifier = label
                    };

                    void AddFilesRecursively(string currentDir)
                    {
                        foreach (var file in Directory.GetFiles(currentDir))
                        {
                            string relPath = Path.GetRelativePath(isoSourcePath, file);
                            builder.AddFile(relPath, file);
                        }
                        foreach (var dir in Directory.GetDirectories(currentDir))
                        {
                            AddFilesRecursively(dir);
                        }
                    }

                    AddFilesRecursively(isoSourcePath);
                    builder.Build(pathOrNumber);
                });
            }

            string script = $@"
    $ErrorActionPreference = 'Stop'
    
    $vmName = '{vmName}'
    $v = Get-VM -Name $vmName
    
    $oldDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction SilentlyContinue
    $oldDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} | Where-Object {{ $_.ControllerType -eq '{controllerType}' }}

    if ('{controllerType}' -eq 'IDE' -and $v.State -eq 'Running') {{
        if ('{driveType}' -ne 'DvdDrive' -or (-not $oldDvd)) {{
            throw 'Storage_Error_IdeHotPlugNotSupported'
        }}
    }}

    if ('{controllerType}' -eq 'SCSI') {{
        $scsiCtrls = Get-VMScsiController -VMName $vmName | Sort-Object ControllerNumber
        $max = if ($scsiCtrls) {{ ($scsiCtrls | Select-Object -Last 1).ControllerNumber }} else {{ -1 }}
        
        if ({controllerNumber} -gt $max) {{
            if ($v.State -eq 'Running') {{
                throw 'Storage_Error_ScsiControllerHotAddNotSupported'
            }}
            for ($i = $max + 1; $i -le {controllerNumber}; $i++) {{ 
                Add-VMScsiController -VMName $vmName -ErrorAction Stop 
            }}
        }}
    }}
    
    if ('{driveType}' -eq 'HardDisk') {{
        if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
        if ($oldDvd) {{ Remove-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
        if ('{isNew.ToString().ToLower()}' -eq 'true') {{
            $vhdParams = @{{ Path = {psPath}; SizeBytes = {sizeGb}GB; {vhdType} = $true; ErrorAction = 'Stop' }}
            if ('{sectorFormat}' -eq '512n') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 512 }}
            elseif ('{sectorFormat}' -eq '512e') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
            elseif ('{sectorFormat}' -eq '4kn') {{ $vhdParams.LogicalSectorSizeBytes = 4096; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
            if ('{blockSize}' -ne 'Default') {{ $vhdParams.BlockSizeBytes = '{blockSize}' }}
            if ('{vhdType}' -eq 'Differencing') {{ $vhdParams.Remove('SizeBytes'); $vhdParams.Remove('Dynamic'); $vhdParams.Remove('Fixed'); $vhdParams.ParentPath = '{parentPath}' }}
            New-VHD @vhdParams
        }}
        $p = @{{ VMName=$vmName; ControllerType='{controllerType}'; ControllerNumber={controllerNumber}; ControllerLocation={location}; ErrorAction='Stop' }}
        if ('{isPhysical.ToString().ToLower()}' -eq 'true') {{ $p.DiskNumber={psPath} }} else {{ $p.Path={psPath} }}
        Add-VMHardDiskDrive @p
    }} else {{
        if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
        if ($oldDvd) {{ Set-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path {psPath} -ErrorAction Stop }}
        else {{ Add-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path {psPath} -ErrorAction Stop }}
    }}
    Write-Output ""RESULT:{controllerType},{controllerNumber},{location}""";
            try
            {
                var results = await Utils.Run2(script);
                string lastLine = results.LastOrDefault()?.ToString() ?? "";
                if (lastLine.StartsWith("RESULT:"))
                {
                    var parts = lastLine.Substring(7).Split(',');
                    return (true, "Storage_Msg_Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Storage_Msg_Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex) { return (false, GetFriendlyErrorMessage(ex.Message), controllerType, controllerNumber, location); }
        }
        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive)
        {
            string script = $@"
    $ErrorActionPreference = 'Stop'
    $vmName = '{vmName}'; $cnum = {drive.ControllerNumber}; $loc = {drive.ControllerLocation}; $ctype = '{drive.ControllerType}'
    $v = Get-VM -Name $vmName
    
    if ('{drive.DriveType}' -eq 'DvdDrive') {{
        $check = Get-VMDvdDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }}
        if (-not $check) {{ throw 'Storage_Error_DvdDriveNotFound' }}

        if ($v.State -eq 'Off' -or $ctype -eq 'SCSI') {{ 
            Get-VMDvdDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }} | Remove-VMDvdDrive -ErrorAction Stop
            return 'Storage_Msg_Removed' 
        }}
        else {{ 
            if ($check.Path) {{ 
                $check | Set-VMDvdDrive -Path $null -ErrorAction Stop; 
                return 'Storage_Msg_Ejected' 
            }} else {{ 
                throw 'Storage_Error_DvdHotRemoveNotSupported' 
            }} 
        }}
    }} else {{
        $disk = Get-VMHardDiskDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }}
        if (-not $disk) {{ throw 'Storage_Error_DiskNotFound' }}
        $disk | Remove-VMHardDiskDrive -ErrorAction Stop
        if ('{drive.DiskType}' -eq 'Physical' -and {drive.DiskNumber} -ne -1) {{ Start-Sleep -Milliseconds 500; Set-Disk -Number {drive.DiskNumber} -IsOffline $false -ErrorAction SilentlyContinue }}
        return 'Storage_Msg_Removed'
    }}";
            try
            {
                var results = await Utils.Run2(script);
                return (true, results.LastOrDefault()?.ToString() ?? "Storage_Msg_Removed");
            }
            catch (Exception ex) { return (false, GetFriendlyErrorMessage(ex.Message)); }
        }

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newIsoPath) => await RunCommandAsync($"Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {(string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'")} -ErrorAction Stop");

        private string GetFriendlyErrorMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return "Storage_Error_Unknown";
            var match = Regex.Match(rawMessage, @"Storage_(Error|Msg)_[A-Za-z0-9_]+");
            if (match.Success) return match.Value;
            string cleanMsg = Regex.Replace(rawMessage.Trim(), @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "").Replace("\r", "").Replace("\n", " ");
            var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return (parts.Count >= 2 && parts.Last().Length > 2) ? parts.Last() + "。" : cleanMsg;
        }

        public async Task<double> GetVhdSizeGbAsync(string path) { try { var res = await Utils.Run2($"Get-VHD -Path '{path}' | Select-Object -ExpandProperty Size"); return Math.Round(Convert.ToInt64(res[0].ToString()) / 1073741824.0, 2); } catch { return 0; } }

        public async Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            string script = $@"
    $v = Get-VM -Name '{vmName}'; $ctype = 'IDE'; $cnum = 0; $loc = 0; $found = $false
    if ($v.Generation -eq 2 -or ($v.Generation -eq 1 -and $v.State -eq 'Running')) {{
        $ctype = 'SCSI'; $controllers = Get-VMScsiController -VMName '{vmName}' | Sort-Object ControllerNumber
        foreach ($ctrl in $controllers) {{
            $cn = $ctrl.ControllerNumber; $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType SCSI -ControllerNumber $cn).ControllerLocation + (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $cn).ControllerLocation
            for ($i=0; $i -lt 64; $i++) {{ if ($used -notcontains $i) {{ $cnum = $cn; $loc = $i; $found = $true; break }} }}
            if ($found) {{ break }}
        }}
        if (-not $found) {{ $cnum = if ($controllers) {{ ($controllers | Sort-Object ControllerNumber | Select-Object -Last 1).ControllerNumber + 1 }} else {{ 0 }}; $loc = 0; }}
    }} else {{
        $ctype = 'IDE'; for ($c=0; $c -lt 2; $c++) {{ $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation + (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation; for ($i=0; $i -lt 2; $i++) {{ if ($used -notcontains $i) {{ $cnum=$c; $loc=$i; $found=$true; break }} }} if ($found) {{ break }} }}
    }} ""$ctype,$cnum,$loc""";
            var res = await ExecutePowerShellAsync(script); var parts = res.Trim().Split(',');
            return parts.Length == 3 ? (parts[0], int.Parse(parts[1]), int.Parse(parts[2])) : ("SCSI", 0, 0);
        }

        private async Task<string> ExecutePowerShellAsync(string script) { try { var results = await Utils.Run2(script); return results == null ? "" : string.Join(Environment.NewLine, results.Select(r => r?.ToString() ?? "")); } catch { return ""; } }
        private async Task<(bool Success, string Message)> RunCommandAsync(string script) { try { await Utils.Run2(script); return (true, "Storage_Msg_Success"); } catch (Exception ex) { return (false, GetFriendlyErrorMessage(ex.Message)); } }
    }
}