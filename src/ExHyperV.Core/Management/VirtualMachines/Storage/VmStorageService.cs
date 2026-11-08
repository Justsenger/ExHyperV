using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>读取与配置虚拟机存储挂接，处理虚拟磁盘、光驱和物理磁盘。</summary>
    public static partial class VmStorageService
    {
        /// <summary>读取虚拟机控制器上的虚拟磁盘、物理直通磁盘及光驱挂接。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>挂接列表或查询错误；控制器编号与槽位使用从零开始的索引。</returns>
        /// <remarks>存在物理磁盘挂接时，宿主磁盘映射或身份读取失败会返回查询错误，不把缺测当成直通盘失效。</remarks>
        public static async Task<ApiResponse<List<VmStorageAttachment>>> GetVmStorageItemsAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse<List<VmStorageAttachment>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);

            var resp = await WmiApi.WithFirstAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                $"WHERE {VmIdentity.SettingsPredicate(vmId)}",
                async settings =>
                {
                    var rasdResp = await WmiApi.QueryRelatedCimAsync(
                        settings,
                        "Msvm_VirtualSystemSettingDataComponent",
                        "Msvm_ResourceAllocationSettingData",
                        "GroupComponent",
                        "PartComponent",
                        obj => new StorageResource(WmiApi.PropStr(obj, "InstanceID"), Convert.ToInt32(obj["ResourceType"] ?? 0),
                            WmiApi.PropStr(obj, "Parent"), WmiApi.PropStr(obj, "AddressOnParent"),
                            (obj["HostResource"] as string[])?.ToArray() ?? [],
                            WmiApi.PropStr(obj, "ResourceSubType"), obj.Path.Path),
                        WmiScope.HyperV);
                    var sasdResp = await WmiApi.QueryRelatedCimAsync(
                        settings,
                        "Msvm_VirtualSystemSettingDataComponent",
                        "Msvm_StorageAllocationSettingData",
                        "GroupComponent",
                        "PartComponent",
                        obj => new StorageResource(WmiApi.PropStr(obj, "InstanceID"), Convert.ToInt32(obj["ResourceType"] ?? 0),
                            WmiApi.PropStr(obj, "Parent"), WmiApi.PropStr(obj, "AddressOnParent"),
                            (obj["HostResource"] as string[])?.ToArray() ?? [],
                            WmiApi.PropStr(obj, "ResourceSubType"), obj.Path.Path),
                        WmiScope.HyperV);
                    return (rasdResp, sasdResp);
                },
                WmiScope.HyperV);

            if (!resp.HasData) return ApiResponse<List<VmStorageAttachment>>.Fail(resp.Error, resp.Code, resp.ErrorSource,
                resp.Exception, resp.Success ? ApiErrorKind.NotFound : resp.ErrorKind);
            var (rasdResp, sasdResp) = resp.Data!;
            if (!rasdResp.Success) return ApiResponse<List<VmStorageAttachment>>.Fail(rasdResp.Error, rasdResp.Code, rasdResp.ErrorSource, rasdResp.Exception, rasdResp.ErrorKind);
            if (!sasdResp.Success) return ApiResponse<List<VmStorageAttachment>>.Fail(sasdResp.Error, sasdResp.Code, sasdResp.ErrorSource, sasdResp.Exception, sasdResp.ErrorKind);

            var allResources = (rasdResp.Data ?? []).Concat(sasdResp.Data ?? []).ToList();
            // BuildStorageItems→BuildDiskMaps 内含同步 WMI(.GetAwaiter().GetResult())，挪线程池，别在 UI 线程跑。
            // ref 局部声明在 lambda 内规避"ref 变量不能被闭包捕获"。
            try
            {
                var items = await Task.Run(() =>
                {
                    Dictionary<string, int>? hvDiskMap = null;
                    Dictionary<int, HostDiskInfoCache>? osDiskMap = null;
                    return BuildStorageItems(allResources, ref hvDiskMap, ref osDiskMap, vmId);
                }).ConfigureAwait(false);
                return ApiResponse<List<VmStorageAttachment>>.Ok(items.OrderBy(i => i.ControllerType).ThenBy(i => i.ControllerNumber).ThenBy(i => i.ControllerLocation).ToList());
            }
            catch (CoreOperationException ex)
            {
                var failure = ex.Result;
                return ApiResponse<List<VmStorageAttachment>>.Fail(failure.Error, failure.Code,
                    failure.ErrorSource, failure.Exception, failure.ErrorKind);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<VmStorageAttachment>>.Fail(ex.Message,
                    exception: ex, kind: ApiErrorKind.OperationFailed);
            }
        }

        internal static async Task<ApiResponse<List<VmStorageAttachment>>> GetVmStorageItemsAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            return identity.HasData ? await GetVmStorageItemsAsync(identity.Data)
                : ApiResponse<List<VmStorageAttachment>>.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
        }

        // 该 VM 悬空的直通物理硬盘(HostResource 钉的盘已从可直通池 Msvm_DiskDrive 消失——被拔出/联机)。
        // 无副作用(不动 vm.StorageItems)，复用 BuildStorageItems 的 stale 检测，供开机失败反应式清理用。
        /// <summary>查找主机物理磁盘身份或编号已变化的直通挂接，供启动修复流程处理。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>成功时为需要核对或重新挂接的磁盘列表；查询失败与成功空列表分开表示，此方法本身不执行修复。</returns>
        /// <remarks>宿主磁盘映射读取成功后才判断失效；读取失败直接返回错误。失效表示当前挂接引用不再匹配，不等于已经证明物理设备被拔出。</remarks>
        public static async Task<ApiResponse<List<VmStorageAttachment>>> FindStalePassthroughDisksAsync(Guid vmId)
        {
            var response = await GetVmStorageItemsAsync(vmId);
            return response.HasData
                ? ApiResponse<List<VmStorageAttachment>>.Ok(response.Data.Where(i => i.IsPassthroughStale && i.DiskType == "Physical" && i.DriveType == "HardDisk").ToList())
                : ApiResponse<List<VmStorageAttachment>>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
        }

        internal static async Task<List<VmStorageAttachment>> FindStalePassthroughDisksAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return [];
            var response = await FindStalePassthroughDisksAsync(identity.Data);
            return response.Data ?? [];
        }

        private static List<VmStorageAttachment> BuildStorageItems(
            List<StorageResource> allResources,
            ref Dictionary<string, int>? hvDiskMap,
            ref Dictionary<int, HostDiskInfoCache>? osDiskMap,
            Guid vmId)
        {
            var resultList = new List<VmStorageAttachment>();
            var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);
            var deviceIdRegex = new Regex("DeviceID=\"([^\"]+)\"", RegexOptions.Compiled);

            var controllers = allResources
                .Where(res =>
                {
                    int rt = res.ResourceType;
                    return rt == 5 || rt == 6;
                })
                .OrderBy(c => c.ResourceType)
                .ToList();

            var childrenMap = new Dictionary<string, List<StorageResource>>();
            foreach (var res in allResources)
            {
                var parentPath = res.Parent;
                if (string.IsNullOrEmpty(parentPath)) continue;

                var match = parentRegex.Match(parentPath);
                if (!match.Success) continue;

                string parentId = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!childrenMap.ContainsKey(parentId))
                    childrenMap[parentId] = new List<StorageResource>();
                childrenMap[parentId].Add(res);
            }

            int scsiCounter = 0, ideCounter = 0;

            foreach (var ctrl in controllers)
            {
                string ctrlId = ctrl.InstanceId;
                int ctrlTypeVal = Convert.ToInt32(ctrl.ResourceType);
                string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                int ctrlNum = ctrlType == "SCSI" ? scsiCounter++ : ideCounter++;

                if (!childrenMap.TryGetValue(ctrlId, out var slots)) continue;

                foreach (var slot in slots)
                {
                    int resType = Convert.ToInt32(slot.ResourceType);
                    if (resType != 16 && resType != 17) continue;

                    string address = slot.AddressOnParent;
                    int location = int.TryParse(address, out int loc) ? loc : 0;

                    string slotId = slot.InstanceId;
                    StorageResource? media = null;
                    if (childrenMap.TryGetValue(slotId, out var mediaList))
                    {
                        media = mediaList.FirstOrDefault(m =>
                        {
                            int t = Convert.ToInt32(m.ResourceType);
                            return t == 31 || t == 16 || t == 22;
                        });
                    }

                    var driveItem = new VmStorageAttachment
                    {
                        ControllerType = ctrlType,
                        ControllerNumber = ctrlNum,
                        ControllerLocation = location,
                        DriveType = resType == 16 ? "DvdDrive" : "HardDisk",
                        DiskType = "Empty"
                    };

                    var slotHostRes = slot.HostResources;
                    var effectiveMedia = media ?? (slotHostRes?.Length > 0 ? slot : null);

                    if (effectiveMedia != null)
                    {
                        var hostRes = effectiveMedia.HostResources;
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
                                    {
                                        string devId = devMatch.Groups[1].Value.Replace("\\\\", "\\");
                                        // hvDiskMap 来自 Msvm_DiskDrive(只含脱机盘)。查得到=盘仍脱机、直通有效;
                                        // 已分配磁盘从直通池消失时，该直通配置已经失效。
                                        // 悬空时仍从 DeviceID 末尾(…\N)解析盘号(挂载时记录、不随联机变)，用于告诉用户是哪块盘失效——
                                        // 不能只靠 TryGetValue 的 out：查不到会把 dNum 置 0(默认值)，错映射到磁盘 0(常为系统盘)。
                                        if (!hvDiskMap.TryGetValue(devId, out dNum))
                                        {
                                            driveItem.IsPassthroughStale = true;
                                            var tail = Regex.Match(devId, @"\\(\d+)$");
                                            dNum = tail.Success ? int.Parse(tail.Groups[1].Value) : -1;
                                        }
                                    }
                                    else if (rawPath.ToUpper().Contains("PHYSICALDRIVE"))
                                    {
                                        var numMatch = Regex.Match(rawPath, @"PHYSICALDRIVE(\d+)",
                                            RegexOptions.IgnoreCase);
                                        if (numMatch.Success)
                                            dNum = int.Parse(numMatch.Groups[1].Value);
                                    }

                                    // 悬空且解析不出盘号(NODRIVE)时 dNum=-1，显式落到 DiskNumber：
                                    // UI 走通用文案，移除时也不会把默认值 0 误当主机磁盘 0 去联机。
                                    driveItem.DiskNumber = dNum;
                                    if (dNum != -1)
                                    {
                                        driveItem.PathOrDiskNumber = $"PhysicalDisk{dNum}";
                                        if (osDiskMap != null && osDiskMap.TryGetValue(dNum, out var hostInfo))
                                        {
                                            driveItem.DiskModel = hostInfo.Model;
                                            driveItem.SerialNumber = hostInfo.SerialNumber;
                                            driveItem.DiskSizeGiB = hostInfo.SizeGiB;
                                        }
                                    }
                                }
                                catch (CoreOperationException) { throw; }
                                catch (Exception ex)
                                {
                                    throw new CoreOperationException(ApiResponse.Fail(
                                        "读取物理磁盘挂接身份失败。", exception: ex, kind: ApiErrorKind.OperationFailed));
                                }
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
                                        driveItem.DiskSizeGiB =
                                            new FileInfo(driveItem.PathOrDiskNumber).Length / 1073741824.0;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }

                    var selectedMedia = childrenMap.TryGetValue(slotId, out var attachedMedia)
                        ? attachedMedia.Where(item => item.ResourceType is 31 or 16 or 22)
                            .GroupBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.First().Identity()).ToArray()
                        : [];
                    HostDiskSnapshot? physicalIdentity = null;
                    if (driveItem.DiskType == "Physical" && driveItem.DriveType == "HardDisk" &&
                        osDiskMap != null && osDiskMap.TryGetValue(driveItem.DiskNumber, out var physical))
                    {
                        physicalIdentity = physical.Identity;
                    }
                    driveItem.AttachSelection(new VmStorageSelection(vmId, ctrl.Identity(), slot.Identity(),
                        selectedMedia, physicalIdentity));
                    resultList.Add(driveItem);
                }
            }

            return resultList;
        }

        private static (Dictionary<string, int> hvDiskMap, Dictionary<int, HostDiskInfoCache> osDiskMap)
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

            foreach (var (did, dnum) in RequireDiskMap(hvResp))
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

            foreach (var (idx, model, serial, sizeBytes) in RequireDiskMap(osResp))
                    if (idx >= 0)
                        osMap[idx] = new HostDiskInfoCache
                        {
                            Model = model,
                            SerialNumber = serial,
                            SizeGiB = Math.Round(sizeBytes / 1073741824.0, 2)
                        };

            // 选择身份与执行时复核统一使用 MSFT_Disk，不能混比不同 provider 的序列号格式。
            // 每次物理盘清单只批量读取一次，显示型号仍沿用 Win32_DiskDrive。
            var identities = WmiApi.QueryCimAsync(
                "SELECT Number, UniqueId, SerialNumber, Size, IsOffline, IsReadOnly, IsSystem, IsBoot FROM MSFT_Disk",
                obj => new HostDiskSnapshot(Convert.ToInt32(obj["Number"]), obj["UniqueId"]?.ToString() ?? "",
                    obj["SerialNumber"]?.ToString()?.Trim() ?? "", Convert.ToInt64(obj["Size"]),
                    Convert.ToBoolean(obj["IsOffline"]), Convert.ToBoolean(obj["IsReadOnly"]),
                    Convert.ToBoolean(obj["IsSystem"]), Convert.ToBoolean(obj["IsBoot"])),
                WmiScope.Storage).GetAwaiter().GetResult();
            foreach (var identity in RequireDiskMap(identities))
                {
                    if (!osMap.TryGetValue(identity.Number, out var info))
                        osMap[identity.Number] = info = new HostDiskInfoCache();
                    info.Identity = identity;
                }

            return (hvMap, osMap);
        }

        internal static IReadOnlyList<T> RequireDiskMap<T>(ApiResponse<List<T>> response)
        {
            if (!response.Success)
                throw new CoreOperationException(ApiResponse.Fail(response.Error, response.Code,
                    response.ErrorSource, response.Exception, response.ErrorKind));
            return response.Data ?? [];
        }

        // 压缩虚拟磁盘

        /// <summary>请求 Hyper-V 压缩虚拟磁盘文件的物理占用。</summary>
        /// <param name="vhdPath">待压缩的虚拟磁盘文件路径。</param>
        /// <returns>压缩结果；操作改变磁盘文件，能否执行取决于主机和磁盘当前占用状态。</returns>
    /// <editentry label="压缩虚拟磁盘" category="存储"/>
        public static async Task<ApiResponse> CompactDiskAsync(string vhdPath)
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

        // 主机物理磁盘列表

        // 刷新虚拟磁盘文件大小

        /// <summary>批量读取磁盘文件当前的文件长度，不是虚拟磁盘声明的最大容量。</summary>
        /// <param name="paths">待查询文件长度的磁盘路径集合。</param>
        /// <returns>路径到文件字节数的映射；未读取成功的路径不在结果中，不应将缺少的项视为零字节。</returns>
        public static Task<IReadOnlyDictionary<string, long>> GetVirtualDiskSizesAsync(IEnumerable<string> paths)
        {
            var requestedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return Task.Run<IReadOnlyDictionary<string, long>>(() =>
            {
                var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in requestedPaths)
                {
                    try { if (File.Exists(path)) result[path] = new FileInfo(path).Length; }
                    catch { }
                }
                return result;
            });
        }

        // 设备增删改操作

        /// <summary>按请求创建或挂接虚拟磁盘、光驱或物理磁盘，必要时创建控制器并调整槽位。</summary>
        /// <param name="vmName">目标虚拟机的显示名称，不是 GUID。</param>
        /// <param name="controllerType">IDE 或 SCSI，区分当前实现使用的类型字符串。</param>
        /// <param name="controllerNumber">从零开始的控制器编号；IDE 为 0 至 1，SCSI 为 0 至 3。</param>
        /// <param name="location">从零开始的控制器槽位；IDE 为 0 至 1，SCSI 为 0 至 63。</param>
        /// <param name="driveType">驱动类型 HardDisk 或 DvdDrive。</param>
        /// <param name="pathOrNumber">虚拟介质路径或物理设备编号字符串；新建 ISO 时用作输出路径。</param>
        /// <param name="isPhysical">是否挂接宿主物理设备，而非虚拟介质文件。</param>
        /// <param name="isNew">是否先创建新 VHD 或 ISO；默认 false 表示复用现有来源。</param>
        /// <param name="sizeGb">新虚拟磁盘容量，单位 GB；默认 256，非新建磁盘时不使用。</param>
        /// <param name="vhdType">新磁盘类型字符串，默认 Dynamic；差分磁盘使用 Differencing。</param>
        /// <param name="parentPath">新差分磁盘的父磁盘路径；其他模式可为空字符串。</param>
        /// <param name="sectorFormat">新磁盘扇区格式选择字符串；Default 使用创建器默认配置。</param>
        /// <param name="blockSize">新磁盘块大小选择字符串；Default 使用创建器默认配置。</param>
        /// <param name="isoSourcePath">制作新 ISO 的来源目录；无需制作 ISO 时可为 null。</param>
        /// <param name="isoVolumeLabel">新 ISO 的卷标；null 时由制作器采用默认卷标。</param>
        /// <param name="expectedDiskUniqueId">物理磁盘预期唯一标识，用于防止编号变化后误选；null 时不提供该身份约束。</param>
        /// <param name="expectedDiskSerial">物理磁盘预期序列号；null 时不提供该身份约束。</param>
        /// <param name="expectedDiskSize">物理磁盘预期字节容量；0 表示未提供。</param>
        /// <returns>成功标记、说明及实际控制器类型、编号、槽位；失败时尝试清理本次新增资源，并按记录恢复物理磁盘的联机和只读状态。</returns>
        /// <remarks>物理磁盘路径可能要求将宿主磁盘置为离线或修改只读状态；新介质可能创建 VHD、差分盘或 ISO。身份参数用于防止宿主磁盘编号变化后连接错误设备。返回的 ActualType、ActualNumber、ActualLocation 才是最终挂接位置。</remarks>
        internal static async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)>
            AddDriveAsync(
                string vmName, string controllerType, int controllerNumber, int location, string driveType,
                string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256,
                string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default",
                string blockSize = "Default", string isoSourcePath = null, string isoVolumeLabel = null,
                string expectedDiskUniqueId = null, string expectedDiskSerial = null, long expectedDiskSize = 0)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return (false, identity.Error, controllerType, controllerNumber, location);
            uint? parsedBlockSize = blockSize == "Default" ? null
                : uint.TryParse(blockSize, out uint bytes) ? bytes : 0;
            var request = new VmStorageAddRequest
            {
                ControllerType = controllerType, ControllerNumber = controllerNumber, Location = location,
                DriveType = driveType, PathOrNumber = pathOrNumber, IsPhysical = isPhysical, IsNew = isNew,
                VhdType = vhdType, ParentPath = parentPath, IsoSourcePath = isoSourcePath,
                IsoOutputPath = isNew && driveType == "DvdDrive" ? pathOrNumber : null, IsoVolumeLabel = isoVolumeLabel,
                SizeGiB = isNew && driveType == "HardDisk" && vhdType != "Differencing" ? sizeGb : null,
                SectorFormat = sectorFormat, BlockSizeBytes = parsedBlockSize,
                PhysicalDisk = isPhysical && driveType == "HardDisk" ? new HostDiskInfo
                {
                    Number = int.TryParse(pathOrNumber, out int diskNumber) ? diskNumber : -1,
                    UniqueId = expectedDiskUniqueId ?? string.Empty, SerialNumber = expectedDiskSerial ?? string.Empty,
                    SizeBytes = expectedDiskSize
                } : null
            };
            var response = await AddDriveAsync(identity.Data, request);
            return response.HasData
                ? (true, string.Empty, response.Data.ControllerType, response.Data.ControllerNumber, response.Data.Location)
                : (false, response.Error, controllerType, controllerNumber, location);
        }

        /// <summary>按完整请求创建或挂接虚拟磁盘、光驱或物理磁盘，并返回实际挂接位置。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID；执行和槽位回查均按此身份定位。</param>
        /// <param name="request">完整来源、创建选项及目标槽位。物理硬盘必须携带查询所得身份和字节容量，提交时重新核对。</param>
        /// <returns>成功时 Data 为实际槽位；失败时 ErrorKind 区分输入错误、对象缺失、身份冲突或执行失败。</returns>
        /// <remarks>不会仅凭物理磁盘编号挂接；系统盘和启动盘始终拒绝。失败清理只接受本次创建回执确认的资源，不按数量变化或槽位推断归属。作业结果或资源归属不明时保留资源并返回 OutcomeUnknown；已确认失败但存在外来子设备或清理未完成时返回 CleanupRequired。两种情况下都不将可能仍在用的物理盘恢复联机。设备查询与系统调用不构成原子硬件锁。</remarks>
    /// <editentry label="添加存储设备" category="存储"/>
        public static Task<ApiResponse<VmStorageSlot>> AddDriveAsync(Guid vmId, VmStorageAddRequest request)
            => AddDriveAsync(vmId, request, onVhdCreated: null);

        internal static async Task<ApiResponse<VmStorageSlot>> AddDriveAsync(
            Guid vmId, VmStorageAddRequest request, Action<string>? onVhdCreated)
        {
            var validation = ValidateAddRequest(request);
            if (!validation.Success)
                return ApiResponse<VmStorageSlot>.Fail(validation.Error, kind: validation.ErrorKind);
            request = request with { PhysicalDisk = SnapshotPhysicalDisk(request.PhysicalDisk) };
            try
            {
                return request.IsPhysical && request.DriveType == "HardDisk"
                    ? await HostDiskService.WithDiskLockAsync(request.PhysicalDisk!.Number,
                        () => AddDriveUnderLockAsync(vmId, request, onVhdCreated))
                    : await AddDriveUnderLockAsync(vmId, request, onVhdCreated);
            }
            catch (Exception error)
            {
                var failure = HostDiskService.LockFailure(error);
                return ApiResponse<VmStorageSlot>.Fail(failure.Error, failure.Code, failure.ErrorSource, error, failure.ErrorKind);
            }
        }

        internal static async Task<ApiResponse<VmStorageSlot>> AddDriveUnderLockAsync(
            Guid vmId, VmStorageAddRequest request, Action<string>? onVhdCreated = null)
        {
            if (vmId == Guid.Empty)
                return ApiResponse<VmStorageSlot>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var validation = ValidateAddRequest(request);
            if (!validation.Success)
                return ApiResponse<VmStorageSlot>.Fail(validation.Error, kind: validation.ErrorKind);
            request = request with { PhysicalDisk = SnapshotPhysicalDisk(request.PhysicalDisk) };
            var prepared = PrepareAddSource(request);
            if (!prepared.Success)
                return ApiResponse<VmStorageSlot>.Fail(prepared.Error!, kind: prepared.IsError ? ApiErrorKind.OperationFailed : ApiErrorKind.InvalidArgument);
            request = prepared.Request!;
            if (request.IsPhysical && request.DriveType == "HardDisk")
                HostResourceLock.AssertHeld(HostDiskResource.Disk(request.PhysicalDisk!.Number));
            string controllerType = request.ControllerType, driveType = request.DriveType;
            int controllerNumber = request.ControllerNumber, location = request.Location;
            string pathOrNumber = request.PathOrNumber!;
            bool isPhysical = request.IsPhysical, isNew = request.IsNew;

            int physicalDiskNumberForRollback = -1;
            bool physicalStateCaptured = false;
            bool originalIsOffline = false;
            bool originalIsReadOnly = false;
            ManagementObject? settings = null;
            var ownership = new VmStorageCreationOwnership();
            int slotResourceType = driveType == "DvdDrive" ? 16 : 17;

            async Task<ApiResponse> RestorePhysicalDiskStateAsync()
            {
                if (!physicalStateCaptured || physicalDiskNumberForRollback < 0) return ApiResponse.Ok();
                return await RestorePhysicalDiskAsync(request.PhysicalDisk!, originalIsReadOnly, originalIsOffline,
                    ReadPhysicalDiskAsync, HostDiskService.SetDiskReadOnlyCoreAsync, HostDiskService.SetDiskOfflineStatusCoreAsync);
            }

            async Task<ApiResponse<VmStorageSlot>> Fail(string msg, ApiErrorKind kind = ApiErrorKind.OperationFailed, Exception? exception = null)
            {
                // 已提交操作的结果不明时，既不能撤销挂接，也不能恢复可能仍在用的物理盘。
                ownership.IsUncertain |= kind == ApiErrorKind.OutcomeUnknown;
                var rollback = await ownership.RollbackAsync(
                    () => ReadCreatedStorageResourcesAsync(settings!),
                    path => WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService",
                        "RemoveResourceSettings", p => p["ResourceSettings"] = new[] { path }),
                    path => { File.Delete(path); return Task.CompletedTask; });
                // 仍可能存在挂接或原生任务时，不能把已交给 VM 的物理盘恢复为宿主联机。
                var restore = rollback.Success ? await RestorePhysicalDiskStateAsync() : ApiResponse.Ok();
                var cleanupErrors = new[] { rollback.Success ? null : rollback.Error, restore.Success ? null : restore.Error }
                    .Where(error => !string.IsNullOrEmpty(error));
                return ApiResponse<VmStorageSlot>.Fail(string.Join(" ", new[] { msg }.Concat(cleanupErrors!)),
                    exception: exception, kind: kind == ApiErrorKind.OutcomeUnknown ||
                        rollback.ErrorKind == ApiErrorKind.OutcomeUnknown || restore.ErrorKind == ApiErrorKind.OutcomeUnknown
                        ? ApiErrorKind.OutcomeUnknown
                        : rollback.Success && restore.Success ? kind : ApiErrorKind.CleanupRequired);
            }

            async Task<VmCreatedStorageResource?> RecordCreationAsync(WmiCreationResult result, int resourceType,
                string resourceSubType, string? parent = null, string? address = null, string[]? hostResources = null)
            {
                if (result.CreatedPaths.Length == 0)
                    return ownership.RecordCreation(result, null, resourceType);
                try
                {
                    var current = await ReadCreatedStorageResourcesAsync(settings!);
                    var created = ownership.RecordCreation(result, current.Success ? current.Data : null, resourceType);
                    if (created != null && !VmStorageCreationOwnership.MatchesConfiguration(created, created with
                    {
                        ResourceSubType = resourceSubType,
                        Parent = parent ?? created.Parent,
                        AddressOnParent = address ?? created.AddressOnParent,
                        HostResources = hostResources ?? created.HostResources
                    }))
                    {
                        ownership.IsUncertain = true;
                        return null;
                    }
                    return created;
                }
                catch { ownership.IsUncertain = true; throw; }
            }

            try
            {
                using var vmObj = WmiApi.GetVmComputerSystem(vmId);
                if (vmObj == null)
                    return ApiResponse<VmStorageSlot>.Fail(string.Format(Properties.Resources.Error_Vm_NotFound, vmId), kind: ApiErrorKind.NotFound);

                int enabledState = WmiApi.Prop<int>(vmObj, "EnabledState", 0);
                bool isRunning = enabledState == 2;

                settings = WmiApi.GetVmSettings(vmObj);
                if (settings == null)
                    return ApiResponse<VmStorageSlot>.Fail(Properties.Resources.Error_Vm_GetSettings, kind: ApiErrorKind.NotFound);

                // The displayed slot is a proposal. Re-read occupancy at submission,
                // before adding controllers, creating a VHD or changing physical disks.
                var inventory = await GetVmStorageItemsAsync(vmId);
                if (!inventory.HasData)
                    return ApiResponse<VmStorageSlot>.Fail(inventory.Error, inventory.Code, inventory.ErrorSource, inventory.Exception, inventory.ErrorKind);
                string subtype = settings["VirtualSystemSubType"]?.ToString() ?? string.Empty;
                int generation = subtype.EndsWith(":1") ? 1 : 2;
                string? slotError = GetAddSlotError(generation, VmMapper.IsActiveState((ushort)enabledState), request, inventory.Data!);
                if (slotError != null)
                    return ApiResponse<VmStorageSlot>.Fail(slotError, kind: ApiErrorKind.InvalidState);

                if (isPhysical && driveType == "DvdDrive")
                {
                    if (generation != 1)
                        return ApiResponse<VmStorageSlot>.Fail("物理光驱仅支持第一代虚拟机。", kind: ApiErrorKind.Unsupported);
                    var optical = await HostDiskService.GetHostOpticalDrivesAsync();
                    if (!optical.Success)
                        return ApiResponse<VmStorageSlot>.Fail(optical.Error, optical.Code, optical.ErrorSource, optical.Exception, optical.ErrorKind);
                    if (!optical.Data!.Any(item => string.Equals(item.PnpDeviceId, pathOrNumber, StringComparison.OrdinalIgnoreCase)))
                        return ApiResponse<VmStorageSlot>.Fail("所选物理光驱已不存在，请重新查询。", kind: ApiErrorKind.NotFound);
                }

                if (driveType == "DvdDrive" && isNew)
                {
                    var isoResult = await CreateIsoFromDirectoryAsync(request.IsoSourcePath!, pathOrNumber, request.IsoVolumeLabel);
                    if (!isoResult.Success)
                        return ApiResponse<VmStorageSlot>.Fail(isoResult.Message, kind: ApiErrorKind.OperationFailed);
                }

                // IDE 不支持热添加设备，服务层也需要校验运行状态。
                if (controllerType == "IDE" && isRunning)
                    return ApiResponse<VmStorageSlot>.Fail(driveType == "DvdDrive" ? Properties.Resources.Error_Storage_Gen1Dvd : Properties.Resources.Error_Storage_IdeHotAdd, kind: ApiErrorKind.InvalidState);

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
                        if (request.ExpectedControllerId != null)
                            return await Fail("原控制器已不存在，未创建替代控制器。");
                        if (isRunning)
                            return ApiResponse<VmStorageSlot>.Fail(Properties.Resources.Error_Storage_ScsiHotAdd, kind: ApiErrorKind.InvalidState);

                        string? scsiErr = null;
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

                            var addScsiResult = await WmiApi.InvokeCreationAsync(
                                "SELECT * FROM Msvm_VirtualSystemManagementService",
                                "AddResourceSettings",
                                "Msvm_ResourceAllocationSettingData",
                                p =>
                                {
                                    p["AffectedConfiguration"] = settings.Path.Path;
                                    p["ResourceSettings"] = new string[] { scsiXml };
                                });
                            var createdController = await RecordCreationAsync(addScsiResult, 6,
                                "Microsoft:Hyper-V:Synthetic SCSI Controller");

                            if (!addScsiResult.Response.Success || createdController == null || ownership.IsUncertain)
                            {
                                scsiErr = addScsiResult.Response.Success
                                    ? "新增控制器的身份尚未确认。" : FriendlyError.LastSentence(addScsiResult.Response.Error);
                                break;
                            }
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

                        if (scsiErr != null)
                            return await Fail(scsiErr);
                    }

                    if (controllerNumber >= scsiCtrlList.Count)
                        return await Fail(Properties.Resources.Error_Storage_NoScsi);
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
                    var ctrl = ctrlRasdResp.Data?.FirstOrDefault(r =>
                    {
                        if (r.ResourceType != 5) return false;
                        var segs = r.InstanceID.Split('\\');
                        return segs.Length >= 1
                            && int.TryParse(segs[^1], out int n)
                            && n == controllerNumber;
                    });
                    controllerPath = ctrl?.ObjPath;
                }
                else
                {
                    var scsiList = ctrlRasdResp.Data?
                        .Where(r => r.ResourceType == 6)
                        .ToList();
                    var ctrl = scsiList?.ElementAtOrDefault(controllerNumber);
                    controllerPath = ctrl?.ObjPath;
                }

                if (controllerPath == null)
                    return await Fail(Properties.Resources.Error_Storage_ControllerNotFound);

                if (request.ExpectedControllerId != null)
                {
                    using var expectedController = new ManagementObject(controllerPath);
                    expectedController.Get();
                    if (!string.Equals(expectedController["InstanceID"]?.ToString(), request.ExpectedControllerId, StringComparison.OrdinalIgnoreCase))
                        return await Fail("原控制器身份已变化，未向替代控制器重新挂接。");
                }

                if (driveType == "HardDisk" && isNew && !string.IsNullOrWhiteSpace(pathOrNumber))
                {
                    var createResult = await CreateVhdAsync(
                        pathOrNumber, request.VhdType, request.SizeGiB ?? 0, request.SectorFormat,
                        request.BlockSizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Default", request.ParentPath);
                    ownership.IsUncertain |= createResult.OutcomeUnknown;
                    if (!createResult.Response.Success || ownership.IsUncertain)
                        return await Fail(FriendlyError.LastSentence(createResult.Response.Error));
                    ownership.VhdPath = pathOrNumber;
                    onVhdCreated?.Invoke(pathOrNumber);
                    // 创建事务接管文件后只由它删除；避免内外两次清理之间误删同路径的新文件。
                    if (onVhdCreated != null) ownership.VhdPath = null;
                }

                string slotSubType = driveType == "DvdDrive"
                    ? "Microsoft:Hyper-V:Synthetic DVD Drive"
                    : (isPhysical
                        ? "Microsoft:Hyper-V:Physical Disk Drive"
                        : "Microsoft:Hyper-V:Synthetic Disk Drive");

                string? physicalHostResource = null;
                if (isPhysical && driveType == "HardDisk")
                {
                    if (!int.TryParse(pathOrNumber, out int physicalDiskNumber))
                        return await Fail(Properties.Resources.Error_Storage_SelectTarget);

                    var stateResp = await ReadPhysicalDiskAsync(physicalDiskNumber);
                    if (!stateResp.Success) return await Fail(stateResp.Error, stateResp.ErrorKind);
                    var selection = ValidatePhysicalDiskSelection(request.PhysicalDisk!, stateResp.Data);
                    if (!selection.Success) return await Fail(selection.Error, selection.ErrorKind);

                    physicalDiskNumberForRollback = physicalDiskNumber;
                    originalIsOffline = stateResp.Data!.IsOffline;
                    originalIsReadOnly = stateResp.Data.IsReadOnly;
                    physicalStateCaptured = true;

                    if (!stateResp.Data.IsOffline)
                    {
                        var offlineResult = await HostDiskService.SetDiskOfflineStatusCoreAsync(physicalDiskNumber, true);
                        if (!offlineResult.Success)
                            return await Fail(FriendlyError.LastSentence(offlineResult.Error), offlineResult.ErrorKind, offlineResult.Exception);
                    }

                    string? diskDrivePath = null;
                    string? diskDeviceId = null;
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        var diskDriveResp = await WmiApi.QueryFirstAsync(
                            $"SELECT * FROM Msvm_DiskDrive WHERE DriveNumber = {physicalDiskNumber}",
                            obj => new
                            {
                                Path = obj["__PATH"]?.ToString() ?? obj.Path.Path,
                                DeviceId = obj["DeviceID"]?.ToString() ?? string.Empty
                            },
                            WmiScope.HyperV);

                        if (diskDriveResp.HasData)
                        {
                            diskDrivePath = diskDriveResp.Data!.Path;
                            diskDeviceId = diskDriveResp.Data.DeviceId.Replace("\\\\", "\\");
                            break;
                        }
                        await Task.Delay(250);
                    }

                    if (string.IsNullOrEmpty(diskDrivePath) || string.IsNullOrEmpty(diskDeviceId))
                        return await Fail(string.Format(Properties.Resources.Error_Storage_PhysDiskNotFound, pathOrNumber));

                    var assignedResp = await WmiApi.QueryAsync(
                        "SELECT HostResource FROM Msvm_ResourceAllocationSettingData WHERE ResourceSubType = 'Microsoft:Hyper-V:Physical Disk Drive'",
                        obj => (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty,
                        WmiScope.HyperV);
                    if (!assignedResp.Success) return await Fail(assignedResp.Error, assignedResp.ErrorKind);
                    bool alreadyAssigned = (assignedResp.Data ?? [])
                        .Where(hr => !string.IsNullOrEmpty(hr))
                        .Select(hr => System.Text.RegularExpressions.Regex.Match(hr, "DeviceID=\"([^\"]+)\""))
                        .Any(match => match.Success && string.Equals(
                            match.Groups[1].Value.Replace("\\\\", "\\"), diskDeviceId,
                            StringComparison.OrdinalIgnoreCase));
                    if (alreadyAssigned)
                        return await Fail(Properties.Resources.Error_Storage_DiskAlreadyAssigned, ApiErrorKind.Conflict);

                    // 等待 Hyper-V 设备枚举期间可能发生热插拔，再次拒绝编号被替换的设备。
                    var latest = await ReadPhysicalDiskAsync(physicalDiskNumber);
                    if (!latest.Success) return await Fail(latest.Error, latest.ErrorKind);
                    var latestSelection = ValidatePhysicalDiskSelection(request.PhysicalDisk!, latest.Data);
                    if (!latestSelection.Success) return await Fail(latestSelection.Error, latestSelection.ErrorKind);

                    physicalHostResource = diskDrivePath;
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

                var addSlotResult = await WmiApi.InvokeCreationAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "AddResourceSettings",
                    "Msvm_ResourceAllocationSettingData",
                    p =>
                    {
                        p["AffectedConfiguration"] = settings.Path.Path;
                        p["ResourceSettings"] = new string[] { slotXml };
                    });
                var createdSlot = await RecordCreationAsync(addSlotResult, slotResourceType, slotSubType,
                    controllerPath, location.ToString(), physicalHostResource == null ? [] : [physicalHostResource]);

                if (!addSlotResult.Response.Success)
                    return await Fail(FriendlyError.LastSentence(addSlotResult.Response.Error));
                if (createdSlot == null || ownership.IsUncertain)
                    return await Fail(Properties.Resources.Error_Storage_NoSlots);
                string slotPath = createdSlot.Path;

                // 物理光驱直通也走 SASD(HostResource = 主机光驱 PNPDeviceID)，与挂 ISO 同路径；物理硬盘的 HostResource 在 slot 上、不经此处。
                bool isPhysicalOptical = isPhysical && driveType == "DvdDrive";
                bool hasMedia = (!isPhysical || isPhysicalOptical) && !string.IsNullOrWhiteSpace(pathOrNumber);
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

                    var addMediaResult = await WmiApi.InvokeCreationAsync(
                        "SELECT * FROM Msvm_VirtualSystemManagementService",
                        "AddResourceSettings",
                        "Msvm_StorageAllocationSettingData",
                        p =>
                        {
                            p["AffectedConfiguration"] = settings.Path.Path;
                            p["ResourceSettings"] = new string[] { sasdXml };
                        });
                    var createdMedia = await RecordCreationAsync(addMediaResult, 31, mediaSubType,
                        slotPath, hostResources: [pathOrNumber]);

                    if (!addMediaResult.Response.Success)
                        return await Fail(FriendlyError.LastSentence(addMediaResult.Response.Error));
                    if (createdMedia == null || ownership.IsUncertain)
                        return await Fail("新增介质的身份尚未确认。");
                }

                return ApiResponse<VmStorageSlot>.Ok(new(controllerType, controllerNumber, location));
            }
            catch (Exception ex)
            {
                return await Fail(FriendlyError.LastSentence(ex.Message), exception: ex);
            }
            finally { settings?.Dispose(); }
        }

        private static Task<ApiResponse<PhysicalDiskState>> ReadPhysicalDiskAsync(int number) => WmiApi.QueryFirstCimAsync(
            $"SELECT IsSystem, IsBoot, IsOffline, IsReadOnly, UniqueId, SerialNumber, Size FROM MSFT_Disk WHERE Number = {number}",
            obj => new PhysicalDiskState(Convert.ToBoolean(obj["IsSystem"] ?? false), Convert.ToBoolean(obj["IsBoot"] ?? false),
                Convert.ToBoolean(obj["IsOffline"] ?? false), Convert.ToBoolean(obj["IsReadOnly"] ?? false),
                obj["UniqueId"]?.ToString() ?? string.Empty, obj["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
                Convert.ToInt64(obj["Size"] ?? 0L)), WmiScope.Storage);

        private static async Task<ApiResponse<List<VmCreatedStorageResource>>> ReadCreatedStorageResourcesAsync(ManagementObject settings)
        {
            static VmCreatedStorageResource Map(ManagementObject obj) => new(
                obj["InstanceID"]?.ToString() ?? string.Empty, obj.Path.Path,
                Convert.ToInt32(obj["ResourceType"] ?? 0), obj["Parent"]?.ToString() ?? string.Empty,
                obj["ResourceSubType"]?.ToString() ?? string.Empty, obj["AddressOnParent"]?.ToString() ?? string.Empty,
                (obj["HostResource"] as string[])?.ToArray() ?? []);

            // 两个 Msvm 类分别继承 CIM 的资源/存储配置类，查询前者不能代表后者的完整清单。
            var resources = await WmiApi.QueryRelatedAsync(settings, "Msvm_ResourceAllocationSettingData", Map,
                "Msvm_VirtualSystemSettingDataComponent", WmiScope.HyperV);
            if (!resources.Success) return resources;
            if (resources.Data == null)
                return ApiResponse<List<VmCreatedStorageResource>>.Fail("未取得完整存储资源清单。", kind: ApiErrorKind.CleanupRequired);
            var media = await WmiApi.QueryRelatedAsync(settings, "Msvm_StorageAllocationSettingData", Map,
                "Msvm_VirtualSystemSettingDataComponent", WmiScope.HyperV);
            if (!media.Success) return media;
            if (media.Data == null)
                return ApiResponse<List<VmCreatedStorageResource>>.Fail("未取得完整存储介质清单。", kind: ApiErrorKind.CleanupRequired);
            return ApiResponse<List<VmCreatedStorageResource>>.Ok([.. resources.Data, .. media.Data]);
        }

        private static async Task<WmiCreationResult> CreateVhdAsync(
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

                return await WmiApi.InvokeCreationAsync(
                    "SELECT * FROM Msvm_ImageManagementService",
                    "CreateVirtualHardDisk",
                    expectedResultClass: null,
                    p => p["VirtualDiskSettingData"] = vhdXml,
                    resultField: null);
            }
            catch (Exception ex)
            {
                return new(ApiResponse.Fail(FriendlyError.LastSentence(ex.Message), exception: ex), [], false);
            }
        }

        /// <summary>移除指定存储挂接及必要的关联设备，不将普通虚拟硬盘文件作为删除对象。</summary>
        /// <param name="vmName">目标虚拟机的显示名称，不是 GUID。</param>
        /// <param name="drive">由存储查询取得的目标挂接对象，含实例标识及控制器位置。</param>
        /// <returns>Success 表示设备移除完成，Message 为结果或失败原因。</returns>
        internal static async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, VmStorageAttachment drive)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return (false, identity.Error);
            var response = await RemoveDriveAsync(identity.Data, drive);
            return (response.Success, response.Success ? Properties.Resources.Msg_Storage_Removed : response.Error);
        }

        /// <summary>从指定虚拟机移除查询时选中的存储挂接和介质；不删除虚拟磁盘文件。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID，必须与产生选择摘要的查询一致。</param>
        /// <param name="drive">存储查询返回的设备，须原样回传 SelectionToken；可序列化后回传。位置及显示字段不作为替代定位依据。</param>
        /// <returns>移除结果；旧摘要、设备或介质已变化时返回冲突。失败可能发生在介质已移除但槽位仍保留之后。</returns>
        /// <remarks>缺少身份摘要不会退回按槽位移除。执行前再次核对原对象；WMI 不提供原子比较删除，不能保证识别外部在最终核对后替换或按完全相同配置重建的对象。</remarks>
    /// <editentry label="移除存储设备" category="存储"/>
        public static async Task<ApiResponse> RemoveDriveAsync(Guid vmId, VmStorageAttachment drive)
        {
            if (vmId == Guid.Empty || drive == null || string.IsNullOrWhiteSpace(drive.SelectionToken))
                return ApiResponse.Fail("请提供目标虚拟机及存储查询返回的 SelectionToken。", kind: ApiErrorKind.InvalidArgument);
            // 在第一次 await 前固定选择；调用方随后修改展示模型不会转移操作目标。
            string token = drive.SelectionToken;
            var selected = VmStorageRemoval.Select(vmId, token, await GetVmStorageItemsAsync(vmId));
            if (!selected.HasData) return VmStorageRemoval.Failure(selected);
            var current = selected.Data!;
            if (current.DiskType != "Physical" || current.DriveType != "HardDisk" || current.DiskNumber < 0)
                return await RemoveDriveCoreAsync(vmId, current);
            try
            {
                return await HostDiskService.WithDiskLockAsync(current.DiskNumber, async () =>
                {
                    var refreshed = VmStorageRemoval.Select(vmId, token, await GetVmStorageItemsAsync(vmId));
                    if (!refreshed.HasData) return VmStorageRemoval.Failure(refreshed);
                    return await RemoveDriveCoreAsync(vmId, refreshed.Data!);
                });
            }
            catch (Exception error) { return HostDiskService.LockFailure(error); }
        }

        private static async Task<ApiResponse> RemoveDriveCoreAsync(Guid vmId, VmStorageAttachment drive)
        {
            if (drive.Selection is not { IsValid: true } selection || selection.VmId != vmId)
                return ApiResponse.Fail("存储挂接身份不完整，请重新查询。", kind: ApiErrorKind.InvalidArgument);
            HostDiskSnapshot? originalDisk = null;
            if (drive.DiskType == "Physical" && drive.DriveType == "HardDisk" && drive.DiskNumber >= 0)
            {
                HostResourceLock.AssertHeld(HostDiskResource.Disk(drive.DiskNumber));
                var snapshot = await HostDiskService.ReadDiskSnapshotAsync(drive.DiskNumber);
                if (!snapshot.HasData || !snapshot.Data!.SameDevice(snapshot.Data) ||
                    selection.PhysicalDisk?.SameDevice(snapshot.Data) != true)
                    return ApiResponse.Fail("无法确认查询时的物理磁盘身份，未移除挂接。", kind: ApiErrorKind.Conflict);
                originalDisk = snapshot.Data;
            }
            var vmResp = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => Convert.ToInt32(obj["EnabledState"] ?? 0),
                WmiScope.HyperV);

            if (!vmResp.HasData)
                return ApiResponse.Fail(string.Format(Properties.Resources.Error_Vm_NotFound, vmId), kind: ApiErrorKind.NotFound);

            bool isRunning = vmResp.Data == 2;

            if (drive.DriveType == "DvdDrive" &&
                isRunning &&
                drive.ControllerType == "IDE")
            {
                return ApiResponse.Fail(Properties.Resources.Error_Storage_DvdHotRemove, kind: ApiErrorKind.InvalidState);
            }

            // 运行中不能删 IDE 硬盘(IDE 不支持热插拔)——与添加的 IdeHotAdd 对称
            if (drive.DriveType == "HardDisk" && isRunning && drive.ControllerType == "IDE")
                return ApiResponse.Fail(Properties.Resources.Error_Storage_IdeHotRemove, kind: ApiErrorKind.InvalidState);

            var removal = await VmStorageRemoval.ExecuteAsync(selection,
                () => GetVmStorageItemsAsync(vmId),
                path => WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "RemoveResourceSettings",
                    p => p["ResourceSettings"] = new[] { path },
                    WmiScope.HyperV));
            if (!removal.Success) return removal;

            // 仅物理硬盘直通才需把主机盘还原上线；物理光驱(DvdDrive)也是 DiskType=="Physical" 但无关联磁盘号(默认 0)，
            // 不加 DriveType 限定会误把主机磁盘 0 上线。
            if (drive.DiskType == "Physical" && drive.DriveType == "HardDisk" && drive.DiskNumber > -1)
            {
                await Task.Delay(500);
                // Hyper-V 挂 pass-through 物理盘会把主机盘置为只读(独占保护)；拿回主机后必须清掉，
                // 否则主机看到的是只读盘、无法写(对齐 GPU 直通拿回 VmGpuService 的处理)。
                var latest = await HostDiskService.ReadDiskSnapshotAsync(drive.DiskNumber);
                if (!latest.HasData || originalDisk?.SameDevice(latest.Data!) != true)
                    return ApiResponse.Fail("磁盘身份已经改变，挂接已移除但未修改替代设备。", kind: ApiErrorKind.CleanupRequired);
                var writable = await HostDiskService.SetDiskReadOnlyCoreAsync(drive.DiskNumber, false);
                if (!writable.Success) return ApiResponse.Fail(writable.Error, writable.Code,
                    writable.ErrorSource, writable.Exception,
                    writable.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
                latest = await HostDiskService.ReadDiskSnapshotAsync(drive.DiskNumber);
                if (!latest.HasData || originalDisk?.SameDevice(latest.Data!) != true)
                    return ApiResponse.Fail("磁盘身份已经改变，未继续联机替代设备。", kind: ApiErrorKind.CleanupRequired);
                var online = await HostDiskService.SetDiskOfflineStatusCoreAsync(drive.DiskNumber, false);
                if (!online.Success) return ApiResponse.Fail(online.Error, online.Code,
                    online.ErrorSource, online.Exception,
                    online.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
            }

            return ApiResponse.Ok();
        }

        /// <summary>替换查询时选中的虚拟光驱介质；空路径弹出介质，保留驱动器。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="drive">存储查询返回的光驱，原样回传 DeviceToken；不以显示槽位重新定位。</param>
        /// <param name="newIsoPath">新的 ISO 文件路径；空字符串用于弹出介质。</param>
        /// <returns>更新结果；设备身份变化时返回 Conflict，未知底层结果不自动重试。</returns>
        public static Task<ApiResponse> ModifyDvdDrivePathAsync(Guid vmId, VmStorageAttachment drive, string newIsoPath)
            => ModifyMediaPathAsync(vmId, drive, "DvdDrive", "Microsoft:Hyper-V:Virtual CD/DVD Disk", newIsoPath);

        /// <summary>替换查询时选中的虚拟硬盘驱动器所引用的介质路径。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="drive">存储查询返回的硬盘驱动器，原样回传 DeviceToken。</param>
        /// <param name="newPath">新的虚拟磁盘路径；沿用介质入口的空路径移除介质行为，不删除文件。</param>
        /// <returns>更新结果；只修改原驱动器，不改同槽位的替代设备。</returns>
        /// <remarks>设备身份摘要不比较旧介质路径。同一驱动器仍采用本次提交覆盖；WMI 不提供跨查询和提交的原子身份锁，完全相同身份的外部重建无法区分。</remarks>
        public static Task<ApiResponse> ModifyHardDrivePathAsync(Guid vmId, VmStorageAttachment drive, string newPath)
            => ModifyMediaPathAsync(vmId, drive, "HardDisk", "Microsoft:Hyper-V:Virtual Hard Disk", newPath);

        private static async Task<ApiResponse> ModifyMediaPathAsync(Guid vmId, VmStorageAttachment drive,
            string driveType, string resourceSubType, string newPath)
        {
            if (vmId == Guid.Empty || drive == null || string.IsNullOrWhiteSpace(drive.DeviceToken))
                return ApiResponse.Fail("请提供虚拟机及存储查询返回的 DeviceToken。", kind: ApiErrorKind.InvalidArgument);
            string token = drive.DeviceToken;
            if (GetMediaPathError(newPath) is string pathError)
                return ApiResponse.Fail(pathError, kind: ApiErrorKind.InvalidArgument);
            using var vmObj = WmiApi.GetVmComputerSystem(vmId);
            if (vmObj == null) return ApiResponse.Fail(Properties.Resources.Error_Storage_ResNotFound, kind: ApiErrorKind.NotFound);
            using var settings = WmiApi.GetVmSettings(vmObj);
            if (settings == null) return ApiResponse.Fail(Properties.Resources.Error_Vm_GetSettings, kind: ApiErrorKind.NotFound);

            var selected = VmStorageRemoval.SelectDevice(vmId, token, await GetVmStorageItemsAsync(vmId));
            if (!selected.HasData) return VmStorageRemoval.Failure(selected);
            var current = selected.Data;
            if (current.DriveType != driveType)
                return ApiResponse.Fail("所选驱动器类型与修改入口不符。", kind: ApiErrorKind.InvalidArgument);
            if (GetEditStoragePathError(current, VmMapper.IsActiveState(Convert.ToUInt16(vmObj["EnabledState"]))) is string error)
                return ApiResponse.Fail(error, kind: ApiErrorKind.InvalidState);
            var selection = current.Selection!;
            if (selection.Media.Length > 0)
            {
                var media = selection.Media[0];
                if (!string.Equals(media.ResourceSubType, resourceSubType, StringComparison.OrdinalIgnoreCase))
                    return ApiResponse.Fail("当前介质类型不允许通过此入口修改。", kind: ApiErrorKind.InvalidState);
                if (string.IsNullOrWhiteSpace(newPath))
                    return await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "RemoveResourceSettings",
                        p => p["ResourceSettings"] = new[] { media.ObjectPath });
                return await WmiApi.WithObjectAsync(
                    $"SELECT * FROM Msvm_StorageAllocationSettingData WHERE InstanceID = '{WmiApi.Escape(media.InstanceId)}'",
                    obj => { obj["HostResource"] = new[] { newPath }; }, submitMethod: "ModifyResourceSettings",
                    submitParamName: "ResourceSettings", wrapInArray: true,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService");
            }
            if (string.IsNullOrWhiteSpace(newPath)) return ApiResponse.Ok();
            using var sasdClass = new ManagementClass(settings.Scope, new ManagementPath("Msvm_StorageAllocationSettingData"), null);
            using var sasd = sasdClass.CreateInstance();
            sasd["ResourceType"] = (ushort)31;
            sasd["ResourceSubType"] = resourceSubType;
            sasd["Parent"] = selection.Slot.ObjectPath;
            sasd["AutomaticAllocation"] = true;
            sasd["HostResource"] = new[] { newPath };
            string xml = sasd.GetText(TextFormat.CimDtd20);
            return await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "AddResourceSettings",
                p => { p["AffectedConfiguration"] = settings.Path.Path; p["ResourceSettings"] = new[] { xml }; });
        }
        // 主机物理磁盘控制

        // ISO 镜像生成

        private static async Task<(bool Success, string Message)> CreateIsoFromDirectoryAsync(
            string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            var sourceDirInfo = new DirectoryInfo(sourceDirectory);
            if (!sourceDirInfo.Exists) return (false, Properties.Resources.Error_Storage_SourceNoExist);

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

                    IsoBuilderService.BuildUdfIso(sourceDirectory, targetIsoPath, finalVolumeLabel);
                    return (true, string.Empty);
                }
                catch (Exception ex)
                {
                    return (false, string.Format(Properties.Resources.Error_Storage_IsoBuildFail, ex.Message));
                }
            });
        }

        /// <summary>按物理磁盘查询快照核对身份，并从指定虚拟机移除其直通挂接。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="disk">GetHostDisksAsync 取得的物理硬盘快照；必须提供身份和容量，执行前重新核对。</param>
        /// <returns>成功时包含原控制器类型、编号和槽位，供重新挂接使用；失败不返回默认槽位。</returns>
        public static async Task<ApiResponse<VmStorageSlot>> DetachPhysicalDiskAsync(Guid vmId, HostDiskInfo disk)
        {
            if (vmId == Guid.Empty || disk == null || disk.Number < 0)
                return ApiResponse<VmStorageSlot>.Fail("虚拟机 GUID 和物理磁盘不能为空。", kind: ApiErrorKind.InvalidArgument);
            var snapshot = SnapshotPhysicalDisk(disk)!;
            try
            {
                return await HostDiskService.WithDiskLockAsync(snapshot.Number,
                    () => DetachPhysicalDiskUnderLockAsync(vmId, snapshot));
            }
            catch (Exception error)
            {
                var failure = HostDiskService.LockFailure(error);
                return ApiResponse<VmStorageSlot>.Fail(failure.Error, failure.Code, failure.ErrorSource, error, failure.ErrorKind);
            }
        }

        internal static async Task<ApiResponse<VmStorageSlot>> DetachPhysicalDiskUnderLockAsync(Guid vmId, HostDiskInfo disk,
            Action<VmStorageSlot, string>? beforeDetach = null)
        {
            if (vmId == Guid.Empty || disk == null)
                return ApiResponse<VmStorageSlot>.Fail("虚拟机 GUID 和物理磁盘不能为空。", kind: ApiErrorKind.InvalidArgument);
            var snapshot = SnapshotPhysicalDisk(disk)!;
            var validation = ValidateAddRequest(new() { IsPhysical = true, PhysicalDisk = snapshot });
            if (!validation.Success) return ApiResponse<VmStorageSlot>.Fail(validation.Error, kind: validation.ErrorKind);
            HostResourceLock.AssertHeld(HostDiskResource.Disk(snapshot.Number));
            var state = await ReadPhysicalDiskAsync(snapshot.Number);
            if (!state.Success) return ApiResponse<VmStorageSlot>.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);
            var selection = ValidatePhysicalDiskSelection(snapshot, state.Data);
            if (!selection.Success) return ApiResponse<VmStorageSlot>.Fail(selection.Error, kind: selection.ErrorKind);
            return await DetachPhysicalDiskCoreAsync(vmId, snapshot.Number, beforeDetach);
        }

        internal static async Task<(bool Success, string CtrlType, int CtrlNum, int CtrlLoc)> DetachPhysicalDiskAsync(string vmName, int diskNumber)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return (false, "", 0, 0);
            var disks = await HostDiskService.GetHostDisksAsync();
            var disk = disks.Data?.SingleOrDefault(item => item.Number == diskNumber);
            if (disk == null) return (false, "", 0, 0);
            var response = await DetachPhysicalDiskAsync(identity.Data, disk);
            return response.HasData ? (true, response.Data.ControllerType, response.Data.ControllerNumber, response.Data.Location)
                : (false, "", 0, 0);
        }

        private static async Task<ApiResponse<VmStorageSlot>> DetachPhysicalDiskCoreAsync(Guid vmId, int diskNumber,
            Action<VmStorageSlot, string>? beforeDetach = null)
        {
            static ApiResponse<VmStorageSlot> Missing() => ApiResponse<VmStorageSlot>.Fail(
                "未能唯一定位指定虚拟机的物理磁盘挂接，请重新查询设备状态。", kind: ApiErrorKind.NotFound);
            static ApiResponse<VmStorageSlot> Failed<T>(ApiResponse<T> response) =>
                ApiResponse<VmStorageSlot>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
            using var vmObj = WmiApi.GetVmComputerSystem(vmId);
            if (vmObj == null) return Missing();

            using var settings = WmiApi.GetVmSettings(vmObj);
            if (settings == null) return Missing();

            var rasdResp = await WmiApi.QueryRelatedAsync(
                settings,
                "Msvm_ResourceAllocationSettingData",
                obj => new RasdInfo(
                    obj["InstanceID"]?.ToString() ?? "",
                    Convert.ToInt32(obj["ResourceType"] ?? 0),
                    obj["__PATH"]?.ToString() ?? obj.Path.Path),
                "Msvm_VirtualSystemSettingDataComponent",
                WmiScope.HyperV);

            if (!rasdResp.Success) return Failed(rasdResp);
            if (rasdResp.Data == null) return Missing();

            var hvDiskResp = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_DiskDrive WHERE DriveNumber = {diskNumber}",
                obj => obj["DeviceID"]?.ToString() ?? "",
                WmiScope.HyperV);

            if (!hvDiskResp.Success) return Failed(hvDiskResp);
            if (!hvDiskResp.HasData) return Missing();
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

            if (slotRasd == null) return Missing();

            var segs = slotRasd.InstanceID.Split('\\');
            if (segs.Length < 5) return Missing();

            int ctrlLoc = int.TryParse(segs[^2], out int l) ? l : 0;

            string ctrlGuid = segs[^4];
            var ctrlList = rasdResp.Data.Where(r => r.ResourceType == 5 || r.ResourceType == 6).ToList();
            var ctrl = ctrlList.FirstOrDefault(c => c.InstanceID.Contains(ctrlGuid));
            if (ctrl == null) return Missing();

            string ctrlType = ctrl.ResourceType == 6 ? "SCSI" : "IDE";
            int ctrlNum = ctrlList.Where(c => c.ResourceType == ctrl.ResourceType).ToList().IndexOf(ctrl);

            beforeDetach?.Invoke(new(ctrlType, ctrlNum, ctrlLoc), ctrl.InstanceID);

            var removeResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementService",
                "RemoveResourceSettings",
                p => p["ResourceSettings"] = new string[] { slotRasd.ObjPath },
                WmiScope.HyperV);

            return removeResult.Success ? ApiResponse<VmStorageSlot>.Ok(new(ctrlType, ctrlNum, ctrlLoc))
                : ApiResponse<VmStorageSlot>.Fail(removeResult.Error, removeResult.Code, removeResult.ErrorSource,
                    removeResult.Exception, removeResult.ErrorKind);
        }

        // 内部辅助数据模型

        private sealed record StorageResource(string InstanceId, int ResourceType, string Parent, string AddressOnParent,
            string[] HostResources, string ResourceSubType, string ObjectPath)
        {
            internal VmStorageResourceIdentity Identity() => new(InstanceId, ObjectPath, ResourceType,
                Parent, AddressOnParent, ResourceSubType, HostResources);
        }

        private sealed record RasdInfo(string InstanceID, int ResourceType, string ObjPath);

        private class HostDiskInfoCache
        {
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public double SizeGiB { get; set; }
            public HostDiskSnapshot? Identity { get; set; }
        }
    }
}
