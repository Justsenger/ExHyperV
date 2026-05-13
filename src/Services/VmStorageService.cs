using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Tools.Api;

namespace ExHyperV.Services
{
    public class VmStorageService
    {
        // ============================================================
        // 核心数据查询：获取虚拟机和主机的存储设备状态
        // ============================================================

        // 查询指定虚拟机下的所有控制器、磁盘驱动器及其挂载的介质详情
        public async Task LoadVmStorageItemsAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;

            var items = await Task.Run(async () =>
            {
                var resultList = new List<VmStorageItem>();

                try
                {
                    using var vmObj = WmiApi.GetVmComputerSystem(vm.Name);
                    if (vmObj == null) return resultList;

                    using var settings = WmiApi.GetVmSettings(vmObj);
                    if (settings == null) return resultList;

                    var rasdResp = await WmiApi.QueryRelatedAsync(
                        settings,
                        "Msvm_ResourceAllocationSettingData",
                        obj => new StorageResource(
                            obj["InstanceID"]?.ToString() ?? "",
                            Convert.ToInt32(obj["ResourceType"] ?? 0),
                            obj["Parent"]?.ToString() ?? "",
                            obj["AddressOnParent"]?.ToString() ?? "0",
                            obj["HostResource"] as string[]),
                        "Msvm_VirtualSystemSettingDataComponent");

                    var sasdResp = await WmiApi.QueryRelatedAsync(
                        settings,
                        "Msvm_StorageAllocationSettingData",
                        obj => new StorageResource(
                            obj["InstanceID"]?.ToString() ?? "",
                            Convert.ToInt32(obj["ResourceType"] ?? 0),
                            obj["Parent"]?.ToString() ?? "",
                            "",
                            obj["HostResource"] as string[]),
                        "Msvm_VirtualSystemSettingDataComponent");

                    if (!rasdResp.Success || !sasdResp.Success) return resultList;

                    var allResources = new List<StorageResource>(
                        (rasdResp.Data?.Count ?? 0) + (sasdResp.Data?.Count ?? 0));
                    if (rasdResp.Data != null) allResources.AddRange(rasdResp.Data);
                    if (sasdResp.Data != null) allResources.AddRange(sasdResp.Data);

                    var controllers = allResources
                        .Where(r => r.ResourceType == 5 || r.ResourceType == 6)
                        .OrderBy(r => r.ResourceType)
                        .ToList();

                    var childrenMap = new Dictionary<string, List<StorageResource>>();
                    var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);

                    foreach (var res in allResources)
                    {
                        if (string.IsNullOrEmpty(res.Parent)) continue;
                        var match = parentRegex.Match(res.Parent);
                        if (!match.Success) continue;
                        string parentId = match.Groups[1].Value.Replace("\\\\", "\\");
                        if (!childrenMap.ContainsKey(parentId)) childrenMap[parentId] = new List<StorageResource>();
                        childrenMap[parentId].Add(res);
                    }

                    Dictionary<string, int>? hvDiskMap = null;
                    Dictionary<int, HostDiskInfoCache>? osDiskMap = null;
                    var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

                    int scsiCounter = 0;
                    int ideCounter = 0;

                    foreach (var ctrl in controllers)
                    {
                        string ctrlType = ctrl.ResourceType == 6 ? "SCSI" : "IDE";
                        int ctrlNum = ctrlType == "SCSI" ? scsiCounter++ : ideCounter++;

                        if (!childrenMap.TryGetValue(ctrl.InstanceId, out var slots)) continue;

                        foreach (var slot in slots)
                        {
                            if (slot.ResourceType != 16 && slot.ResourceType != 17) continue;

                            int location = int.TryParse(slot.AddressOnParent, out int loc) ? loc : 0;

                            StorageResource? media = null;
                            if (childrenMap.TryGetValue(slot.InstanceId, out var mediaList))
                                media = mediaList.FirstOrDefault(m => m.ResourceType == 31 || m.ResourceType == 16 || m.ResourceType == 22);

                            var driveItem = new VmStorageItem
                            {
                                ControllerType = ctrlType,
                                ControllerNumber = ctrlNum,
                                ControllerLocation = location,
                                DriveType = slot.ResourceType == 16 ? "DvdDrive" : "HardDisk",
                                DiskType = "Empty"
                            };

                            var effectiveMedia = media ?? (slot.HostResource?.Length > 0 ? slot : null);

                            if (effectiveMedia != null)
                            {
                                string rawPath = effectiveMedia.HostResource?.Length > 0 ? effectiveMedia.HostResource[0] : "";

                                if (!string.IsNullOrWhiteSpace(rawPath))
                                {
                                    bool isPhysicalHardDisk = rawPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) ||
                                                              rawPath.Contains("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase);
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
                                                var hvResp = await WmiApi.QueryAsync<(string DeviceId, int DriveNumber)>(
                                                    "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber IS NOT NULL",
                                                    obj =>
                                                    {
                                                        string did = obj["DeviceID"]?.ToString()?.Replace("\\\\", "\\") ?? "";
                                                        return (did, Convert.ToInt32(obj["DriveNumber"]));
                                                    });
                                                if (hvResp.Success && hvResp.Data != null)
                                                    foreach (var (did, dnum) in hvResp.Data)
                                                        if (!string.IsNullOrEmpty(did)) hvDiskMap[did] = dnum;

                                                osDiskMap = new Dictionary<int, HostDiskInfoCache>();
                                                var osResp = await WmiApi.QueryAsync<HostDiskInfoCache>(
                                                    "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive",
                                                    obj =>
                                                    {
                                                        int idx = Convert.ToInt32(obj["Index"]);
                                                        long.TryParse(obj["Size"]?.ToString(), out long sizeBytes);
                                                        return new HostDiskInfoCache
                                                        {
                                                            Index = idx,
                                                            Model = obj["Model"]?.ToString(),
                                                            SerialNumber = obj["SerialNumber"]?.ToString()?.Trim(),
                                                            SizeGB = Math.Round(sizeBytes / 1073741824.0, 2)
                                                        };
                                                    },
                                                    WmiScope.CimV2);
                                                if (osResp.Success && osResp.Data != null)
                                                    foreach (var d in osResp.Data) osDiskMap[d.Index] = d;
                                            }

                                            var devMatch = deviceIdRegex.Match(rawPath);
                                            int dNum = -1;
                                            if (devMatch.Success)
                                                hvDiskMap.TryGetValue(devMatch.Groups[1].Value.Replace("\\\\", "\\"), out dNum);
                                            else if (rawPath.Contains("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase))
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
                                            try { driveItem.DiskSizeGB = new FileInfo(driveItem.PathOrDiskNumber).Length / 1073741824.0; }
                                            catch { }
                                        }
                                    }
                                }
                            }

                            resultList.Add(driveItem);
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
            var result = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_ImageManagementService",
                "CompactVirtualHardDisk",
                p => { p["Path"] = vhdPath; p["Mode"] = (ushort)1; });

            return result.Success
                ? (true, string.Empty)
                : (false, string.Format(Properties.Resources.VmStorageService_ErrWmiException, result.Error));
        }

        // 获取主机上可用于直通挂载的物理硬盘列表（排除系统盘及已占用的磁盘）
        public async Task<List<HostDiskInfo>> GetHostDisksAsync()
        {
            var usedResp = await WmiApi.QueryAsync<int>(
                "SELECT DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber IS NOT NULL",
                obj => Convert.ToInt32(obj["DriveNumber"]));

            var usedDiskNumbers = new HashSet<int>(usedResp.Data ?? []);

            var disksResp = await WmiApi.QueryCimAsync<HostDiskInfo>(
                "SELECT Number, FriendlyName, Size, IsOffline, IsSystem, IsBoot, BusType, OperationalStatus FROM MSFT_Disk WHERE BusType <> 7 AND IsSystem = FALSE AND IsBoot = FALSE",
                inst =>
                {
                    int number = Convert.ToInt32(inst.CimInstanceProperties["Number"]?.Value ?? -1);
                    bool isSystem = Convert.ToBoolean(inst.CimInstanceProperties["IsSystem"]?.Value ?? false);
                    var opStatusArr = inst.CimInstanceProperties["OperationalStatus"]?.Value as ushort[];
                    return new HostDiskInfo
                    {
                        Number = number,
                        FriendlyName = inst.CimInstanceProperties["FriendlyName"]?.Value?.ToString() ?? string.Empty,
                        SizeGB = Math.Round(Convert.ToInt64(inst.CimInstanceProperties["Size"]?.Value ?? 0L) / 1073741824.0, 2),
                        IsOffline = Convert.ToBoolean(inst.CimInstanceProperties["IsOffline"]?.Value ?? false),
                        IsSystem = isSystem,
                        OperationalStatus = opStatusArr?.Length > 0 ? opStatusArr[0].ToString() : "Unknown"
                    };
                },
                WmiScope.Storage);

            return disksResp.Data?
                .Where(d => d.Number >= 0 && !usedDiskNumbers.Contains(d.Number))
                .ToList() ?? [];
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
                                System.Windows.Application.Current.Dispatcher.Invoke(() => item.DiskSizeGB = sizeGb);
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
                                System.Windows.Application.Current.Dispatcher.Invoke(() => disk.CurrentSize = sizeBytes);
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

            // 核心逻辑：如果是运行中的虚拟机，采用"先删再加"策略，这是实现 SCSI 热交换(Hot-Swap)的唯一可靠方式
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

        private async Task<(bool Success, string Message)> CreateIsoFromDirectoryAsync(string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists) return (false, "Iso_Error_SourceDirNotFound");

            string finalVolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? sourceDirInfo.Name : volumeLabel;
            finalVolumeLabel = Regex.Replace(finalVolumeLabel, @"[^A-Za-z0-9_\- ]", "_");
            if (string.IsNullOrEmpty(finalVolumeLabel)) finalVolumeLabel = "NewISO";

            return await Task.Run(() => {
                try
                {
                    var targetDir = Path.GetDirectoryName(targetIsoPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    ExHyperV.Tools.ImapiIsoTool.BuildUdfIso(sourceDirectory, targetIsoPath, finalVolumeLabel);
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

        private async Task<string> ExecutePowerShellAsync(string script)
        {
            try
            {
                var res = await Utils.Run2(script);
                return res == null ? "" : string.Join(Environment.NewLine, res.Select(r => r?.ToString() ?? ""));
            }
            catch { return ""; }
        }

        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            try { await Utils.Run2(script); return (true, "Storage_Msg_Success"); }
            catch (Exception ex) { return (false, Utils.GetFriendlyErrorMessage(ex.Message)); }
        }

        // ============================================================
        // 内部辅助数据模型
        // ============================================================

        private record StorageResource(
            string InstanceId,
            int ResourceType,
            string Parent,
            string AddressOnParent,
            string[]? HostResource);

        private class HostDiskInfoCache
        {
            public int Index { get; set; }
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGB { get; set; }
        }
    }
}