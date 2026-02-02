using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management; // 用于 GetVhdSizeGbAsync
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.Management.Infrastructure; // 用于 CimSession

namespace ExHyperV.Services
{
    public class VmStorageService
    {
        private const string NamespaceV2 = @"root\virtualization\v2";
        private const string NamespaceCimV2 = @"root\cimv2";
        private const string NamespaceStorage = @"Root\Microsoft\Windows\Storage";

        #region 1. 核心查询逻辑 (WMI/CIM)

        /// <summary>
        /// 全量加载虚拟机的存储架构并映射到 VmInstanceInfo.StorageItems
        /// </summary>
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

                        // 获取资源分配设置 (RASD) 和 存储分配设置 (SASD)
                        var rasd = session.EnumerateAssociatedInstances(NamespaceV2, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_ResourceAllocationSettingData", "GroupComponent", "PartComponent").ToList();
                        var sasd = session.EnumerateAssociatedInstances(NamespaceV2, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_StorageAllocationSettingData", "GroupComponent", "PartComponent").ToList();

                        var allResources = new List<CimInstance>(rasd.Count + sasd.Count);
                        allResources.AddRange(rasd);
                        allResources.AddRange(sasd);

                        // 筛选控制器 (5=IDE, 6=SCSI)
                        var controllers = allResources.Where(res => {
                            int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                            return rt == 5 || rt == 6;
                        }).OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value).ToList();

                        // 构建父子关系映射
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
                                    // 16=DVD, 17=Disk Controller connection point? 通常这里直接看挂载的驱动器
                                    // 实际上 Hyper-V 中驱动器本身也是 Resource，挂在控制器下
                                    int resType = Convert.ToInt32(slot.CimInstanceProperties["ResourceType"]?.Value);
                                    if (resType != 16 && resType != 17) continue;

                                    string address = slot.CimInstanceProperties["AddressOnParent"]?.Value?.ToString() ?? "0";
                                    int location = int.TryParse(address, out int loc) ? loc : 0;

                                    // 查找实际的媒体 (Media)
                                    // 31=Virtual Hard Disk, 16=Logical Disk (ISO/DVD), 22=Physical Disk
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

                                    // 如果没有子节点媒体，检查插槽本身是否有 HostResource (通常用于物理直通或 ISO)
                                    var slotHostRes = slot.CimInstanceProperties["HostResource"]?.Value as string[];
                                    var effectiveMedia = media ?? ((slotHostRes != null && slotHostRes.Length > 0) ? slot : null);

                                    if (effectiveMedia != null)
                                    {
                                        var hostRes = effectiveMedia.CimInstanceProperties["HostResource"]?.Value as string[];
                                        string rawPath = (hostRes != null && hostRes.Length > 0) ? hostRes[0] : "";

                                        if (!string.IsNullOrWhiteSpace(rawPath))
                                        {
                                            // 判定物理硬盘
                                            bool isPhysicalHardDisk = rawPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) ||
                                                                      rawPath.ToUpper().Contains("PHYSICALDRIVE");

                                            // [修复] 判定物理光驱 (被遗漏的逻辑)
                                            bool isPhysicalCdRom = rawPath.Contains("CDROM", StringComparison.OrdinalIgnoreCase) ||
                                                                   rawPath.Contains("Msvm_OpticalDrive", StringComparison.OrdinalIgnoreCase);

                                            if (isPhysicalHardDisk)
                                            {
                                                driveItem.DiskType = "Physical";
                                                try
                                                {
                                                    // 延迟加载主机磁盘映射缓存
                                                    if (hvDiskMap == null)
                                                    {
                                                        hvDiskMap = session.QueryInstances(NamespaceV2, "WQL", "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive")
                                                            .ToDictionary(d => d.CimInstanceProperties["DeviceID"].Value.ToString().Replace("\\\\", "\\"), d => Convert.ToInt32(d.CimInstanceProperties["DriveNumber"].Value));

                                                        osDiskMap = session.QueryInstances(NamespaceCimV2, "WQL", "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive")
                                                            .ToDictionary(d => Convert.ToInt32(d.CimInstanceProperties["Index"].Value), d => new HostDiskInfoCache
                                                            {
                                                                Model = d.CimInstanceProperties["Model"]?.Value?.ToString(),
                                                                SerialNumber = d.CimInstanceProperties["SerialNumber"]?.Value?.ToString()?.Trim(),
                                                                SizeGB = Math.Round(Convert.ToInt64(d.CimInstanceProperties["Size"].Value) / 1073741824.0, 2)
                                                            });
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
                                                        driveItem.PathOrDiskNumber = $"PhysicalDrive{dNum}";
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
                                                // [修复] 物理光驱直通处理
                                                driveItem.DiskType = "Physical";
                                                driveItem.PathOrDiskNumber = rawPath; // 通常显示为 CDROM0 等
                                                driveItem.DiskModel = "Host Optical Drive";
                                            }
                                            else
                                            {
                                                // 虚拟文件 (VHD/VHDX/ISO)
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
                    // 记录日志或处理异常
                    System.Diagnostics.Debug.WriteLine($"Error loading storage: {ex.Message}");
                }
                return resultList;
            });

            // 回到 UI 线程更新集合
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                vm.StorageItems.Clear();
                foreach (var item in items.OrderBy(i => i.ControllerType).ThenBy(i => i.ControllerNumber).ThenBy(i => i.ControllerLocation))
                    vm.StorageItems.Add(item);
            });
        }

        /// <summary>
        /// 获取主机可用于直通的物理磁盘列表
        /// </summary>
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

        #endregion

        #region 2. 存储操作逻辑 (PowerShell)

        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
            => await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline ${isOffline.ToString().ToLower()}");

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(
            string vmName, string controllerType, int controllerNumber, int location, string driveType,
            string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256,
            string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default",
            string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";

            // [修复] ISO 制作前置校验
            if (driveType == "DvdDrive" && isNew && !string.IsNullOrWhiteSpace(isoSourcePath))
            {
                var createResult = await CreateIsoFromDirectoryAsync(isoSourcePath, pathOrNumber, isoVolumeLabel);
                if (!createResult.Success) return (false, createResult.Message, controllerType, controllerNumber, location);
            }

            // [修复] 使用 B 代码更健壮的脚本逻辑
            string script = $@"
                $ErrorActionPreference = 'Stop'
                $vmName = '{vmName}'; $v = Get-VM -Name $vmName
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
                ""RESULT:{controllerType},{controllerNumber},{location}""";

            try
            {
                var results = await Utils.Run2(script);
                var last = results.LastOrDefault()?.ToString() ?? "";
                if (last.StartsWith("RESULT:"))
                {
                    var parts = last.Substring(7).Split(',');
                    return (true, "Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message), controllerType, controllerNumber, location); }
        }

        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, VmStorageItem drive)
        {
            // [修复] 增加物理硬盘 DiskNumber 检查，区分 Removed 和 Ejected 状态
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
                    
                    # Safety check for physical disk
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

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, int controllerNumber, int controllerLocation, string newIsoPath)
            => await RunCommandAsync($"Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {(string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'")} -ErrorAction Stop");

        #endregion

        #region 3. 辅助功能 (VHD查询/插槽计算/ISO制作)

        public async Task<double> GetVhdSizeGbAsync(string path)
        {
            return await Task.Run(() => {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(new ManagementScope(NamespaceV2), new ObjectQuery("SELECT * FROM Msvm_ImageManagementService")))
                    {
                        var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                        if (service == null) return 0;
                        using (var inParams = service.GetMethodParameters("GetVirtualHardDiskSettingData"))
                        {
                            inParams["Path"] = path;
                            using (var outParams = service.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null))
                            {
                                var vhdSetting = new ManagementClass("Msvm_VirtualHardDiskSettingData") { ["text"] = outParams["SettingData"].ToString() };
                                return Math.Round((ulong)vhdSetting.GetPropertyValue("MaxInternalSize") / 1073741824.0, 2);
                            }
                        }
                    }
                }
                catch { return 0; }
            });
        }

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
                    if (-not $found) {{ $cnum = if ($controllers) {{ ($controllers | Select-Object -Last 1).ControllerNumber + 1 }} else {{ 0 }}; $loc = 0; }}
                }} else {{
                    $ctype = 'IDE'; for ($c=0; $c -lt 2; $c++) {{ $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation + (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation; for ($i=0; $i -lt 2; $i++) {{ if ($used -notcontains $i) {{ $cnum=$c; $loc=$i; $found=$true; break }} }} if ($found) {{ break }} }}
                }} ""$ctype,$cnum,$loc""";
            var res = await ExecutePowerShellAsync(script);
            var parts = res.Trim().Split(',');
            return parts.Length == 3 ? (parts[0], int.Parse(parts[1]), int.Parse(parts[2])) : ("SCSI", 0, 0);
        }

        private async Task<(bool Success, string Message)> CreateIsoFromDirectoryAsync(string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            // [修复] 完整的 ISO 9660 限制检查
            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists) return (false, "Iso_Error_SourceDirNotFound");

            const long MaxFileSize = 4294967295; // 4GB - 1 byte
            const int MaxFileNameLength = 64;
            const int MaxPathLength = 240;
            const int MaxDirectoryDepth = 8;
            const int MaxVolumeLabelLength = 16;

            string finalVolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? sourceDirInfo.Name : volumeLabel;
            if (finalVolumeLabel.Length > MaxVolumeLabelLength) return (false, "Iso_Error_VolumeLabelTooLong");

            return await Task.Run(() => {
                try
                {
                    // 1. 预检查循环
                    foreach (var item in sourceDirInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        string relativePath = Path.GetRelativePath(sourceDirInfo.FullName, item.FullName);
                        if (item.Name.Length > MaxFileNameLength) return (false, "Iso_Error_FileNameTooLong");
                        if (relativePath.Length > MaxPathLength) return (false, "Iso_Error_PathTooLong");

                        int depth = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
                        if (item is FileInfo)
                        {
                            if (depth - 1 >= MaxDirectoryDepth) return (false, "Iso_Error_FileDepthTooDeep");
                            if (((FileInfo)item).Length >= MaxFileSize) return (false, "Iso_Error_FileTooLarge");
                        }
                        else if (depth > MaxDirectoryDepth) return (false, "Iso_Error_DirectoryDepthTooDeep");
                    }

                    // 2. 创建目录
                    var targetDir = Path.GetDirectoryName(targetIsoPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    // 3. 构建 ISO
                    var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = finalVolumeLabel };
                    foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                        builder.AddFile(Path.GetRelativePath(sourceDirectory, file), file);

                    builder.Build(targetIsoPath);
                    return (true, "Iso_Msg_CreateSuccess");
                }
                catch (Exception ex) { return (false, $"Iso_Error_BuildFailed: {ex.Message}"); }
            });
        }

        private async Task<string> ExecutePowerShellAsync(string script) { try { var res = await Utils.Run2(script); return string.Join("", res); } catch { return ""; } }
        private async Task<(bool Success, string Message)> RunCommandAsync(string script) { try { await Utils.Run2(script); return (true, "Success"); } catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message)); } }
        private class HostDiskInfoCache { public string? Model { get; set; } public string? SerialNumber { get; set; } public double SizeGB { get; set; } }

        #endregion
    }
}