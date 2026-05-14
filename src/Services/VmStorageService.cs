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

        public async Task LoadVmStorageItemsAsync(VmInstanceInfo vm)
        {
            if (vm == null) return;

            var settingsResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{WmiApi.Escape(vm.Name)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                ci => ci,
                WmiScope.HyperV);

            if (!settingsResp.HasData) return;
            var settings = settingsResp.Data!;

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

            Dictionary<string, int>? hvDiskMap = null;
            Dictionary<int, HostDiskInfoCache>? osDiskMap = null;

            var items = BuildStorageItems(allResources, ref hvDiskMap, ref osDiskMap);

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

        private List<VmStorageItem> BuildStorageItems(
            List<Microsoft.Management.Infrastructure.CimInstance> allResources,
            ref Dictionary<string, int>? hvDiskMap,
            ref Dictionary<int, HostDiskInfoCache>? osDiskMap)
        {
            var resultList = new List<VmStorageItem>();
            var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);
            var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

            var controllers = allResources
                .Where(res =>
                {
                    int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                    return rt == 5 || rt == 6;
                })
                .OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value)
                .ToList();

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

        private (Dictionary<string, int> hvDiskMap, Dictionary<int, HostDiskInfoCache> osDiskMap)
            BuildDiskMaps()
        {
            var hvMap = new Dictionary<string, int>();
            var osMap = new Dictionary<int, HostDiskInfoCache>();

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

        public async Task<ApiResponse> CompactDiskAsync(string vhdPath)
        {
            return await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_ImageManagementService",
                "CompactVirtualHardDisk",
                p =>
                {
                    p["Path"] = vhdPath;
                    p["Mode"] = 1u;
                },
                WmiScope.HyperV);
        }

        // ============================================================
        // 主机物理磁盘列表
        // ============================================================

        public async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync()
        {
            var usedResp = await WmiApi.QueryAsync(
                "SELECT DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber >= 0",
                obj => WmiApi.Prop<int>(obj, "DriveNumber", -1),
                WmiScope.HyperV);

            var usedDiskNumbers = new HashSet<int>(
                usedResp.Success && usedResp.Data != null
                    ? usedResp.Data.Where(n => n >= 0)
                    : Enumerable.Empty<int>());

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
                    return new { number, busType, isSystem, isBoot, isOffline, sizeBytes, friendlyName, opStatus };
                },
                WmiScope.Storage);

            if (!diskResp.Success)
                return ApiResponse<List<HostDiskInfo>>.Fail(
                    diskResp.Error, diskResp.Code, diskResp.ErrorSource);

            var result = diskResp.Data!
                .Where(d => d.number >= 0
                         && d.busType != 7
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
        // 刷新虚拟磁盘文件大小
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
        // 槽位检测（已重构为原生 WMI）
        // ============================================================

        public async Task<(string ControllerType, int ControllerNumber, int Location)>
            GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            var vmResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                ci => ci,
                WmiScope.HyperV);

            if (!vmResp.HasData) return ("NONE", -1, -1);
            int state = Convert.ToInt32(vmResp.Data!.CimInstanceProperties["EnabledState"]?.Value ?? 0);
            bool isRunning = (state == 2);

            var settingsResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT InstanceID, VirtualSystemSubType FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                ci => ci,
                WmiScope.HyperV);

            if (!settingsResp.HasData) return ("NONE", -1, -1);
            var settings = settingsResp.Data!;
            string subType = settings.CimInstanceProperties["VirtualSystemSubType"]?.Value?.ToString() ?? "";
            bool isGen1 = subType == "Microsoft:Hyper-V:SubType:1";
            string settingId = settings.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";

            var rasdResp = await WmiApi.QueryCimAsync(
                $"SELECT ResourceType, Address, AddressOnParent, InstanceID, Parent " +
                $"FROM Msvm_ResourceAllocationSettingData " +
                $"WHERE InstanceID LIKE '{WmiApi.Escape(settingId)}%' " +
                $"AND (ResourceType = 5 OR ResourceType = 6 OR ResourceType = 16 OR ResourceType = 17)",
                ci => ci,
                WmiScope.HyperV);

            if (!rasdResp.HasData) return ("NONE", -1, -1);

            var controllers = new List<(string Type, int Number, string InstanceID)>();
            var occupiedSlots = new HashSet<string>();
            var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var res in rasdResp.Data!)
            {
                int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0);
                string instanceId = res.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";

                if (rt == 5 || rt == 6)
                {
                    string type = rt == 5 ? "IDE" : "SCSI";
                    int number = Convert.ToInt32(res.CimInstanceProperties["Address"]?.Value ?? 0);
                    controllers.Add((type, number, instanceId));
                }
                else if (rt == 16 || rt == 17)
                {
                    string parentPath = res.CimInstanceProperties["Parent"]?.Value?.ToString() ?? "";
                    string addressOnParent = res.CimInstanceProperties["AddressOnParent"]?.Value?.ToString() ?? "";

                    string parentId = parentPath;
                    var match = parentRegex.Match(parentPath);
                    if (match.Success)
                        parentId = match.Groups[1].Value.Replace("\\\\", "\\");

                    if (int.TryParse(addressOnParent, out int location))
                        occupiedSlots.Add($"{parentId}|{location}");
                }
            }

            if (isGen1 && !isRunning)
            {
                foreach (var ctrl in controllers.Where(c => c.Type == "IDE").OrderBy(c => c.Number))
                {
                    for (int i = 0; i < 2; i++)
                        if (!occupiedSlots.Contains($"{ctrl.InstanceID}|{i}"))
                            return ("IDE", ctrl.Number, i);
                }
            }

            foreach (var ctrl in controllers.Where(c => c.Type == "SCSI").OrderBy(c => c.Number))
            {
                for (int i = 0; i < 64; i++)
                    if (!occupiedSlots.Contains($"{ctrl.InstanceID}|{i}"))
                        return ("SCSI", ctrl.Number, i);
            }

            return ("NONE", -1, -1);
        }

        // ============================================================
        // ============================================================
        // 设备增删改操作
        // ============================================================

        /// <summary>
        /// 向虚拟机添加存储设备。
        ///
        /// WMI 调用链（实测确认）：
        ///   1. ISO 生成（可选）
        ///   2. 取 VM 状态 + settings
        ///   3. 槽位冲突检测：查 RASD InstanceID 末尾 \ctrlNum\loc\D
        ///   4. 运行状态校验：IDE 运行中只允许 DvdDrive 热插
        ///   5. SCSI 控制器不足时补充：AddResourceSettings + SCSI RASD XML
        ///   6. 添加槽位 RASD：AddResourceSettings，返回槽位 __PATH
        ///   7. 添加介质 SASD（有路径时）：AddResourceSettings，Parent=槽位路径
        ///
        /// 物理直通盘 vs 虚拟盘的差异（PS cmdlet 隐藏的细节）：
        ///   虚拟盘槽位 ResourceSubType = "Microsoft:Hyper-V:Synthetic Disk Drive"
        ///   物理直通槽位 ResourceSubType = "Microsoft:Hyper-V:Physical Disk Drive"
        ///   介质层 ResourceSubType = "Microsoft:Hyper-V:Virtual Hard Disk"（虚拟）
        ///                           物理直通的 HostResource 直接指向 Msvm_DiskDrive 路径
        /// </summary>
        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)>
            AddDriveAsync(
                string vmName, string controllerType, int controllerNumber, int location, string driveType,
                string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256,
                string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default",
                string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            // ── Step 0: ISO 生成（DvdDrive + isNew + 有源目录）─────────
            if (driveType == "DvdDrive" && isNew && !string.IsNullOrWhiteSpace(isoSourcePath))
            {
                var isoResult = await CreateIsoFromDirectoryAsync(isoSourcePath, pathOrNumber, isoVolumeLabel);
                if (!isoResult.Success)
                    return (false, isoResult.Message, controllerType, controllerNumber, location);
            }

            try
            {
                // ── Step 1: 取 VM 对象、状态、settings ────────────────
                using var vmObj = WmiApi.GetVmComputerSystem(vmName);
                if (vmObj == null)
                    return (false, $"VM '{vmName}' not found", controllerType, controllerNumber, location);

                int enabledState = WmiApi.Prop<int>(vmObj, "EnabledState", 0);
                bool isRunning = enabledState == 2;

                using var settings = WmiApi.GetVmSettings(vmObj);
                if (settings == null)
                    return (false, "Cannot get VM settings", controllerType, controllerNumber, location);

                // ── Step 3: 运行状态校验 ──────────────────────────────
                if (controllerType == "IDE" && isRunning && driveType != "DvdDrive")
                    return (false, "Storage_Error_IdeHotPlugNotSupported", controllerType, controllerNumber, location);

                // ── Step 4: SCSI 控制器数量检查，不足时补充 ──────────
                if (controllerType == "SCSI")
                {
                    var allRasdResp = await WmiApi.QueryRelatedAsync(
                        settings,
                        "Msvm_ResourceAllocationSettingData",
                        obj => new RasdInfo(
                            obj["InstanceID"]?.ToString() ?? "",
                            Convert.ToInt32(obj["ResourceType"] ?? 0),
                            (obj["__PATH"]?.ToString() ?? obj.Path.Path)),
                        "Msvm_VirtualSystemSettingDataComponent",
                        WmiScope.HyperV);

                    var scsiCtrlList = (allRasdResp.Data ?? [])
                        .Where(r => r.ResourceType == 6)
                        .ToList();

                    int scsiCount = scsiCtrlList.Count;

                    if (controllerNumber >= scsiCount)
                    {
                        if (isRunning)
                            return (false, "Storage_Error_ScsiControllerHotAddNotSupported",
                                controllerType, controllerNumber, location);

                        // 补充 SCSI 控制器到够用
                        for (int i = scsiCount; i <= controllerNumber; i++)
                        {
                            var scsiClass = new System.Management.ManagementClass(
                                settings.Scope,
                                new System.Management.ManagementPath("Msvm_ResourceAllocationSettingData"),
                                null);
                            using var scsiObj = scsiClass.CreateInstance();
                            scsiObj["ResourceType"] = (ushort)6;
                            scsiObj["ResourceSubType"] = "Microsoft:Hyper-V:Synthetic SCSI Controller";
                            scsiObj["AutomaticAllocation"] = true;
                            string scsiXml = scsiObj.GetText(System.Management.TextFormat.CimDtd20);

                            var addScsiResult = await WmiApi.InvokeAsync(
                                "SELECT * FROM Msvm_VirtualSystemManagementService",
                                "AddResourceSettings",
                                p =>
                                {
                                    p["AffectedConfiguration"] = settings.Path.Path;
                                    p["ResourceSettings"] = new string[] { scsiXml };
                                },
                                WmiScope.HyperV);

                            if (!addScsiResult.Success)
                                return (false, Utils.GetFriendlyErrorMessage(addScsiResult.Error),
                                    controllerType, controllerNumber, location);
                        }

                        // 重新查控制器列表（刚添加完）
                        allRasdResp = await WmiApi.QueryRelatedAsync(
                            settings,
                            "Msvm_ResourceAllocationSettingData",
                            obj => new RasdInfo(
                                obj["InstanceID"]?.ToString() ?? "",
                                Convert.ToInt32(obj["ResourceType"] ?? 0),
                                (obj["__PATH"]?.ToString() ?? obj.Path.Path)),
                            "Msvm_VirtualSystemSettingDataComponent",
                            WmiScope.HyperV);

                        scsiCtrlList = (allRasdResp.Data ?? [])
                            .Where(r => r.ResourceType == 6)
                            .ToList();
                    }

                    // 取目标 SCSI 控制器的 WMI 路径（按顺序，index=controllerNumber）
                    if (controllerNumber >= scsiCtrlList.Count)
                        return (false, "Storage_Error_ScsiControllerNotFound",
                            controllerType, controllerNumber, location);
                }

                // ── Step 5: 取目标控制器的 WMI 路径 ──────────────────
                // IDE: ResourceType=5，Address 字段 = 控制器编号（"0" 或 "1"）
                // SCSI: ResourceType=6，按关联查询返回顺序的第 controllerNumber 个
                var ctrlRasdResp = await WmiApi.QueryRelatedAsync(
                    settings,
                    "Msvm_ResourceAllocationSettingData",
                    obj => new RasdInfo(
                        obj["InstanceID"]?.ToString() ?? "",
                        Convert.ToInt32(obj["ResourceType"] ?? 0),
                        (obj["__PATH"]?.ToString() ?? obj.Path.Path)),
                    "Msvm_VirtualSystemSettingDataComponent",
                    WmiScope.HyperV);

                string? controllerPath = null;
                if (controllerType == "IDE")
                {
                    // IDE 控制器 InstanceID 末尾格式：...\GUID\controllerNumber（只有一段数字，无 \D/\L）
                    controllerPath = ctrlRasdResp.Data?
                        .FirstOrDefault(r =>
                        {
                            if (r.ResourceType != 5) return false;
                            var segs = r.InstanceID.Split('\\');
                            return segs.Length >= 1
                                && int.TryParse(segs[^1], out int n)
                                && n == controllerNumber;
                        })
                        ?.ObjPath;
                }
                else
                {
                    var scsiList = ctrlRasdResp.Data?
                        .Where(r => r.ResourceType == 6)
                        .ToList();
                    controllerPath = scsiList?.ElementAtOrDefault(controllerNumber)?.ObjPath;
                }

                if (controllerPath == null)
                    return (false, "Storage_Error_ControllerNotFound",
                        controllerType, controllerNumber, location);

                // ── Step 6: 如果是 HardDisk + isNew，先创建 VHD ──────
                if (driveType == "HardDisk" && isNew && !string.IsNullOrWhiteSpace(pathOrNumber))
                {
                    var createResult = await CreateVhdAsync(
                        pathOrNumber, vhdType, sizeGb, sectorFormat, blockSize, parentPath);
                    if (!createResult.Success)
                        return (false, createResult.Message, controllerType, controllerNumber, location);
                }

                // ── Step 7: 添加槽位 RASD ────────────────────────────
                int slotResourceType = driveType == "DvdDrive" ? 16 : 17;
                string slotSubType = driveType == "DvdDrive"
                    ? "Microsoft:Hyper-V:Synthetic DVD Drive"
                    : (isPhysical
                        ? "Microsoft:Hyper-V:Physical Disk Drive"
                        : "Microsoft:Hyper-V:Synthetic Disk Drive");

                // ── Step 7: 添加槽位 RASD ────────────────────────────
                // 物理直通盘：HostResource 直接设在槽位 RASD 上（没有介质层 SASD）
                //   HostResource 格式：Msvm_DiskDrive 的完整 WMI 对象路径
                //   DeviceID 格式：Microsoft:GUID\diskNumber
                // 虚拟盘/DVD：槽位 RASD 不设 HostResource，介质路径在后续 SASD 里

                string? physicalHostResource = null;
                if (isPhysical && driveType == "HardDisk")
                {
                    // 取 Msvm_DiskDrive 对象路径，DeviceID LIKE '%\diskNumber'
                    var diskDriveResp = await WmiApi.QueryFirstAsync(
                        $"SELECT * FROM Msvm_DiskDrive WHERE DeviceID LIKE '%\\\\{pathOrNumber}'",
                        obj => (obj["__PATH"]?.ToString() ?? obj.Path.Path),
                        WmiScope.HyperV);

                    if (!diskDriveResp.HasData)
                        return (false, $"Physical disk {pathOrNumber} not found in Hyper-V",
                            controllerType, controllerNumber, location);

                    physicalHostResource = diskDriveResp.Data!;
                }

                var slotClass = new System.Management.ManagementClass(
                    settings.Scope,
                    new System.Management.ManagementPath("Msvm_ResourceAllocationSettingData"),
                    null);
                using var slotObj = slotClass.CreateInstance();
                slotObj["ResourceType"] = (ushort)slotResourceType;
                slotObj["ResourceSubType"] = slotSubType;
                slotObj["Parent"] = controllerPath;
                slotObj["AddressOnParent"] = location.ToString();
                slotObj["AutomaticAllocation"] = true;

                if (physicalHostResource != null)
                    slotObj["HostResource"] = new string[] { physicalHostResource };

                string slotXml = slotObj.GetText(System.Management.TextFormat.CimDtd20);

                var addSlotResult = await WmiApi.InvokeWithResultAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "AddResourceSettings",
                    p =>
                    {
                        p["AffectedConfiguration"] = settings.Path.Path;
                        p["ResourceSettings"] = new string[] { slotXml };
                    },
                    WmiScope.HyperV);

                if (!addSlotResult.Success)
                    return (false, Utils.GetFriendlyErrorMessage(addSlotResult.Error),
                        controllerType, controllerNumber, location);

                string? slotPath = addSlotResult.Data?.FirstOrDefault();

                if (slotPath == null)
                    return (false, "Storage_Error_SlotNotFound after AddResourceSettings",
                        controllerType, controllerNumber, location);

                // ── Step 8: 添加介质 SASD（虚拟盘/DVD，有路径时）─────
                // 物理直通盘不走这里，HostResource 已在槽位 RASD 上
                bool hasMedia = !isPhysical && !string.IsNullOrWhiteSpace(pathOrNumber);
                if (hasMedia)
                {
                    string mediaSubType = driveType == "DvdDrive"
                        ? "Microsoft:Hyper-V:Virtual CD/DVD Disk"
                        : "Microsoft:Hyper-V:Virtual Hard Disk";

                    var sasdClass = new System.Management.ManagementClass(
                        settings.Scope,
                        new System.Management.ManagementPath("Msvm_StorageAllocationSettingData"),
                        null);
                    using var sasdObj = sasdClass.CreateInstance();
                    sasdObj["ResourceType"] = (ushort)31;
                    sasdObj["ResourceSubType"] = mediaSubType;
                    sasdObj["Parent"] = slotPath;
                    sasdObj["AutomaticAllocation"] = true;
                    sasdObj["HostResource"] = new string[] { pathOrNumber };

                    string sasdXml = sasdObj.GetText(System.Management.TextFormat.CimDtd20);

                    var addMediaResult = await WmiApi.InvokeAsync(
                        "SELECT * FROM Msvm_VirtualSystemManagementService",
                        "AddResourceSettings",
                        p =>
                        {
                            p["AffectedConfiguration"] = settings.Path.Path;
                            p["ResourceSettings"] = new string[] { sasdXml };
                        },
                        WmiScope.HyperV);

                    if (!addMediaResult.Success)
                        return (false, Utils.GetFriendlyErrorMessage(addMediaResult.Error),
                            controllerType, controllerNumber, location);
                }

                return (true, "Storage_Msg_Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex)
            {
                return (false, Utils.GetFriendlyErrorMessage(ex.Message),
                    controllerType, controllerNumber, location);
            }
        }

        /// <summary>
        /// 内部：创建 VHD/VHDX 文件。
        /// 对应 New-VHD，走 Msvm_ImageManagementService.CreateVirtualHardDisk。
        ///
        /// Msvm_VirtualHardDiskSettingData 关键字段（文档确认）：
        ///   Type:   2=Fixed, 3=Dynamic, 4=Differencing
        ///   Format: 2=VHD, 3=VHDX
        ///   MaxInternalSize: 字节数（uint64）
        ///   BlockSize, LogicalSectorSize, PhysicalSectorSize: 扇区参数（uint32，0=默认）
        ///   ParentPath: 差分盘父路径
        /// </summary>
        private async Task<(bool Success, string Message)> CreateVhdAsync(
            string path, string vhdType, int sizeGb,
            string sectorFormat, string blockSize, string parentPathStr)
        {
            try
            {
                // 判断格式（vhd/vhdx）
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                ushort format = ext == ".vhd" ? (ushort)2 : (ushort)3;

                // Type
                ushort type = vhdType switch
                {
                    "Fixed" => 2,
                    "Differencing" => 4,
                    _ => 3  // Dynamic
                };

                // 扇区参数
                uint logicalSector = sectorFormat switch
                {
                    "512n" => 512,
                    "512e" => 512,
                    "4kn" => 4096,
                    _ => 0  // 0 = 默认
                };
                uint physicalSector = sectorFormat switch
                {
                    "512n" => 512,
                    "512e" => 4096,
                    "4kn" => 4096,
                    _ => 0
                };

                // BlockSize
                uint blockSizeBytes = 0;
                if (blockSize != "Default" && uint.TryParse(blockSize, out uint bs))
                    blockSizeBytes = bs;

                // 构造 VirtualHardDiskSettingData XML
                // 通过已有的 GetVirtualSystemManagementService 借用其 Scope
                // 该对象已持有连接好的 ManagementScope，避免重复建连
                using var svcForScope = WmiApi.GetVirtualSystemManagementService();
                var vhdClass = new System.Management.ManagementClass(
                    svcForScope.Scope,
                    new System.Management.ManagementPath("Msvm_VirtualHardDiskSettingData"),
                    null);
                using var vhdObj = vhdClass.CreateInstance();
                vhdObj["Type"] = type;
                vhdObj["Format"] = format;
                vhdObj["Path"] = path;
                vhdObj["MaxInternalSize"] = type == 4 ? (ulong)0 : (ulong)sizeGb * 1073741824UL;

                if (logicalSector > 0) vhdObj["LogicalSectorSize"] = logicalSector;
                if (physicalSector > 0) vhdObj["PhysicalSectorSize"] = physicalSector;
                if (blockSizeBytes > 0) vhdObj["BlockSize"] = blockSizeBytes;

                if (type == 4 && !string.IsNullOrWhiteSpace(parentPathStr))
                    vhdObj["ParentPath"] = parentPathStr;

                string vhdXml = vhdObj.GetText(System.Management.TextFormat.CimDtd20);

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_ImageManagementService",
                    "CreateVirtualHardDisk",
                    p => p["VirtualDiskSettingData"] = vhdXml,
                    WmiScope.HyperV);

                return result.Success
                    ? (true, string.Empty)
                    : (false, Utils.GetFriendlyErrorMessage(result.Error));
            }
            catch (Exception ex)
            {
                return (false, Utils.GetFriendlyErrorMessage(ex.Message));
            }
        }


        /// <summary>
        /// 从虚拟机移除存储设备。
        ///
        /// 分支逻辑（与原 PowerShell 完全对应）：
        ///   DVD + 关机 或 SCSI  → RemoveResourceSettings 移除槽位 RASD
        ///   DVD + 运行中 IDE + 有介质 → ModifyMediaPathAsync("") 弹出介质，返回 Ejected
        ///   DVD + 运行中 IDE + 无介质 → 报错，不支持热移除空驱动器
        ///   HardDisk            → RemoveResourceSettings 移除槽位 RASD
        ///                         物理盘额外调 SetDiskOfflineStatusAsync(false) 恢复联机
        ///
        /// RemoveResourceSettings 入参是对象路径引用数组（不是 XML），
        /// 取槽位 RASD 的 __PATH 字符串传入即可。
        /// </summary>
        public async Task<(bool Success, string Message)> RemoveDriveAsync(
            string vmName, VmStorageItem drive)
        {
            // Step 1：取 VM 运行状态
            var vmResp = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => Convert.ToInt32(obj["EnabledState"] ?? 0),
                WmiScope.HyperV);

            if (!vmResp.HasData)
                return (false, $"VM '{vmName}' not found");

            bool isRunning = vmResp.Data == 2;

            // Step 2：DVD 运行中 IDE → 只能弹出介质，不能移除驱动器
            if (drive.DriveType == "DvdDrive" &&
                isRunning &&
                drive.ControllerType == "IDE")
            {
                // 有介质 → 弹出
                if (drive.DiskType != "Empty" && !string.IsNullOrEmpty(drive.PathOrDiskNumber))
                {
                    var ejectResult = await ModifyMediaPathAsync(
                        vmName, drive.ControllerNumber, drive.ControllerLocation,
                        "Microsoft:Hyper-V:Virtual CD/DVD Disk", "");
                    return ejectResult.Success
                        ? (true, "Storage_Msg_Ejected")
                        : ejectResult;
                }

                // 无介质 → 不支持热移除空 IDE 驱动器
                return (false, "Storage_Error_DvdHotRemoveNotSupported");
            }

            // Step 3：定位槽位 RASD，通过 VM settings 关联查询限定在当前 VM 范围
            using var vmObj = WmiApi.GetVmComputerSystem(vmName);
            if (vmObj == null)
                return (false, $"VM '{vmName}' not found");

            using var settings = WmiApi.GetVmSettings(vmObj);
            if (settings == null)
                return (false, "Cannot get VM settings");

            var rasdResp = await WmiApi.QueryRelatedAsync(
                settings,
                "Msvm_ResourceAllocationSettingData",
                obj => new RasdInfo(
                    obj["InstanceID"]?.ToString() ?? "",
                    Convert.ToInt32(obj["ResourceType"] ?? 0),
                    (obj["__PATH"]?.ToString() ?? obj.Path.Path)),
                "Msvm_VirtualSystemSettingDataComponent",
                WmiScope.HyperV);

            if (!rasdResp.Success || rasdResp.Data == null)
                return (false, rasdResp.Error.Length > 0 ? rasdResp.Error : "Cannot enumerate resources");

            // InstanceID 实际格式：Microsoft:VM-GUID\CTRL-GUID\0\location\D
            // segs[^3] 固定是 "0"，不是控制器编号。
            // 正确定位：先按控制器类型（IDE=5/SCSI=6）分组，取第 ControllerNumber 个控制器的 GUID，
            // 再找 segs[^4] 匹配该 GUID 且 segs[^2]=location 的槽位。
            int ctrlResourceType = drive.ControllerType == "SCSI" ? 6 : 5;
            var ctrlList = rasdResp.Data
                .Where(r => r.ResourceType == ctrlResourceType)
                .ToList();

            if (drive.ControllerNumber >= ctrlList.Count)
                return (false, drive.DriveType == "DvdDrive"
                    ? "Storage_Error_DvdDriveNotFound"
                    : "Storage_Error_DiskNotFound");

            // 控制器 InstanceID 格式：Microsoft:VM-GUID\CTRL-GUID\0，取 segs[^2] 即 CTRL-GUID
            var ctrlSegs = ctrlList[drive.ControllerNumber].InstanceID.Split('\\');
            string ctrlGuid = ctrlSegs.Length >= 2 ? ctrlSegs[^2] : "";

            var slotRasd = rasdResp.Data.FirstOrDefault(r =>
            {
                var segs = r.InstanceID.Split('\\');
                // 槽位格式：Microsoft:VM-GUID\CTRL-GUID\0\location\D，共5段以上
                if (segs.Length < 5 || segs[^1] != "D") return false;
                return segs[^4] == ctrlGuid
                    && int.TryParse(segs[^2], out int cLoc) && cLoc == drive.ControllerLocation;
            });

            if (slotRasd == null)
                return (false, drive.DriveType == "DvdDrive"
                    ? "Storage_Error_DvdDriveNotFound"
                    : "Storage_Error_DiskNotFound");

            // Step 4：RemoveResourceSettings，入参是对象路径引用数组
            // 必须先删介质层 SASD（\L），再删槽位 RASD（\D），否则 Hyper-V 报错：
            // "仍然有一个逻辑磁盘对象连接到它"
            // 介质 InstanceID = 槽位 InstanceID 末尾 \D 替换为 \L，格式固定。
            // SASD 有介质时才需要删，空槽（无 \L 对象）直接删槽位即可。
            var mediaInstanceId = slotRasd.InstanceID[..^1] + "L"; // 末尾 D → L
            // WQL 中反斜杠是转义字符，InstanceID 里的每个 \ 必须双写
            var mediaInstanceIdWql = mediaInstanceId.Replace(@"\", @"\\");

            var mediaResp = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_StorageAllocationSettingData WHERE InstanceID = '{mediaInstanceIdWql}'",
                obj => (obj["__PATH"]?.ToString() ?? obj.Path.Path),
                WmiScope.HyperV);

            if (mediaResp.HasData)
            {
                var removeMediaResult = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "RemoveResourceSettings",
                    p => p["ResourceSettings"] = new string[] { mediaResp.Data! },
                    WmiScope.HyperV);

                if (!removeMediaResult.Success)
                    return (false, Utils.GetFriendlyErrorMessage(removeMediaResult.Error));
            }

            // 再删槽位
            var removeResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementService",
                "RemoveResourceSettings",
                p => p["ResourceSettings"] = new string[] { slotRasd.ObjPath },
                WmiScope.HyperV);

            if (!removeResult.Success)
                return (false, Utils.GetFriendlyErrorMessage(removeResult.Error));

            // Step 5：物理盘移除后恢复联机
            if (drive.DiskType == "Physical" && drive.DiskNumber > -1)
            {
                await Task.Delay(500);
                await SetDiskOfflineStatusAsync(drive.DiskNumber, false);
            }

            return (true, "Storage_Msg_Removed");
        }


        // ============================================================
        // 设备修改操作（已重构为原生 WMI）
        // ============================================================

        /// <summary>
        /// 修改光驱挂载的 ISO 文件路径。
        /// newIsoPath 为空表示弹出介质。
        ///
        /// WMI 结构（实测确认）：
        ///   SASD 介质层：ResourceType=31, ResourceSubType="Microsoft:Hyper-V:Virtual CD/DVD Disk"
        ///   AddressOnParent 为空，定位依赖 Parent 字段末尾的 \controllerNumber\location\D
        ///   HostResource[0] = ISO 路径，空数组 = 弹出
        ///   提交方法：Msvm_VirtualSystemManagementService.ModifyResourceSettings
        /// </summary>
        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(
            string vmName, int controllerNumber, int controllerLocation, string newIsoPath)
        {
            return await ModifyMediaPathAsync(
                vmName, controllerNumber, controllerLocation,
                "Microsoft:Hyper-V:Virtual CD/DVD Disk", newIsoPath);
        }

        /// <summary>
        /// 修改虚拟硬盘挂载的 VHD/VHDX 文件路径。
        /// 运行中的 VM（热交换）和关机 VM 均通过同一 WMI 路径处理。
        ///
        /// WMI 结构（与 DVD 相同层级）：
        ///   SASD 介质层：ResourceType=31, ResourceSubType="Microsoft:Hyper-V:Virtual Hard Disk"
        ///   定位方式同 DVD，依赖 Parent 字段末尾的 \controllerNumber\location\D
        /// </summary>
        public async Task<(bool Success, string Message)> ModifyHardDrivePathAsync(
            string vmName, string controllerType, int controllerNumber, int controllerLocation, string newPath)
        {
            return await ModifyMediaPathAsync(
                vmName, controllerNumber, controllerLocation,
                "Microsoft:Hyper-V:Virtual Hard Disk", newPath);
        }

        /// <summary>
        /// 通用内部方法：按 ResourceSubType + InstanceID 坐标定位 SASD，
        /// 修改 HostResource 后通过 ModifyResourceSettings 提交。
        ///
        /// InstanceID 格式（实测确认，C# 取到的是单反斜杠）：
        ///   Microsoft:VM-GUID\SASD-GUID\controllerNumber\location\L
        /// 按 \ 分割后：segments[^1]="L", segments[^2]=location, segments[^3]=controllerNumber
        /// </summary>
        private async Task<(bool Success, string Message)> ModifyMediaPathAsync(
            string vmName, int controllerNumber, int controllerLocation,
            string resourceSubType, string newPath)
        {
            // Step 1：取 VM settings 对象（用于关联查询，确保只查当前 VM 的资源）
            using var vmObj = WmiApi.GetVmComputerSystem(vmName);
            if (vmObj == null)
                return (false, $"VM '{vmName}' not found");

            using var settings = WmiApi.GetVmSettings(vmObj);
            if (settings == null)
                return (false, "Cannot get VM settings");

            // Step 2：关联查询该 VM 的所有 SASD，按 ResourceSubType + InstanceID 坐标定位目标
            var sasdResp = await WmiApi.QueryRelatedAsync(
                settings,
                "Msvm_StorageAllocationSettingData",
                obj => new
                {
                    InstanceID = obj["InstanceID"]?.ToString() ?? "",
                    ResourceSubType = obj["ResourceSubType"]?.ToString() ?? "",
                },
                "Msvm_VirtualSystemSettingDataComponent");

            if (!sasdResp.Success || sasdResp.Data == null)
                return (false, sasdResp.Error.Length > 0 ? sasdResp.Error : "Cannot enumerate storage resources");

            // InstanceID 示例：Microsoft:VM-GUID\SASD-GUID\0\1\L
            // 按 \ 分割：[..., "0", "1", "L"]
            //   segments[^3] = controllerNumber
            //   segments[^2] = location
            //   segments[^1] = "L"（固定后缀）
            var target = sasdResp.Data.FirstOrDefault(s =>
            {
                if (!string.Equals(s.ResourceSubType, resourceSubType,
                        StringComparison.OrdinalIgnoreCase)) return false;
                var segments = s.InstanceID.Split('\\');
                if (segments.Length < 3) return false;
                return int.TryParse(segments[^3], out int cNum) && cNum == controllerNumber
                    && int.TryParse(segments[^2], out int cLoc) && cLoc == controllerLocation;
            });

            if (target == null)
                return (false,
                    $"Storage resource not found: subType={resourceSubType}, " +
                    $"controller={controllerNumber}, location={controllerLocation}");

            // Step 3：精确定位 SASD 并修改 HostResource，提交到 ModifyResourceSettings
            // 方法签名：ModifyResourceSettings([in] string ResourceSettings[])
            // ResourceSettings 是序列化后的 XML 字符串数组（wrapInArray=true）
            //
            // 关键：WQL 中反斜杠是转义字符（MS-WMI 规范 §2.2.1），
            // InstanceID 里的每个 \ 必须双写为 \\，否则 WMI 引擎吃掉转义符
            // 导致 "无效查询" 或根本查不到对象。
            // 原 PowerShell 版本用 cmdlet 直接操作对象引用，完全不走 WQL，无此问题。
            string safeId = target.InstanceID
                .Replace("'", "\\'")    // 防 WQL 注入
                .Replace(@"\", @"\\");  // WQL 反斜杠转义

            var result = await WmiApi.WithObjectAsync(
                wql: $"SELECT * FROM Msvm_StorageAllocationSettingData WHERE InstanceID = '{safeId}'",
                modifier: obj =>
                {
                    obj["HostResource"] = string.IsNullOrWhiteSpace(newPath)
                        ? new string[0]
                        : new string[] { newPath };
                },
                submitMethod: "ModifyResourceSettings",
                submitParamName: "ResourceSettings",
                wrapInArray: true,
                serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService");

            return result.Success
                ? (true, "Storage_Msg_Success")
                : (false, Utils.GetFriendlyErrorMessage(result.Error));
        }

        // ============================================================
        // 主机物理磁盘控制（已重构为原生 WMI）
        // ============================================================

        /// <summary>
        /// 设置宿主机物理硬盘的脱机/联机状态。
        /// 文档：Root\Microsoft\Windows\Storage，MSFT_Disk.Offline() / .Online()
        ///   签名：UInt32 Offline/Online([out] String ExtendedStatus)
        ///   返回 0=Success；ExtendedStatus 含嵌入 MSFT_StorageExtendedStatus。
        /// </summary>
        public async Task<ApiResponse> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                ci => ci,
                WmiScope.Storage);

            if (!diskResp.HasData)
                return ApiResponse.Fail($"Disk {diskNumber} not found", -1, ApiErrorSource.Wmi);

            string methodName = isOffline ? "Offline" : "Online";

            return await WmiApi.InvokeCimMethodAsync(
                diskResp.Data!,
                methodName,
                WmiScope.Storage);
        }

        // ============================================================
        // ISO 镜像生成
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
        // 内部辅助数据模型
        // ============================================================

        private sealed record RasdInfo(string InstanceID, int ResourceType, string ObjPath);

        private class HostDiskInfoCache
        {
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGB { get; set; }
        }
    }
}