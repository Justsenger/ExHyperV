using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Api;
using ExHyperV.Models;
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

            var resp = await WmiApi.WithFirstAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{WmiApi.Escape(vm.Name)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                async settings =>
                {
                    var rasdResp = await WmiApi.QueryRelatedCimAsync(
                        settings,
                        "Msvm_VirtualSystemSettingDataComponent",
                        "Msvm_ResourceAllocationSettingData",
                        "GroupComponent",
                        "PartComponent",
                        obj => obj,
                        WmiScope.HyperV);
                    var sasdResp = await WmiApi.QueryRelatedCimAsync(
                        settings,
                        "Msvm_VirtualSystemSettingDataComponent",
                        "Msvm_StorageAllocationSettingData",
                        "GroupComponent",
                        "PartComponent",
                        obj => obj,
                        WmiScope.HyperV);
                    return (rasdResp, sasdResp);
                },
                WmiScope.HyperV);

            if (!resp.HasData) return;
            var (rasdResp, sasdResp) = resp.Data!;
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
            List<ManagementObject> allResources,
            ref Dictionary<string, int>? hvDiskMap,
            ref Dictionary<int, HostDiskInfoCache>? osDiskMap)
        {
            var resultList = new List<VmStorageItem>();
            var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);
            var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

            var controllers = allResources
                .Where(res =>
                {
                    int rt = Convert.ToInt32(res["ResourceType"] ?? 0);
                    return rt == 5 || rt == 6;
                })
                .OrderBy(c => c["ResourceType"])
                .ToList();

            var childrenMap = new Dictionary<string, List<ManagementObject>>();
            foreach (var res in allResources)
            {
                var parentPath = res["Parent"]?.ToString();
                if (string.IsNullOrEmpty(parentPath)) continue;

                var match = parentRegex.Match(parentPath);
                if (!match.Success) continue;

                string parentId = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!childrenMap.ContainsKey(parentId))
                    childrenMap[parentId] = new List<ManagementObject>();
                childrenMap[parentId].Add(res);
            }

            int scsiCounter = 0, ideCounter = 0;

            foreach (var ctrl in controllers)
            {
                string ctrlId = ctrl["InstanceID"]?.ToString() ?? "";
                int ctrlTypeVal = Convert.ToInt32(ctrl["ResourceType"]);
                string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                int ctrlNum = ctrlType == "SCSI" ? scsiCounter++ : ideCounter++;

                if (!childrenMap.TryGetValue(ctrlId, out var slots)) continue;

                foreach (var slot in slots)
                {
                    int resType = Convert.ToInt32(slot["ResourceType"]);
                    if (resType != 16 && resType != 17) continue;

                    string address = slot["AddressOnParent"]?.ToString() ?? "0";
                    int location = int.TryParse(address, out int loc) ? loc : 0;

                    string slotId = slot["InstanceID"]?.ToString() ?? "";
                    ManagementObject? media = null;
                    if (childrenMap.TryGetValue(slotId, out var mediaList))
                    {
                        media = mediaList.FirstOrDefault(m =>
                        {
                            int t = Convert.ToInt32(m["ResourceType"]);
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

                    var slotHostRes = slot["HostResource"] as string[];
                    var effectiveMedia = media ?? (slotHostRes?.Length > 0 ? slot : null);

                    if (effectiveMedia != null)
                    {
                        var hostRes = effectiveMedia["HostResource"] as string[];
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
                obj =>
                {
                    int number = Convert.ToInt32(obj["Number"] ?? -1);
                    ushort busType = Convert.ToUInt16(obj["BusType"] ?? 0);
                    bool isSystem = Convert.ToBoolean(obj["IsSystem"] ?? false);
                    bool isBoot = Convert.ToBoolean(obj["IsBoot"] ?? false);
                    bool isOffline = Convert.ToBoolean(obj["IsOffline"] ?? false);
                    long sizeBytes = Convert.ToInt64(obj["Size"] ?? 0L);
                    string friendlyName = obj["FriendlyName"]?.ToString() ?? "";
                    var opArr = obj["OperationalStatus"] as ushort[];
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
        // 槽位检测
        // ============================================================

        public async Task<(string ControllerType, int ControllerNumber, int Location)>
            GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            var vmResp = await WmiApi.QueryFirstAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => Convert.ToInt32(obj["EnabledState"] ?? 0),
                WmiScope.HyperV);

            if (!vmResp.HasData) return ("NONE", -1, -1);
            bool isRunning = (vmResp.Data == 2);

            var settingsResp = await WmiApi.QueryFirstAsync(
                $"SELECT InstanceID, VirtualSystemSubType FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj,
                WmiScope.HyperV);

            if (!settingsResp.HasData) return ("NONE", -1, -1);

            // 注意：settingsResp.Data 已被 using 释放，需用 WithFirstAsync
            return await WmiApi.WithFirstAsync(
                $"SELECT InstanceID, VirtualSystemSubType FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                async settings =>
                {
                    string subType = settings["VirtualSystemSubType"]?.ToString() ?? "";
                    bool isGen1 = subType == "Microsoft:Hyper-V:SubType:1";
                    string settingId = settings["InstanceID"]?.ToString() ?? "";

                    var rasdResp = await WmiApi.QueryAsync(
                        $"SELECT ResourceType, Address, AddressOnParent, InstanceID, Parent " +
                        $"FROM Msvm_ResourceAllocationSettingData " +
                        $"WHERE InstanceID LIKE '{WmiApi.Escape(settingId)}%' " +
                        $"AND (ResourceType = 5 OR ResourceType = 6 OR ResourceType = 16 OR ResourceType = 17)",
                        obj => obj,
                        WmiScope.HyperV);

                    if (!rasdResp.HasData) return ("NONE", -1, -1);

                    var controllers = new List<(string Type, int Number, string InstanceID)>();
                    var occupiedSlots = new HashSet<string>();
                    var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

                    foreach (var res in rasdResp.Data!)
                    {
                        int rt = Convert.ToInt32(res["ResourceType"] ?? 0);
                        string instanceId = res["InstanceID"]?.ToString() ?? "";

                        if (rt == 5 || rt == 6)
                        {
                            string type = rt == 5 ? "IDE" : "SCSI";
                            int number = Convert.ToInt32(res["Address"] ?? 0);
                            controllers.Add((type, number, instanceId));
                        }
                        else if (rt == 16 || rt == 17)
                        {
                            string parentPath = res["Parent"]?.ToString() ?? "";
                            string addressOnParent = res["AddressOnParent"]?.ToString() ?? "";

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
                },
                WmiScope.HyperV) is { HasData: true } r ? r.Data! : ("NONE", -1, -1);
        }

        // ============================================================
        // 设备增删改操作
        // ============================================================

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)>
            AddDriveAsync(
                string vmName, string controllerType, int controllerNumber, int location, string driveType,
                string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256,
                string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default",
                string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null)
        {
            if (driveType == "DvdDrive" && isNew && !string.IsNullOrWhiteSpace(isoSourcePath))
            {
                var isoResult = await CreateIsoFromDirectoryAsync(isoSourcePath, pathOrNumber, isoVolumeLabel);
                if (!isoResult.Success)
                    return (false, isoResult.Message, controllerType, controllerNumber, location);
            }

            try
            {
                using var vmObj = WmiApi.GetVmComputerSystem(vmName);
                if (vmObj == null)
                    return (false, $"VM '{vmName}' not found", controllerType, controllerNumber, location);

                int enabledState = WmiApi.Prop<int>(vmObj, "EnabledState", 0);
                bool isRunning = enabledState == 2;

                using var settings = WmiApi.GetVmSettings(vmObj);
                if (settings == null)
                    return (false, "Cannot get VM settings", controllerType, controllerNumber, location);

                if (controllerType == "IDE" && isRunning && driveType != "DvdDrive")
                    return (false, "Storage_Error_IdeHotPlugNotSupported", controllerType, controllerNumber, location);

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

                        for (int i = scsiCount; i <= controllerNumber; i++)
                        {
                            var scsiClass = new ManagementClass(
                                settings.Scope,
                                new ManagementPath("Msvm_ResourceAllocationSettingData"),
                                null);
                            using var scsiObj = scsiClass.CreateInstance();
                            scsiObj["ResourceType"] = (ushort)6;
                            scsiObj["ResourceSubType"] = "Microsoft:Hyper-V:Synthetic SCSI Controller";
                            scsiObj["AutomaticAllocation"] = true;
                            string scsiXml = scsiObj.GetText(TextFormat.CimDtd20);

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

                    if (controllerNumber >= scsiCtrlList.Count)
                        return (false, "Storage_Error_ScsiControllerNotFound",
                            controllerType, controllerNumber, location);
                }

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

                if (driveType == "HardDisk" && isNew && !string.IsNullOrWhiteSpace(pathOrNumber))
                {
                    var createResult = await CreateVhdAsync(
                        pathOrNumber, vhdType, sizeGb, sectorFormat, blockSize, parentPath);
                    if (!createResult.Success)
                        return (false, createResult.Message, controllerType, controllerNumber, location);
                }

                int slotResourceType = driveType == "DvdDrive" ? 16 : 17;
                string slotSubType = driveType == "DvdDrive"
                    ? "Microsoft:Hyper-V:Synthetic DVD Drive"
                    : (isPhysical
                        ? "Microsoft:Hyper-V:Physical Disk Drive"
                        : "Microsoft:Hyper-V:Synthetic Disk Drive");

                string? physicalHostResource = null;
                if (isPhysical && driveType == "HardDisk")
                {
                    var diskDriveResp = await WmiApi.QueryFirstAsync(
                        $"SELECT * FROM Msvm_DiskDrive WHERE DeviceID LIKE '%\\\\{pathOrNumber}'",
                        obj => (obj["__PATH"]?.ToString() ?? obj.Path.Path),
                        WmiScope.HyperV);

                    if (!diskDriveResp.HasData)
                        return (false, $"Physical disk {pathOrNumber} not found in Hyper-V",
                            controllerType, controllerNumber, location);

                    physicalHostResource = diskDriveResp.Data!;
                }

                var slotClass = new ManagementClass(
                    settings.Scope,
                    new ManagementPath("Msvm_ResourceAllocationSettingData"),
                    null);
                using var slotObj = slotClass.CreateInstance();
                slotObj["ResourceType"] = (ushort)slotResourceType;
                slotObj["ResourceSubType"] = slotSubType;
                slotObj["Parent"] = controllerPath;
                slotObj["AddressOnParent"] = location.ToString();
                slotObj["AutomaticAllocation"] = true;

                if (physicalHostResource != null)
                    slotObj["HostResource"] = new string[] { physicalHostResource };

                string slotXml = slotObj.GetText(TextFormat.CimDtd20);

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

                bool hasMedia = !isPhysical && !string.IsNullOrWhiteSpace(pathOrNumber);
                if (hasMedia)
                {
                    string mediaSubType = driveType == "DvdDrive"
                        ? "Microsoft:Hyper-V:Virtual CD/DVD Disk"
                        : "Microsoft:Hyper-V:Virtual Hard Disk";

                    var sasdClass = new ManagementClass(
                        settings.Scope,
                        new ManagementPath("Msvm_StorageAllocationSettingData"),
                        null);
                    using var sasdObj = sasdClass.CreateInstance();
                    sasdObj["ResourceType"] = (ushort)31;
                    sasdObj["ResourceSubType"] = mediaSubType;
                    sasdObj["Parent"] = slotPath;
                    sasdObj["AutomaticAllocation"] = true;
                    sasdObj["HostResource"] = new string[] { pathOrNumber };

                    string sasdXml = sasdObj.GetText(TextFormat.CimDtd20);

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

        private async Task<(bool Success, string Message)> CreateVhdAsync(
            string path, string vhdType, int sizeGb,
            string sectorFormat, string blockSize, string parentPathStr)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                ushort format = ext == ".vhd" ? (ushort)2 : (ushort)3;

                ushort type = vhdType switch
                {
                    "Fixed" => 2,
                    "Differencing" => 4,
                    _ => 3
                };

                uint logicalSector = sectorFormat switch
                {
                    "512n" => 512,
                    "512e" => 512,
                    "4kn" => 4096,
                    _ => 0
                };
                uint physicalSector = sectorFormat switch
                {
                    "512n" => 512,
                    "512e" => 4096,
                    "4kn" => 4096,
                    _ => 0
                };

                uint blockSizeBytes = 0;
                if (blockSize != "Default" && uint.TryParse(blockSize, out uint bs))
                    blockSizeBytes = bs;

                using var svcForScope = WmiApi.GetVirtualSystemManagementService();
                var vhdClass = new ManagementClass(
                    svcForScope.Scope,
                    new ManagementPath("Msvm_VirtualHardDiskSettingData"),
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

                string vhdXml = vhdObj.GetText(TextFormat.CimDtd20);

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

        public async Task<(bool Success, string Message)> RemoveDriveAsync(
            string vmName, VmStorageItem drive)
        {
            var vmResp = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => Convert.ToInt32(obj["EnabledState"] ?? 0),
                WmiScope.HyperV);

            if (!vmResp.HasData)
                return (false, $"VM '{vmName}' not found");

            bool isRunning = vmResp.Data == 2;

            if (drive.DriveType == "DvdDrive" &&
                isRunning &&
                drive.ControllerType == "IDE")
            {
                if (drive.DiskType != "Empty" && !string.IsNullOrEmpty(drive.PathOrDiskNumber))
                {
                    var ejectResult = await ModifyMediaPathAsync(
                        vmName, drive.ControllerNumber, drive.ControllerLocation,
                        "Microsoft:Hyper-V:Virtual CD/DVD Disk", "");
                    return ejectResult.Success
                        ? (true, "Storage_Msg_Ejected")
                        : ejectResult;
                }

                return (false, "Storage_Error_DvdHotRemoveNotSupported");
            }

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

            int ctrlResourceType = drive.ControllerType == "SCSI" ? 6 : 5;
            var ctrlList = rasdResp.Data
                .Where(r => r.ResourceType == ctrlResourceType)
                .ToList();

            if (drive.ControllerNumber >= ctrlList.Count)
                return (false, drive.DriveType == "DvdDrive"
                    ? "Storage_Error_DvdDriveNotFound"
                    : "Storage_Error_DiskNotFound");

            var ctrlSegs = ctrlList[drive.ControllerNumber].InstanceID.Split('\\');
            string ctrlGuid = ctrlSegs.Length >= 2 ? ctrlSegs[^2] : "";

            var slotRasd = rasdResp.Data.FirstOrDefault(r =>
            {
                var segs = r.InstanceID.Split('\\');
                if (segs.Length < 5 || segs[^1] != "D") return false;
                return segs[^4] == ctrlGuid
                    && int.TryParse(segs[^2], out int cLoc) && cLoc == drive.ControllerLocation;
            });

            if (slotRasd == null)
                return (false, drive.DriveType == "DvdDrive"
                    ? "Storage_Error_DvdDriveNotFound"
                    : "Storage_Error_DiskNotFound");

            var mediaInstanceId = slotRasd.InstanceID[..^1] + "L";
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

            var removeResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementService",
                "RemoveResourceSettings",
                p => p["ResourceSettings"] = new string[] { slotRasd.ObjPath },
                WmiScope.HyperV);

            if (!removeResult.Success)
                return (false, Utils.GetFriendlyErrorMessage(removeResult.Error));

            if (drive.DiskType == "Physical" && drive.DiskNumber > -1)
            {
                await Task.Delay(500);
                await SetDiskOfflineStatusAsync(drive.DiskNumber, false);
            }

            return (true, "Storage_Msg_Removed");
        }

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(
            string vmName, int controllerNumber, int controllerLocation, string newIsoPath)
        {
            return await ModifyMediaPathAsync(
                vmName, controllerNumber, controllerLocation,
                "Microsoft:Hyper-V:Virtual CD/DVD Disk", newIsoPath);
        }

        public async Task<(bool Success, string Message)> ModifyHardDrivePathAsync(
            string vmName, string controllerType, int controllerNumber, int controllerLocation, string newPath)
        {
            return await ModifyMediaPathAsync(
                vmName, controllerNumber, controllerLocation,
                "Microsoft:Hyper-V:Virtual Hard Disk", newPath);
        }

        private async Task<(bool Success, string Message)> ModifyMediaPathAsync(
            string vmName, int controllerNumber, int controllerLocation,
            string resourceSubType, string newPath)
        {
            using var vmObj = WmiApi.GetVmComputerSystem(vmName);
            if (vmObj == null)
                return (false, $"VM '{vmName}' not found");

            using var settings = WmiApi.GetVmSettings(vmObj);
            if (settings == null)
                return (false, "Cannot get VM settings");

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

            string safeId = target.InstanceID
                .Replace("'", "\\'")
                .Replace(@"\", @"\\");

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
        // 主机物理磁盘控制
        // ============================================================

        public async Task<ApiResponse> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                obj => obj,
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

        public async Task<ApiResponse> SetDiskReadOnlyAsync(int diskNumber, bool isReadOnly)
        {
            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                obj => obj,
                WmiScope.Storage);

            if (!diskResp.HasData)
                return ApiResponse.Fail($"Disk {diskNumber} not found", -1, ApiErrorSource.Wmi);

            return await WmiApi.InvokeCimMethodAsync(
                diskResp.Data!,
                "SetAttributes",
                WmiScope.Storage,
                p => p["IsReadOnly"] = isReadOnly);
        }

        public async Task<(bool Success, int DiskNumber)> MountDiskImageAsync(string imagePath)
        {
            var imageResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_DiskImage WHERE ImagePath = '{imagePath.Replace("'", "\\'")}'",
                obj => obj,
                WmiScope.Storage);

            if (!imageResp.HasData)
                return (false, -1);

            var mountResult = await WmiApi.InvokeCimMethodAsync(
                imageResp.Data!,
                "Mount",
                WmiScope.Storage,
                p =>
                {
                    p["Access"] = (uint)2;
                    p["NoDriveLetter"] = true;
                });

            if (!mountResult.Success) return (false, -1);

            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_DiskImage WHERE ImagePath = '{imagePath.Replace("'", "\\'")}'",
                obj => Convert.ToInt32(obj["Number"] ?? -1),
                WmiScope.Storage);

            return (true, diskResp.Data);
        }

        public async Task<bool> DismountDiskImageAsync(string imagePath)
        {
            var imageResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_DiskImage WHERE ImagePath = '{imagePath.Replace("'", "\\'")}'",
                obj => obj,
                WmiScope.Storage);

            if (!imageResp.HasData) return true;

            var result = await WmiApi.InvokeCimMethodAsync(
                imageResp.Data!,
                "Dismount",
                WmiScope.Storage);

            return result.Success;
        }

        public async Task<(bool Success, int DiskNumber)> MountVhdxAsync(string imagePath)
        {
            await DismountVhdxAsync(imagePath);

            var result = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_ImageManagementService",
                "AttachVirtualHardDisk",
                p =>
                {
                    p["Path"] = imagePath;
                    p["ReadOnly"] = false;
                    p["AssignDriveLetter"] = false;
                },
                WmiScope.HyperV);

            if (!result.Success) return (false, -1);

            for (int i = 0; i < 10; i++)
            {
                var diskResp = await WmiApi.QueryFirstCimAsync(
                    $"SELECT * FROM MSFT_Disk WHERE Location = '{imagePath.Replace("'", "\\'").Replace("\\", "\\\\")}'",
                    obj => Convert.ToInt32(obj["Number"] ?? -1),
                    WmiScope.Storage);

                if (diskResp.HasData && diskResp.Data >= 0)
                    return (true, diskResp.Data);

                await Task.Delay(500);
            }
            return (false, -1);
        }

        public async Task<bool> DismountVhdxAsync(string imagePath)
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

                using var svcSearcher = new ManagementObjectSearcher(ms,
                    new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
                using var svcCol = svcSearcher.Get();
                using var imgSvc = svcCol.Cast<ManagementObject>().FirstOrDefault();
                if (imgSvc == null) return false;

                using var inParams = imgSvc.GetMethodParameters("FindMountedStorageImageInstance");
                inParams["CriterionType"] = (ushort)2;
                inParams["SelectionCriterion"] = imagePath;
                using var outParams = imgSvc.InvokeMethod("FindMountedStorageImageInstance", inParams, null);

                if (outParams == null || Convert.ToInt32(outParams["ReturnValue"]) != 0) return true;

                string imgPath = outParams["Image"]?.ToString();
                if (string.IsNullOrEmpty(imgPath)) return true;

                using var mountedImg = new ManagementObject(ms, new ManagementPath(imgPath), null);
                mountedImg.Get();
                mountedImg.InvokeMethod("DetachVirtualHardDisk", null, null);
                return true;
            }
            catch { return false; }
        }

        public async Task<(bool Success, char DriveLetter)> AssignPartitionDriveLetterAsync(
            int diskNumber, int partitionNumber, char driveLetter)
        {
            var partResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
                obj => obj,
                WmiScope.Storage);

            if (!partResp.HasData)
                return (false, '\0');

            var result = await WmiApi.InvokeCimMethodAsync(
                partResp.Data!,
                "AddAccessPath",
                WmiScope.Storage,
                p => p["AccessPath"] = $"{driveLetter}:\\");

            return result.Success ? (true, driveLetter) : (false, '\0');
        }

        public async Task<bool> RemovePartitionAccessPathAsync(
            int diskNumber, int partitionNumber, char driveLetter)
        {
            var partResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
                obj => obj,
                WmiScope.Storage);

            if (!partResp.HasData) return true;

            var result = await WmiApi.InvokeCimMethodAsync(
                partResp.Data!,
                "RemoveAccessPath",
                WmiScope.Storage,
                p => p["AccessPath"] = $"{driveLetter}:\\");

            return result.Success;
        }

        public async Task RemoveAllPartitionAccessPathsAsync(int diskNumber)
        {
            var partsResp = await WmiApi.QueryCimAsync(
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}",
                obj => obj,
                WmiScope.Storage);

            if (!partsResp.HasData) return;

            foreach (var part in partsResp.Data!)
            {
                char letter = Convert.ToChar(part["DriveLetter"] ?? '\0');
                if (letter == '\0') continue;
                await WmiApi.InvokeCimMethodAsync(
                    part,
                    "RemoveAccessPath",
                    WmiScope.Storage,
                    p => p["AccessPath"] = $"{letter}:\\");
            }
        }

        public async Task<(bool Success, string CtrlType, int CtrlNum, int CtrlLoc)>
            DetachPhysicalDiskAsync(string vmName, int diskNumber)
        {
            using var vmObj = WmiApi.GetVmComputerSystem(vmName);
            if (vmObj == null) return (false, "", 0, 0);

            using var settings = WmiApi.GetVmSettings(vmObj);
            if (settings == null) return (false, "", 0, 0);

            var rasdResp = await WmiApi.QueryRelatedAsync(
                settings,
                "Msvm_ResourceAllocationSettingData",
                obj => new RasdInfo(
                    obj["InstanceID"]?.ToString() ?? "",
                    Convert.ToInt32(obj["ResourceType"] ?? 0),
                    obj["__PATH"]?.ToString() ?? obj.Path.Path),
                "Msvm_VirtualSystemSettingDataComponent",
                WmiScope.HyperV);

            if (!rasdResp.Success || rasdResp.Data == null) return (false, "", 0, 0);

            var hvDiskResp = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_DiskDrive WHERE DriveNumber = {diskNumber}",
                obj => obj["DeviceID"]?.ToString() ?? "",
                WmiScope.HyperV);

            if (!hvDiskResp.HasData) return (false, "", 0, 0);
            string deviceId = hvDiskResp.Data;

            RasdInfo? slotRasd = null;
            foreach (var rasd in rasdResp.Data.Where(r => r.ResourceType == 17))
            {
                var slotResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_ResourceAllocationSettingData WHERE InstanceID = '{rasd.InstanceID.Replace(@"\", @"\\")}'",
                    obj => (obj["HostResource"] as string[])?.FirstOrDefault() ?? "",
                    WmiScope.HyperV);

                if (slotResp.HasData && slotResp.Data.Contains(
                    deviceId.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase))
                {
                    slotRasd = rasd;
                    break;
                }
            }

            if (slotRasd == null) return (false, "", 0, 0);

            var segs = slotRasd.InstanceID.Split('\\');
            if (segs.Length < 5) return (false, "", 0, 0);

            int ctrlLoc = int.TryParse(segs[^2], out int l) ? l : 0;

            string ctrlGuid = segs[^4];
            var ctrlList = rasdResp.Data.Where(r => r.ResourceType == 5 || r.ResourceType == 6).ToList();
            var ctrl = ctrlList.FirstOrDefault(c => c.InstanceID.Contains(ctrlGuid));
            if (ctrl == null) return (false, "", 0, 0);

            string ctrlType = ctrl.ResourceType == 6 ? "SCSI" : "IDE";
            int ctrlNum = ctrlList.Where(c => c.ResourceType == ctrl.ResourceType).ToList().IndexOf(ctrl);

            var removeResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementService",
                "RemoveResourceSettings",
                p => p["ResourceSettings"] = new string[] { slotRasd.ObjPath },
                WmiScope.HyperV);

            if (!removeResult.Success) return (false, "", 0, 0);

            return (true, ctrlType, ctrlNum, ctrlLoc);
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