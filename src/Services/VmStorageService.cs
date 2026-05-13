using System.IO;
using System.Text.RegularExpressions;
using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools.Api;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class VmStorageService
    {
        // ============================================================
        // 核心数据查询
        // ============================================================

        /// <summary>
        /// 查询指定虚拟机下的所有控制器、磁盘驱动器及其挂载的介质详情。
        /// 全部走 WmiApi，不再直接持有 CimSession。
        /// </summary>
        public async Task LoadVmStorageItemsAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;

            // ── Step 1: 取 VM 的 VirtualSystemSettingData ────────────
            var settingsResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{WmiApi.Escape(vm.Name)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                ci => ci,
                WmiScope.HyperV);

            if (!settingsResp.HasData) return;
            var settings = settingsResp.Data!;

            // ── Step 2: 关联查询获取资源分配列表（RASD + SASD）────────────────
            // 完美还原旧代码逻辑：顺藤摸瓜只查属于当前虚拟机的资源
            var rasdResp = await WmiApi.QueryRelatedCimAsync(
                settings,
                "Msvm_VirtualSystemSettingDataComponent",
                "Msvm_ResourceAllocationSettingData",
                "GroupComponent",
                "PartComponent",
                ci => ci,
                WmiScope.HyperV);

            var sasdResp = await WmiApi.QueryRelatedCimAsync(
                settings,
                "Msvm_VirtualSystemSettingDataComponent",
                "Msvm_StorageAllocationSettingData",
                "GroupComponent",
                "PartComponent",
                ci => ci,
                WmiScope.HyperV);

            if (!rasdResp.Success || !sasdResp.Success) return;

            var allResources = rasdResp.Data!.Concat(sasdResp.Data!).ToList();

            // ── Step 3: 取 Hyper-V 物理盘映射（懒加载，按需构建）──
            Dictionary<string, int>? hvDiskMap = null;
            Dictionary<int, HostDiskInfoCache>? osDiskMap = null;

            // ── Step 4: 组装存储项（原逻辑不变，仅数据源换为 WmiApi）
            var items = BuildStorageItems(
                allResources,
                ref hvDiskMap,
                ref osDiskMap);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                vm.StorageItems.Clear();
                foreach (var item in items
                    .OrderBy(i => i.ControllerType)
                    .ThenBy(i => i.ControllerNumber)
                    .ThenBy(i => i.ControllerLocation))
                {
                    vm.StorageItems.Add(item);
                }
            });
        }

        // ── 私有：从资源列表组装 VmStorageItem ───────────────────────
        private List<VmStorageItem> BuildStorageItems(
            List<Microsoft.Management.Infrastructure.CimInstance> allResources,
            ref Dictionary<string, int>? hvDiskMap,
            ref Dictionary<int, HostDiskInfoCache>? osDiskMap)
        {
            var resultList = new List<VmStorageItem>();
            var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);
            var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

            // 筛出控制器（ResourceType 5=IDE, 6=SCSI）
            var controllers = allResources
                .Where(res =>
                {
                    int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                    return rt == 5 || rt == 6;
                })
                .OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value)
                .ToList();

            // 按 Parent InstanceID 建子项映射
            var childrenMap = new Dictionary<string, List<Microsoft.Management.Infrastructure.CimInstance>>();
            foreach (var res in allResources)
            {
                var parentPath = res.CimInstanceProperties["Parent"]?.Value?.ToString();
                if (string.IsNullOrEmpty(parentPath)) continue;

                var match = parentRegex.Match(parentPath);
                if (!match.Success) continue;

                string parentId = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!childrenMap.ContainsKey(parentId))
                    childrenMap[parentId] = new List<Microsoft.Management.Infrastructure.CimInstance>();
                childrenMap[parentId].Add(res);
            }

            int scsiCounter = 0, ideCounter = 0;

            foreach (var ctrl in controllers)
            {
                string ctrlId = ctrl.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                int ctrlTypeVal = Convert.ToInt32(ctrl.CimInstanceProperties["ResourceType"]?.Value);
                string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                int ctrlNum = ctrlType == "SCSI" ? scsiCounter++ : ideCounter++;

                if (!childrenMap.TryGetValue(ctrlId, out var slots)) continue;

                foreach (var slot in slots)
                {
                    int resType = Convert.ToInt32(slot.CimInstanceProperties["ResourceType"]?.Value);
                    if (resType != 16 && resType != 17) continue;

                    string address = slot.CimInstanceProperties["AddressOnParent"]?.Value?.ToString() ?? "0";
                    int location = int.TryParse(address, out int loc) ? loc : 0;

                    string slotId = slot.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                    Microsoft.Management.Infrastructure.CimInstance? media = null;
                    if (childrenMap.TryGetValue(slotId, out var mediaList))
                    {
                        media = mediaList.FirstOrDefault(m =>
                        {
                            int t = Convert.ToInt32(m.CimInstanceProperties["ResourceType"]?.Value);
                            return t == 31 || t == 16 || t == 22;
                        });
                    }

                    var driveItem = new VmStorageItem
                    {
                        ControllerType = ctrlType,
                        ControllerNumber = ctrlNum,
                        ControllerLocation = location,
                        DriveType = resType == 16 ? "DvdDrive" : "HardDisk",
                        DiskType = "Empty"
                    };

                    var slotHostRes = slot.CimInstanceProperties["HostResource"]?.Value as string[];
                    var effectiveMedia = media ?? (slotHostRes?.Length > 0 ? slot : null);

                    if (effectiveMedia != null)
                    {
                        var hostRes = effectiveMedia.CimInstanceProperties["HostResource"]?.Value as string[];
                        string rawPath = hostRes?.Length > 0 ? hostRes[0] : "";

                        if (!string.IsNullOrWhiteSpace(rawPath))
                        {
                            bool isPhysicalHardDisk =
                                rawPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) ||
                                rawPath.ToUpper().Contains("PHYSICALDRIVE");

                            bool isPhysicalCdRom =
                                rawPath.Contains("CDROM", StringComparison.OrdinalIgnoreCase) ||
                                rawPath.Contains("Msvm_OpticalDrive", StringComparison.OrdinalIgnoreCase);

                            if (isPhysicalHardDisk)
                            {
                                driveItem.DiskType = "Physical";
                                try
                                {
                                    // 懒加载 Hyper-V 盘 & OS 盘映射
                                    if (hvDiskMap == null)
                                        (hvDiskMap, osDiskMap) = BuildDiskMaps();

                                    var devMatch = deviceIdRegex.Match(rawPath);
                                    int dNum = -1;
                                    if (devMatch.Success)
                                        hvDiskMap.TryGetValue(
                                            devMatch.Groups[1].Value.Replace("\\\\", "\\"), out dNum);
                                    else if (rawPath.ToUpper().Contains("PHYSICALDRIVE"))
                                    {
                                        var numMatch = Regex.Match(rawPath, @"PHYSICALDRIVE(\d+)",
                                            RegexOptions.IgnoreCase);
                                        if (numMatch.Success)
                                            dNum = int.Parse(numMatch.Groups[1].Value);
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
                                        driveItem.DiskSizeGB =
                                            new FileInfo(driveItem.PathOrDiskNumber).Length / 1073741824.0;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    resultList.Add(driveItem);
                }
            }

            return resultList;
        }

        // ── 私有：构建物理盘映射（WmiApi 替换直接 CimSession）────────
        private (Dictionary<string, int> hvDiskMap, Dictionary<int, HostDiskInfoCache> osDiskMap)
            BuildDiskMaps()
        {
            var hvMap = new Dictionary<string, int>();
            var osMap = new Dictionary<int, HostDiskInfoCache>();

            // Hyper-V 虚拟磁盘驱动器 → DriveNumber 映射
            var hvResp = WmiApi.QueryAsync(
                "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive",
                obj =>
                {
                    string did = (WmiApi.PropStr(obj, "DeviceID")).Replace("\\\\", "\\");
                    int dnum = WmiApi.Prop<int>(obj, "DriveNumber", -1);
                    return (did, dnum);
                },
                WmiScope.HyperV).GetAwaiter().GetResult();

            if (hvResp.Success && hvResp.Data != null)
                foreach (var (did, dnum) in hvResp.Data)
                    if (!string.IsNullOrEmpty(did) && dnum >= 0)
                        hvMap[did] = dnum;

            // Win32_DiskDrive → 型号、序列号、大小
            var osResp = WmiApi.QueryAsync(
                "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive",
                obj =>
                {
                    int idx = WmiApi.Prop<int>(obj, "Index", -1);
                    string? model = WmiApi.PropStr(obj, "Model");
                    string? serial = obj["SerialNumber"]?.ToString()?.Trim();
                    long.TryParse(obj["Size"]?.ToString(), out long sizeBytes);
                    return (idx, model, serial, sizeBytes);
                },
                WmiScope.CimV2).GetAwaiter().GetResult();

            if (osResp.Success && osResp.Data != null)
                foreach (var (idx, model, serial, sizeBytes) in osResp.Data)
                    if (idx >= 0)
                        osMap[idx] = new HostDiskInfoCache
                        {
                            Model = model,
                            SerialNumber = serial,
                            SizeGB = Math.Round(sizeBytes / 1073741824.0, 2)
                        };

            return (hvMap, osMap);
        }

        // ============================================================
        // 压缩虚拟磁盘
        // ============================================================

        /// <summary>
        /// 压缩/优化虚拟硬盘（Full 模式）。
        /// 原来依赖 WmiTools.ExecuteMethodAsync，现改用 WmiApi.InvokeAsync。
        /// </summary>
        public async Task<ApiResponse> CompactDiskAsync(string vhdPath)
        {
            return await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_ImageManagementService",
                "CompactVirtualHardDisk",
                p =>
                {
                    p["Path"] = vhdPath;
                    p["Mode"] = 1u; // Full 深度压缩
                },
                WmiScope.HyperV);
        }

        // ============================================================
        // 主机物理磁盘列表
        // ============================================================

        /// <summary>
        /// 获取主机上可用于直通挂载的物理硬盘列表。
        /// 原来混用两个 CimSession，现全部经由 WmiApi。
        /// </summary>
        public async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync()
        {
            // 先取 Hyper-V 已占用的盘号
            var usedResp = await WmiApi.QueryAsync(
                "SELECT DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber >= 0",
                obj => WmiApi.Prop<int>(obj, "DriveNumber", -1),
                WmiScope.HyperV);

            var usedDiskNumbers = new HashSet<int>(
                usedResp.Success && usedResp.Data != null
                    ? usedResp.Data.Where(n => n >= 0)
                    : Enumerable.Empty<int>());

            // 取 Storage 命名空间下的物理盘列表
            var diskResp = await WmiApi.QueryCimAsync(
                "SELECT Number, FriendlyName, Size, IsOffline, IsSystem, IsBoot, BusType, OperationalStatus " +
                "FROM MSFT_Disk",
                ci =>
                {
                    int number = Convert.ToInt32(ci.CimInstanceProperties["Number"]?.Value ?? -1);
                    ushort busType = Convert.ToUInt16(ci.CimInstanceProperties["BusType"]?.Value ?? 0);
                    bool isSystem = Convert.ToBoolean(ci.CimInstanceProperties["IsSystem"]?.Value ?? false);
                    bool isBoot = Convert.ToBoolean(ci.CimInstanceProperties["IsBoot"]?.Value ?? false);
                    bool isOffline = Convert.ToBoolean(ci.CimInstanceProperties["IsOffline"]?.Value ?? false);
                    long sizeBytes = Convert.ToInt64(ci.CimInstanceProperties["Size"]?.Value ?? 0L);
                    string friendlyName = ci.CimInstanceProperties["FriendlyName"]?.Value?.ToString() ?? "";
                    var opArr = ci.CimInstanceProperties["OperationalStatus"]?.Value as ushort[];
                    string opStatus = opArr?.Length > 0 ? opArr[0].ToString() : "Unknown";

                    return new
                    {
                        number,
                        busType,
                        isSystem,
                        isBoot,
                        isOffline,
                        sizeBytes,
                        friendlyName,
                        opStatus
                    };
                },
                WmiScope.Storage);

            if (!diskResp.Success)
                return ApiResponse<List<HostDiskInfo>>.Fail(
                    diskResp.Error, diskResp.Code, diskResp.ErrorSource);

            var result = diskResp.Data!
                .Where(d => d.number >= 0
                         && d.busType != 7     // 排除 USB（BusType 7）
                         && !d.isSystem
                         && !d.isBoot
                         && !usedDiskNumbers.Contains(d.number))
                .Select(d => new HostDiskInfo
                {
                    Number = d.number,
                    FriendlyName = d.friendlyName,
                    SizeGB = Math.Round(d.sizeBytes / 1073741824.0, 2),
                    IsOffline = d.isOffline,
                    IsSystem = d.isSystem,
                    OperationalStatus = d.opStatus
                })
                .ToList();

            return ApiResponse<List<HostDiskInfo>>.Ok(result);
        }

        // ============================================================
        // 刷新虚拟磁盘文件大小（纯文件系统操作，无需修改）
        // ============================================================

        public async Task RefreshVirtualDiskSizesAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;

            await Task.Run(() =>
            {
                foreach (var item in vm.StorageItems.Where(i => i.DiskType == "Virtual"))
                {
                    try
                    {
                        if (!File.Exists(item.PathOrDiskNumber)) continue;
                        double sizeGb = new FileInfo(item.PathOrDiskNumber).Length / 1073741824.0;
                        if (Math.Abs(item.DiskSizeGB - sizeGb) > 0.001)
                            System.Windows.Application.Current.Dispatcher.Invoke(
                                () => item.DiskSizeGB = sizeGb);
                    }
                    catch { }
                }

                foreach (var disk in vm.Disks.Where(d => d.DiskType != "Physical"))
                {
                    try
                    {
                        if (!File.Exists(disk.Path)) continue;
                        long sizeBytes = new FileInfo(disk.Path).Length;
                        if (disk.CurrentSize != sizeBytes)
                            System.Windows.Application.Current.Dispatcher.Invoke(
                                () => disk.CurrentSize = sizeBytes);
                    }
                    catch { }
                }
            });
        }

        // ============================================================
        // 槽位检测（暂留 PowerShell，待 WMI 重写确认后替换）
        // ============================================================

        public async Task<(string ControllerType, int ControllerNumber, int Location)>
            GetNextAvailableSlotAsync(string vmName, string driveType)
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
        for ($i=0; $i -lt 64; $i++) {{ if ($used -notcontains $i) {{ $ctype='SCSI'; $cnum=$cn; $loc=$i; $found=$true; break }} }}
        if ($found) {{ break }}
    }}
}}
""$ctype,$cnum,$loc""";

            try
            {
                var res = await ExecutePowerShellAsync(script);
                var parts = res.Trim().Split(',');
                if (parts.Length == 3 && parts[0] != "NONE")
                    return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
            }
            catch { }

            return ("NONE", -1, -1);
        }

        // ============================================================
        // 设备增删改操作（暂留 PowerShell，WMI 迁移待后续专项处理）
        // ============================================================

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)>
            AddDriveAsync(
                string vmName, string controllerType, int controllerNumber, int location, string driveType,
                string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256,
                string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default",
                string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";

            if (driveType == "DvdDrive" && isNew && !string.IsNullOrWhiteSpace(isoSourcePath))
            {
                var createResult = await CreateIsoFromDirectoryAsync(isoSourcePath, pathOrNumber, isoVolumeLabel);
                if (!createResult.Success)
                    return (false, createResult.Message, controllerType, controllerNumber, location);
            }

            string script = $@"
$ErrorActionPreference = 'Stop'
$vmName = '{vmName}'; $v = Get-VM -Name $vmName
$targetDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction SilentlyContinue
$targetDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} | Where-Object {{ $_.ControllerType -eq '{controllerType}' }}
if ($targetDisk -or $targetDvd) {{ throw 'Storage_Error_SlotOccupied' }}

$oldDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction SilentlyContinue
$oldDvd  = Get-VMDvdDrive -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} | Where-Object {{ $_.ControllerType -eq '{controllerType}' }}

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
    if ($oldDvd)  {{ Remove-VMDvdDrive     -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
    if ('{isNew.ToString().ToLower()}' -eq 'true') {{
        $vhdParams = @{{ Path={psPath}; SizeBytes={sizeGb}GB; {vhdType}=$true; ErrorAction='Stop' }}
        if ('{sectorFormat}' -eq '512n') {{ $vhdParams.LogicalSectorSizeBytes=512;  $vhdParams.PhysicalSectorSizeBytes=512  }}
        elseif ('{sectorFormat}' -eq '512e') {{ $vhdParams.LogicalSectorSizeBytes=512;  $vhdParams.PhysicalSectorSizeBytes=4096 }}
        elseif ('{sectorFormat}' -eq '4kn')  {{ $vhdParams.LogicalSectorSizeBytes=4096; $vhdParams.PhysicalSectorSizeBytes=4096 }}
        if ('{blockSize}' -ne 'Default') {{ $vhdParams.BlockSizeBytes='{blockSize}' }}
        if ('{vhdType}' -eq 'Differencing') {{ $vhdParams.Remove('SizeBytes'); $vhdParams.Remove('Dynamic'); $vhdParams.Remove('Fixed'); $vhdParams.ParentPath='{parentPath}' }}
        New-VHD @vhdParams
    }}
    $p = @{{ VMName=$vmName; ControllerType='{controllerType}'; ControllerNumber={controllerNumber}; ControllerLocation={location}; ErrorAction='Stop' }}
    if ('{isPhysical.ToString().ToLower()}' -eq 'true') {{ $p.DiskNumber={psPath} }} else {{ $p.Path={psPath} }}
    Add-VMHardDiskDrive @p
}} else {{
    if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName $vmName -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
    if ($oldDvd)  {{ Set-VMDvdDrive  -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path {psPath} -ErrorAction Stop }}
    else          {{ Add-VMDvdDrive  -VMName $vmName -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path {psPath} -ErrorAction Stop }}
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
            catch (Exception ex)
            {
                return (false, Utils.GetFriendlyErrorMessage(ex.Message),
                    controllerType, controllerNumber, location);
            }
        }

        public async Task<(bool Success, string Message)> RemoveDriveAsync(
            string vmName, VmStorageItem drive)
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
    }} else {{
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

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(
            string vmName, int controllerNumber, int controllerLocation, string newIsoPath)
            => await RunCommandAsync(
                $"Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} " +
                $"-ControllerLocation {controllerLocation} " +
                $"-Path {(string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'")} -ErrorAction Stop");

        public async Task<(bool Success, string Message)> ModifyHardDrivePathAsync(
            string vmName, string controllerType, int controllerNumber, int controllerLocation, string newPath)
        {
            string psPath = string.IsNullOrWhiteSpace(newPath) ? "$null" : $"'{newPath}'";
            string script = $@"
$ErrorActionPreference = 'Stop'
$vm = Get-VM -Name '{vmName}'
if ($vm.State -eq 'Running') {{
    Remove-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -ErrorAction Stop
    Add-VMHardDiskDrive    -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {psPath} -ErrorAction Stop
}} else {{
    Set-VMHardDiskDrive    -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {psPath} -ErrorAction Stop
}}";
            return await RunCommandAsync(script);
        }

        // ============================================================
        // 主机物理磁盘控制
        // ============================================================

        /// <summary>
        /// 设置宿主机物理硬盘的脱机/联机状态。
        /// 原来走 PowerShell Set-Disk，现改用 WmiApi.InvokeCimMethodAsync。
        /// </summary>
        public async Task<ApiResponse> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            // 先取 MSFT_Disk 实例
            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                ci => ci,
                WmiScope.Storage);

            if (!diskResp.HasData)
                return ApiResponse.Fail($"Disk {diskNumber} not found", -1, ApiErrorSource.Wmi);

            // Offline → 调 Offline()；Online → 调 Online()
            string methodName = isOffline ? "Offline" : "Online";

            return await WmiApi.InvokeCimMethodAsync(
                diskResp.Data!,
                methodName,
                WmiScope.Storage);
        }

        // ============================================================
        // ISO 镜像生成（纯本地逻辑，无需修改）
        // ============================================================

        private async Task<(bool Success, string Message)> CreateIsoFromDirectoryAsync(
            string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists) return (false, "Iso_Error_SourceDirNotFound");

            string finalVolumeLabel = string.IsNullOrWhiteSpace(volumeLabel)
                ? sourceDirInfo.Name : volumeLabel;
            finalVolumeLabel = Regex.Replace(finalVolumeLabel, @"[^A-Za-z0-9_\- ]", "_");
            if (string.IsNullOrEmpty(finalVolumeLabel)) finalVolumeLabel = "NewISO";

            return await Task.Run(() =>
            {
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
        // 底层辅助
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

        private class HostDiskInfoCache
        {
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGB { get; set; }
        }
    }
}