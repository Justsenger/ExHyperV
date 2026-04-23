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

        // ============================================================
        // 核心数据查询：获取虚拟机和主机的存储设备状态
        // ============================================================

        // 查询指定虚拟机下的所有控制器、磁盘驱动器及其挂载的介质详情
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
                                                    if (hvDiskMap == null)
                                                    {
                                                        hvDiskMap = new Dictionary<string, int>();
                                                        var allHvDisks = session.QueryInstances(NamespaceV2, "WQL", "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive");
                                                        foreach (var d in allHvDisks)
                                                        {
                                                            string did = d.CimInstanceProperties["DeviceID"]?.Value?.ToString() ?? "";
                                                            if (!string.IsNullOrEmpty(did) && d.CimInstanceProperties["DriveNumber"]?.Value != null)
                                                            {
                                                                did = did.Replace("\\\\", "\\");
                                                                int dnum = Convert.ToInt32(d.CimInstanceProperties["DriveNumber"].Value);
                                                                hvDiskMap[did] = dnum;
                                                            }
                                                        }

                                                        osDiskMap = new Dictionary<int, HostDiskInfoCache>();
                                                        var allOsDisks = session.QueryInstances(NamespaceCimV2, "WQL", "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive");
                                                        foreach (var d in allOsDisks)
                                                        {
                                                            if (d.CimInstanceProperties["Index"]?.Value != null)
                                                            {
                                                                int idx = Convert.ToInt32(d.CimInstanceProperties["Index"].Value);
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

        // 压缩/优化磁盘的方法。

        public async Task<(bool Success, string Message)> CompactDiskAsync(string vhdPath)
        {
            try
            {
                // 构造输入参数
                var inParams = new Dictionary<string, object>
        {
            { "Path", vhdPath },
            { "Mode", 1u } // 1u 表示 Full 深度压缩模式
        };

                // 调用 Msvm_ImageManagementService 的 CompactVirtualHardDisk 方法
                // WmiTools.ExecuteMethodAsync 内部会处理 Job (4096) 并等待其完成
                return await WmiTools.ExecuteMethodAsync(
                    "SELECT * FROM Msvm_ImageManagementService",
                    "CompactVirtualHardDisk",
                    inParams
                );
            }
            catch (Exception ex)
            {
                return (false, $"WMI 执行异常: {ex.Message}");
            }
        }


        // 获取主机上可用于直通挂载的物理硬盘列表（排除系统盘及已占用的磁盘）
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

        // 轻量级的方法，获取虚拟磁盘文件的实时大小
        public async Task RefreshVirtualDiskSizesAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;

            await Task.Run(() =>
            {
                // 1. 刷新 StorageItems 集合 (用于设置页面，单位 GB)
                foreach (var item in vm.StorageItems.Where(i => i.DiskType == "Virtual"))
                {
                    try
                    {
                        if (File.Exists(item.PathOrDiskNumber))
                        {
                            double sizeGb = (double)new FileInfo(item.PathOrDiskNumber).Length / 1073741824.0;
                            if (Math.Abs(item.DiskSizeGB - sizeGb) > 0.001)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => item.DiskSizeGB = sizeGb);
                            }
                        }
                    }
                    catch { }
                }

                // 2. 同时刷新 Disks 集合 (用于卡片/仪表盘，单位 Bytes)
                foreach (var disk in vm.Disks.Where(d => d.DiskType != "Physical"))
                {
                    try
                    {
                        if (File.Exists(disk.Path))
                        {
                            long sizeBytes = new FileInfo(disk.Path).Length;
                            if (disk.CurrentSize != sizeBytes)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() => disk.CurrentSize = sizeBytes);
                            }
                        }
                    }
                    catch { }
                }
            });
        }
        // ============================================================
        // 槽位检测：寻找可用的控制器接口
        // ============================================================

        // 自动探测虚拟机存储控制器上第一个未被占用的空闲位置
        public async Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            string script = $@"
    $v = Get-VM -Name '{vmName}'; $ctype = 'NONE'; $cnum = -1; $loc = -1; $found = $false
    if ($v.Generation -eq 1 -and $v.State -ne 'Running') {{
        for ($c=0; $c -lt 2; $c++) {{ 
            $h_loc = @((Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation)
            $d_loc = @((Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation)
            $used = $h_loc + $d_loc
            for ($i=0; $i -lt 2; $i++) {{ if ($used -notcontains $i) {{ $ctype='IDE'; $cnum=$c; $loc=$i; $found=$true; break }} }} 
            if ($found) {{ break }} 
        }}
    }}
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

            return ("NONE", -1, -1);
        }

        // ============================================================
        // 设备增删改操作：通过 PowerShell 脚本进行虚拟机配置变更
        // ============================================================

        // 向虚拟机添加硬盘或光驱设备（支持新建 VHD、物理直通以及 ISO 生成）
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

        // 从虚拟机移除存储设备，物理硬盘移除后会自动尝试恢复主机联机状态
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

        // 修改光驱挂载的 ISO 文件路径
        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, int controllerNumber, int controllerLocation, string newIsoPath)
            => await RunCommandAsync($"Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {(string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'")} -ErrorAction Stop");

        // 修改虚拟硬盘挂载的 VHD/VHDX 文件路径
        public async Task<(bool Success, string Message)> ModifyHardDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newPath)
        {
            string psPath = string.IsNullOrWhiteSpace(newPath) ? "$null" : $"'{newPath}'";

            // 核心逻辑：如果是运行中的虚拟机，采用“先删再加”策略，这是实现 SCSI 热交换(Hot-Swap)的唯一可靠方式
            string script = $@"
        $ErrorActionPreference = 'Stop'
        $vm = Get-VM -Name '{vmName}'
        if ($vm.State -eq 'Running') {{
            Remove-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -ErrorAction Stop
            Add-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {psPath} -ErrorAction Stop
        }} else {{
            Set-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {psPath} -ErrorAction Stop
        }}";

            return await RunCommandAsync(script);
        }

        // ============================================================
        // 主机物理磁盘控制
        // ============================================================

        // 设置宿主机物理硬盘的脱机/联机状态（物理直通必须先脱机）
        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
            => await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline ${isOffline.ToString().ToLower()}");

        // ============================================================
        // ISO 镜像生成：将本地目录打包为标准镜像
        // ============================================================

        // 使用 DiscUtils 库将指定的本地文件夹打包成符合 ISO 9660 / Joliet 标准的镜像文件
        private async Task<(bool Success, string Message)> CreateIsoFromDirectoryAsync(string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists) return (false, "Iso_Error_SourceDirNotFound");

            const long MaxFileSize = 4294967295;
            const int MaxFileNameLength = 103;
            const int MaxPathLength = 240;
            const int MaxDirectoryDepth = 8;
            const int MaxVolumeLabelLength = 31;
            const long MaxTotalSize = 8796093022208;

            string finalVolumeLabel = string.IsNullOrWhiteSpace(volumeLabel)
                ? sourceDirInfo.Name
                : volumeLabel;

            if (finalVolumeLabel.Length > MaxVolumeLabelLength)
                return (false, "Iso_Error_VolumeLabelTooLong");

            finalVolumeLabel = Regex.Replace(finalVolumeLabel, @"[^A-Za-z0-9_\- ]", "_");
            if (string.IsNullOrEmpty(finalVolumeLabel))
                finalVolumeLabel = "NewISO";

            return await Task.Run(() => {
                try
                {
                    var allItems = Directory.EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.AllDirectories).ToList();

                    if (allItems.Count == 0)
                        return (false, "Iso_Error_SourceDirEmpty");

                    long totalSize = 0;

                    foreach (var item in allItems)
                    {
                        string relativePath = Path.GetRelativePath(sourceDirInfo.FullName, item);
                        string fileName = Path.GetFileName(item);

                        if (fileName.Length > MaxFileNameLength)
                            return (false, $"Iso_Error_FileNameTooLong");

                        if (relativePath.Length > MaxPathLength)
                            return (false, $"Iso_Error_PathTooLong");

                        int depth = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

                        if (File.Exists(item))
                        {
                            var fileInfo = new FileInfo(item);
                            if (depth - 1 >= MaxDirectoryDepth)
                                return (false, "Iso_Error_FileDepthTooDeep");
                            if (fileInfo.Length >= MaxFileSize)
                                return (false, "Iso_Error_FileTooLarge");

                            totalSize += fileInfo.Length;
                            if (totalSize >= MaxTotalSize)
                                return (false, "Iso_Error_TotalSizeTooLarge");
                        }
                        else if (Directory.Exists(item))
                        {
                            if (depth > MaxDirectoryDepth)
                                return (false, "Iso_Error_DirectoryDepthTooDeep");
                        }
                    }

                    var targetDir = Path.GetDirectoryName(targetIsoPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    var builder = new CDBuilder
                    {
                        UseJoliet = true,
                        VolumeIdentifier = finalVolumeLabel
                    };

                    foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
                    {
                        builder.AddFile(Path.GetRelativePath(sourceDirectory, file), file);
                    }

                    builder.Build(targetIsoPath);
                    return (true, "Iso_Msg_CreateSuccess");
                }
                catch (Exception ex)
                {
                    return (false, $"Iso_Error_BuildFailed: {ex.Message}");
                }
            });
        }

        // ============================================================
        // 底层辅助工具：PowerShell 执行与脚本封装
        // ============================================================

        // 执行 PowerShell 脚本并返回合并后的字符串结果
        private async Task<string> ExecutePowerShellAsync(string script)
        {
            try
            {
                var res = await Utils.Run2(script);
                return res == null ? "" : string.Join(Environment.NewLine, res.Select(r => r?.ToString() ?? ""));
            }
            catch { return ""; }
        }

        // 通用的 PowerShell 命令执行包装器，仅返回成功与否的状态
        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            try { await Utils.Run2(script); return (true, "Storage_Msg_Success"); }
            catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message)); }
        }

        // ============================================================
        // 内部辅助数据模型
        // ============================================================

        // 用于在加载过程中缓存宿主机物理硬盘元数据的信息类
        private class HostDiskInfoCache
        {
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGB { get; set; }
        }
    }
}