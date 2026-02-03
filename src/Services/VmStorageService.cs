using System.IO;
using System.Text.RegularExpressions;
using DiscUtils.Iso9660;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.Management.Infrastructure;

namespace ExHyperV.Services
{
    public class VmStorageService
    {
        private const string NamespaceV2 = @"root\virtualization\v2";
        private const string NamespaceCimV2 = @"root\cimv2";
        private const string NamespaceStorage = @"Root\Microsoft\Windows\Storage";


        //查询虚拟机下的控制器和存储设备
        public async Task LoadVmStorageItemsAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;

            var items = await Task.Run(() =>
            {
                var resultList = new List<VmStorageItem>();
                Dictionary<string, int>? hvDiskMap = null;
                Dictionary<int, HostDiskInfoCache>? osDiskMap = null;

                try
                {
                    using (var session = CimSession.Create(null))
                    {
                        var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vm.Name}'";
                        var vmInstance = session.QueryInstances(NamespaceV2, "WQL", vmQuery).FirstOrDefault();
                        if (vmInstance == null) return resultList;

                        var settings = session.EnumerateAssociatedInstances(
                            NamespaceV2, vmInstance, "Msvm_SettingsDefineState", "Msvm_VirtualSystemSettingData",
                            "ManagedElement", "SettingData").FirstOrDefault();

                        if (settings == null) return resultList;

                        var rasd = session.EnumerateAssociatedInstances(NamespaceV2, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_ResourceAllocationSettingData", "GroupComponent", "PartComponent").ToList();
                        var sasd = session.EnumerateAssociatedInstances(NamespaceV2, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_StorageAllocationSettingData", "GroupComponent", "PartComponent").ToList();

                        var allResources = new List<CimInstance>(rasd.Count + sasd.Count);
                        allResources.AddRange(rasd);
                        allResources.AddRange(sasd);

                        var controllers = allResources.Where(res => {
                            int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                            return rt == 5 || rt == 6;
                        }).OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value).ToList();

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

                        int scsiCounter = 0;
                        int ideCounter = 0;
                        var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

                        foreach (var ctrl in controllers)
                        {
                            string ctrlId = ctrl.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                            int ctrlTypeVal = Convert.ToInt32(ctrl.CimInstanceProperties["ResourceType"]?.Value);
                            string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                            int ctrlNum = (ctrlType == "SCSI") ? scsiCounter++ : ideCounter++;

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

                                    var driveItem = new VmStorageItem
                                    {
                                        ControllerType = ctrlType,
                                        ControllerNumber = ctrlNum,
                                        ControllerLocation = location,
                                        DriveType = (resType == 16) ? "DvdDrive" : "HardDisk",
                                        DiskType = "Empty"
                                    };

                                    var slotHostRes = slot.CimInstanceProperties["HostResource"]?.Value as string[];
                                    var effectiveMedia = media ?? ((slotHostRes != null && slotHostRes.Length > 0) ? slot : null);

                                    if (effectiveMedia != null)
                                    {
                                        var hostRes = effectiveMedia.CimInstanceProperties["HostResource"]?.Value as string[];
                                        string rawPath = (hostRes != null && hostRes.Length > 0) ? hostRes[0] : "";

                                        if (!string.IsNullOrWhiteSpace(rawPath))
                                        {
                                            bool isPhysicalHardDisk = rawPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) ||
                                                                      rawPath.ToUpper().Contains("PHYSICALDRIVE");

                                            bool isPhysicalCdRom = rawPath.Contains("CDROM", StringComparison.OrdinalIgnoreCase) ||
                                                                   rawPath.Contains("Msvm_OpticalDrive", StringComparison.OrdinalIgnoreCase);

                                            if (isPhysicalHardDisk)
                                            {
                                                driveItem.DiskType = "Physical";
                                                try
                                                {
                                                    // [核心逻辑] 物理磁盘双表映射
                                                    if (hvDiskMap == null)
                                                    {
                                                        // A表：HV 内部映射 (DeviceID -> DriveNumber)
                                                        // 修复：不使用 ToDictionary，改用循环以防止空值崩溃或键值重复崩溃
                                                        hvDiskMap = new Dictionary<string, int>();
                                                        var allHvDisks = session.QueryInstances(NamespaceV2, "WQL", "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive");
                                                        foreach (var d in allHvDisks)
                                                        {
                                                            // 安全获取 DeviceID，处理可能的 Null 值
                                                            string did = d.CimInstanceProperties["DeviceID"]?.Value?.ToString() ?? "";
                                                            // 只有当 DeviceID 和 DriveNumber 都有效时才添加
                                                            if (!string.IsNullOrEmpty(did) && d.CimInstanceProperties["DriveNumber"]?.Value != null)
                                                            {
                                                                // 统一反斜杠格式
                                                                did = did.Replace("\\\\", "\\");
                                                                int dnum = Convert.ToInt32(d.CimInstanceProperties["DriveNumber"].Value);
                                                                // 使用索引器赋值，防止重复键报错 (Last wins)
                                                                hvDiskMap[did] = dnum;
                                                            }
                                                        }

                                                        // B表：宿主机物理信息 (Index -> Info)
                                                        // 修复：同样改回循环处理，增加安全性
                                                        osDiskMap = new Dictionary<int, HostDiskInfoCache>();
                                                        var allOsDisks = session.QueryInstances(NamespaceCimV2, "WQL", "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive");
                                                        foreach (var d in allOsDisks)
                                                        {
                                                            if (d.CimInstanceProperties["Index"]?.Value != null)
                                                            {
                                                                int idx = Convert.ToInt32(d.CimInstanceProperties["Index"].Value);

                                                                // 安全获取大小，防止转换错误
                                                                long sizeBytes = 0;
                                                                if (d.CimInstanceProperties["Size"]?.Value != null)
                                                                {
                                                                    long.TryParse(d.CimInstanceProperties["Size"].Value.ToString(), out sizeBytes);
                                                                }

                                                                osDiskMap[idx] = new HostDiskInfoCache
                                                                {
                                                                    Model = d.CimInstanceProperties["Model"]?.Value?.ToString(),
                                                                    SerialNumber = d.CimInstanceProperties["SerialNumber"]?.Value?.ToString()?.Trim(),
                                                                    SizeGB = Math.Round(sizeBytes / 1073741824.0, 2)
                                                                };
                                                            }
                                                        }
                                                    }
                                                    var devMatch = deviceIdRegex.Match(rawPath);
                                                    int dNum = -1;
                                                    if (devMatch.Success) hvDiskMap.TryGetValue(devMatch.Groups[1].Value.Replace("\\\\", "\\"), out dNum);
                                                    else if (rawPath.ToUpper().Contains("PHYSICALDRIVE"))
                                                    {
                                                        var numMatch = Regex.Match(rawPath, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
                                                        if (numMatch.Success) dNum = int.Parse(numMatch.Groups[1].Value);
                                                    }

                                                    if (dNum != -1)
                                                    {
                                                        driveItem.DiskNumber = dNum;
                                                        driveItem.PathOrDiskNumber = $"PhysicalDisk{dNum}";
                                                        if (osDiskMap != null && osDiskMap.TryGetValue(dNum, out var hostInfo))
                                                        {
                                                            driveItem.DiskModel = hostInfo.Model;
                                                            driveItem.SerialNumber = hostInfo.SerialNumber;
                                                            driveItem.DiskSizeGB = hostInfo.SizeGB;
                                                        }
                                                    }
                                                }
                                                catch { }
                                            }
                                            else if (isPhysicalCdRom)
                                            {
                                                driveItem.DiskType = "Physical";
                                                driveItem.PathOrDiskNumber = rawPath;
                                                driveItem.DiskModel = "Passthrough Optical Drive";
                                            }
                                            else
                                            {
                                                driveItem.DiskType = "Virtual";
                                                driveItem.PathOrDiskNumber = rawPath.Trim('"');
                                                if (File.Exists(driveItem.PathOrDiskNumber))
                                                {
                                                    try
                                                    {
                                                        driveItem.DiskSizeGB = new FileInfo(driveItem.PathOrDiskNumber).Length / 1073741824.0;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }
                                    resultList.Add(driveItem);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading storage: {ex.Message}");
                }
                return resultList;
            });

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                vm.StorageItems.Clear();
                foreach (var item in items.OrderBy(i => i.ControllerType).ThenBy(i => i.ControllerNumber).ThenBy(i => i.ControllerLocation))
                    vm.StorageItems.Add(item);
            });
        }

        //查询主机可用的直通硬盘
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
                        var vmUsedDisks = session.QueryInstances(NamespaceV2, "WQL", "SELECT DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber IS NOT NULL");
                        foreach (var disk in vmUsedDisks)
                            if (int.TryParse(disk.CimInstanceProperties["DriveNumber"]?.Value?.ToString(), out int num)) usedDiskNumbers.Add(num);

                        var allHostDisks = session.QueryInstances(NamespaceStorage, "WQL", "SELECT Number, FriendlyName, Size, IsOffline, IsSystem, IsBoot, BusType, OperationalStatus FROM MSFT_Disk");
                        foreach (var disk in allHostDisks)
                        {
                            var number = Convert.ToInt32(disk.CimInstanceProperties["Number"]?.Value ?? -1);
                            if (number == -1) continue;
                            var busType = Convert.ToUInt16(disk.CimInstanceProperties["BusType"]?.Value ?? 0);
                            bool isSystem = Convert.ToBoolean(disk.CimInstanceProperties["IsSystem"]?.Value ?? false);
                            bool isBoot = Convert.ToBoolean(disk.CimInstanceProperties["IsBoot"]?.Value ?? false);

                            if (busType == 7 || isSystem || isBoot || usedDiskNumbers.Contains(number)) continue;

                            var opStatusArr = disk.CimInstanceProperties["OperationalStatus"]?.Value as ushort[];
                            string opStatus = (opStatusArr != null && opStatusArr.Length > 0) ? opStatusArr[0].ToString() : "Unknown";

                            result.Add(new HostDiskInfo
                            {
                                Number = number,
                                FriendlyName = disk.CimInstanceProperties["FriendlyName"]?.Value?.ToString() ?? string.Empty,
                                SizeGB = Math.Round(Convert.ToInt64(disk.CimInstanceProperties["Size"].Value) / 1073741824.0, 2),
                                IsOffline = Convert.ToBoolean(disk.CimInstanceProperties["IsOffline"]?.Value ?? false),
                                IsSystem = isSystem,
                                OperationalStatus = opStatus
                            });
                        }
                    }
                }
                catch { }
                return result;
            });
        }

        //将指定的直通硬盘脱机
        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
            => await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline ${isOffline.ToString().ToLower()}");


        //检测存储设备第一个可用的空位
        public async Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            string script = $@"
    $v = Get-VM -Name '{vmName}'; $ctype = 'NONE'; $cnum = -1; $loc = -1; $found = $false
    # 1代机且没开机时优先 IDE
    if ($v.Generation -eq 1 -and $v.State -ne 'Running') {{
        for ($c=0; $c -lt 2; $c++) {{ 
            $h_loc = @((Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation)
            $d_loc = @((Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation)
            $used = $h_loc + $d_loc
            for ($i=0; $i -lt 2; $i++) {{ if ($used -notcontains $i) {{ $ctype='IDE'; $cnum=$c; $loc=$i; $found=$true; break }} }} 
            if ($found) {{ break }} 
        }}
    }}
    # 如果 IDE 满了或者是一代开机状态/二代机，尝试 SCSI
    if (-not $found) {{
        $controllers = Get-VMScsiController -VMName '{vmName}' | Sort-Object ControllerNumber
        foreach ($ctrl in $controllers) {{
            $cn = $ctrl.ControllerNumber
            $h_loc = @((Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType SCSI -ControllerNumber $cn).ControllerLocation)
            $d_loc = @((Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $cn).ControllerLocation)
            $used = $h_loc + $d_loc
            for ($i=0; $i -lt 64; $i++) {{ if ($used -notcontains $i) {{ $ctype='SCSI'; $cnum = $cn; $loc = $i; $found = $true; break }} }}
            if ($found) {{ break }}
        }}
    }}
    ""$ctype,$cnum,$loc""";

            var res = await ExecutePowerShellAsync(script);
            var parts = res.Trim().Split(',');
            if (parts.Length == 3 && parts[0] != "NONE")
                return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]));

            return ("NONE", -1, -1); // 明确表示没有位置了
        }

        //添加硬盘或光盘
        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(
    string vmName, string controllerType, int controllerNumber, int location, string driveType,
    string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256,
    string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default",
    string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";

            if (driveType == "DvdDrive" && isNew && !string.IsNullOrWhiteSpace(isoSourcePath))
            {
                var createResult = await CreateIsoFromDirectoryAsync(isoSourcePath, pathOrNumber, isoVolumeLabel);
                if (!createResult.Success) return (false, createResult.Message, controllerType, controllerNumber, location);
            }

            string script = $@"
                $ErrorActionPreference = 'Stop'
                $vmName = '{vmName}'; $v = Get-VM -Name $vmName
    $targetDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction SilentlyContinue
    $targetDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} | Where-Object {{ $_.ControllerType -eq '{controllerType}' }}
    if ($targetDisk -or $targetDvd) {{
        throw 'Storage_Error_SlotOccupied'
    }}

                $oldDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction SilentlyContinue
                $oldDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} | Where-Object {{ $_.ControllerType -eq '{controllerType}' }}

                if ('{controllerType}' -eq 'IDE' -and $v.State -eq 'Running') {{
                    if ('{driveType}' -ne 'DvdDrive' -or (-not $oldDvd)) {{ throw 'Storage_Error_IdeHotPlugNotSupported' }}
                }}
                if ('{controllerType}' -eq 'SCSI') {{
                    $scsiCtrls = Get-VMScsiController -VMName $vmName | Sort-Object ControllerNumber
                    $max = if ($scsiCtrls) {{ ($scsiCtrls | Select-Object -Last 1).ControllerNumber }} else {{ -1 }}
                    if ({controllerNumber} -gt $max) {{
                        if ($v.State -eq 'Running') {{ throw 'Storage_Error_ScsiControllerHotAddNotSupported' }}
                        for ($i = $max + 1; $i -le {controllerNumber}; $i++) {{ Add-VMScsiController -VMName $vmName -ErrorAction Stop }}
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
                var last = results.LastOrDefault()?.ToString() ?? "";
                if (last.StartsWith("RESULT:"))
                {
                    var parts = last.Substring(7).Split(',');
                    return (true, "Storage_Msg_Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Storage_Msg_Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message), controllerType, controllerNumber, location); }
        }

        //移除存储设备
        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, VmStorageItem drive)
        {
            string script = $@"
                $ErrorActionPreference = 'Stop'
                $vmName = '{vmName}'; $cnum = {drive.ControllerNumber}; $loc = {drive.ControllerLocation}; $ctype = '{drive.ControllerType}'
                $v = Get-VM -Name $vmName
                if ('{drive.DriveType}' -eq 'DvdDrive') {{
                    $check = Get-VMDvdDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }}
                    if (-not $check) {{ throw 'Storage_Error_DvdDriveNotFound' }}
                    if ($v.State -eq 'Off' -or $ctype -eq 'SCSI') {{ 
                        $check | Remove-VMDvdDrive -ErrorAction Stop 
                        return 'Storage_Msg_Removed'
                    }}
                    else {{ 
                        if ($check.Path) {{ 
                            $check | Set-VMDvdDrive -Path $null -ErrorAction Stop
                            return 'Storage_Msg_Ejected'
                        }} else {{ throw 'Storage_Error_DvdHotRemoveNotSupported' }} 
                    }}
                }} else {{
                    $disk = Get-VMHardDiskDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }}
                    if (-not $disk) {{ throw 'Storage_Error_DiskNotFound' }}
                    $disk | Remove-VMHardDiskDrive -ErrorAction Stop
                    
                    if ('{drive.DiskType}' -eq 'Physical' -and {drive.DiskNumber} -gt -1) {{ 
                        Start-Sleep -Milliseconds 500
                        Set-Disk -Number {drive.DiskNumber} -IsOffline $false -ErrorAction SilentlyContinue 
                    }}
                    return 'Storage_Msg_Removed'
                }}";
            try
            {
                var res = await Utils.Run2(script);
                return (true, res.LastOrDefault()?.ToString() ?? "Storage_Msg_Removed");
            }
            catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message)); }
        }

        //修改光盘路径
        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, int controllerNumber, int controllerLocation, string newIsoPath)
            => await RunCommandAsync($"Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {(string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'")} -ErrorAction Stop");

        //修改硬盘路径
        public async Task<(bool Success, string Message)> ModifyHardDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newPath)
        {
            string psPath = string.IsNullOrWhiteSpace(newPath) ? "$null" : $"'{newPath}'";
            string script = $@"
        $ErrorActionPreference = 'Stop'
        Set-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {psPath} -ErrorAction Stop";

            return await RunCommandAsync(script);
        }

        //从执行结果解析字符串
        private async Task<string> ExecutePowerShellAsync(string script)
        {
            try
            {
                var res = await Utils.Run2(script);
                return res == null ? "" : string.Join(Environment.NewLine, res.Select(r => r?.ToString() ?? ""));
            }
            catch { return ""; }
        }

        //创建ISO 9660标准
        private async Task<(bool Success, string Message)> CreateIsoFromDirectoryAsync(string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists) return (false, "Iso_Error_SourceDirNotFound");

            const long MaxFileSize = 4294967295;
            const int MaxFileNameLength = 64;
            const int MaxPathLength = 240;
            const int MaxDirectoryDepth = 8;
            const int MaxVolumeLabelLength = 16;

            string finalVolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? sourceDirInfo.Name : volumeLabel;
            if (finalVolumeLabel.Length > MaxVolumeLabelLength) return (false, "Iso_Error_VolumeLabelTooLong");

            return await Task.Run(() => {
                try
                {
                    foreach (var item in sourceDirInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        string relativePath = Path.GetRelativePath(sourceDirInfo.FullName, item.FullName);
                        if (item.Name.Length > MaxFileNameLength) return (false, "Iso_Error_FileNameTooLong");
                        if (relativePath.Length > MaxPathLength) return (false, "Iso_Error_PathTooLong");

                        int depth = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
                        if (item is FileInfo fileInfo)
                        {
                            if (depth - 1 >= MaxDirectoryDepth) return (false, "Iso_Error_FileDepthTooDeep");
                            if (fileInfo.Length >= MaxFileSize) return (false, "Iso_Error_FileTooLarge");
                        }
                        else if (depth > MaxDirectoryDepth) return (false, "Iso_Error_DirectoryDepthTooDeep");
                    }

                    var targetDir = Path.GetDirectoryName(targetIsoPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = finalVolumeLabel };
                    foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                        builder.AddFile(Path.GetRelativePath(sourceDirectory, file), file);

                    builder.Build(targetIsoPath);
                    return (true, "Iso_Msg_CreateSuccess");
                }
                catch (Exception ex) { return (false, $"Iso_Error_BuildFailed: {ex.Message}"); }
            });
        }

        //包装器，勿动
        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            try { await Utils.Run2(script); return (true, "Storage_Msg_Success"); }
            catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message)); }
        }

        //辅助类，勿动
        private class HostDiskInfoCache
        {
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGB { get; set; }
        }
    }
}