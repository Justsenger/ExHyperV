using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>读取和配置 BIOS 或 UEFI 启动设备顺序与 Num Lock。</summary>
public static partial class VmBootService
{

    internal static async Task<List<BootOrderItem>> GetBootOrderAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) throw new InvalidOperationException(identity.Error);
        var response = await GetBootOrderAsync(identity.Data);
        if (!response.HasData) throw new InvalidOperationException(response.Error);
        return response.Data;
    }


    internal static async Task<(bool Success, string Message)> SetBootOrderAsync(string vmName, List<BootOrderItem> items)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var response = await SetBootOrderAsync(identity.Data, items);
        return (response.Success, response.Error);
    }


    internal static async Task<(bool Success, string Message)> SetIsoFirstAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var response = await SetIsoFirstAsync(identity.Data);
        return (response.Success, response.Error);
    }


    internal static async Task<bool> GetBootNumLockAsync(string vmName)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) throw new InvalidOperationException(identity.Error);
        var response = await GetBootNumLockAsync(identity.Data);
        if (!response.HasData) throw new InvalidOperationException(response.Error);
        return response.Data;
    }


    internal static async Task<(bool Success, string Message)> SetBootNumLockAsync(string vmName, bool enable)
    {
        var identity = await VmIdentity.ResolveAsync(vmName);
        if (!identity.HasData) return (false, identity.Error);
        var response = await SetBootNumLockAsync(identity.Data, enable);
        return (response.Success, response.Error);
    }

    /// <summary>按虚拟机代数与运行状态判断是否允许改变启动顺序。</summary>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
    /// <returns>第一代运行时不允许修改；其他受支持状态允许。</returns>
    internal static bool CanChangeBootOrder(int generation, bool isRunning) => !(generation == 1 && isRunning);

    /// <summary>评估启动顺序的修改条件。</summary>
    /// <param name="generation">查询得到的虚拟机代数。</param>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>第一代要求关机；第二代提交固件启动顺序，由 VMMS 检查实际请求。</returns>
    public static VmModificationCondition GetBootOrderModificationCondition(int generation, VmState state) => generation switch
    {
        1 => VmModificationCondition.PoweredOff(state),
        2 => VmModificationCondition.Allowed,
        _ => VmModificationCondition.Unsupported("未识别虚拟机代数。")
    };

    /// <summary>读取第一代 BIOS 或第二代固件的启动设备顺序。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功时返回按优先级排列的设备，列表可以为空；找不到虚拟机/配置与查询失败分别返回失败原因，不返回部分读取的顺序。</returns>
    public static async Task<ApiResponse<List<BootOrderItem>>> GetBootOrderAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<List<BootOrderItem>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        return await Task.Run(() =>
        {
            var result = new List<BootOrderItem>();
            List<ManagementObject> allHardware = [];
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null) return ApiResponse<List<BootOrderItem>>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return ApiResponse<List<BootOrderItem>>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";
                allHardware = GetVmHardware(settings);
                var hardwareMap = allHardware.ToDictionary(
                    h => NormalizeId(h["InstanceID"]?.ToString()), h => h);
                var childrenMap = BuildChildrenMap(allHardware);

                if (!isGen2)
                {
                    if (settings["BootOrder"] is ushort[] bootOrder)
                    {
                        foreach (var code in bootOrder)
                        {
                            var item = CreateGen1(code);
                            item.OwnerVmId = vmId;
                            item.Description = GetGen1HardwareSummary(
                                (int)code, allHardware, hardwareMap, childrenMap);
                            result.Add(item);
                        }
                    }
                }
                else
                {
                    var bootPaths = (string[])settings["BootSourceOrder"];
                    if (bootPaths != null)
                    {
                        var osDiskModels = BuildOsDiskModels();
                        foreach (var path in bootPaths)
                        {
                            try
                            {
                                // F 类型：路径实例化，走 WmiApi.GetByPathAsync
                                var bootSourceResponse = WmiApi.GetByPathAsync(path, obj => new
                                {
                                    Desc = obj["BootSourceDescription"]?.ToString(),
                                    Element = obj["ElementName"]?.ToString(),
                                    FwPath = obj["FirmwareDevicePath"]?.ToString(),
                                }).GetAwaiter().GetResult();

                                if (!bootSourceResponse.Success)
                                    return ApiResponse<List<BootOrderItem>>.Fail(bootSourceResponse.Error, bootSourceResponse.Code,
                                        bootSourceResponse.ErrorSource, bootSourceResponse.Exception, bootSourceResponse.ErrorKind);
                                if (!bootSourceResponse.HasData)
                                    return ApiResponse<List<BootOrderItem>>.Fail("启动源已变化，请重新读取启动顺序。", kind: ApiErrorKind.Conflict);
                                var bs = bootSourceResponse.Data!;

                                var item = new BootOrderItem
                                {
                                    Name = string.IsNullOrWhiteSpace(bs.Desc) ? bs.Element : bs.Desc,
                                    Reference = path,
                                    OwnerVmId = vmId,
                                    FwPath = bs.FwPath
                                };
                                ParseGen2BootInfo(item, bs.FwPath, allHardware, hardwareMap, childrenMap, osDiskModels);
                                result.Add(item);
                            }
                            catch (Exception ex)
                            {
                                return ApiResponse<List<BootOrderItem>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return ApiResponse<List<BootOrderItem>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
            }
            finally { foreach (var hardware in allHardware) hardware.Dispose(); }
            return ApiResponse<List<BootOrderItem>>.Ok(result);
        });
    }

    /// <summary>写入启动顺序；第二代按稳定固件设备路径匹配当前设备，并保留未列出的设备。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="items">同一虚拟机读取结果中的设备项，按期望优先级排列；新建、重复、跨虚拟机或已失效的设备项会被拒绝。</param>
    /// <returns>写入结果；身份、运行状态或 WMI 拒绝分别返回失败，不改变输入设备列表。</returns>
    public static async Task<ApiResponse> SetBootOrderAsync(Guid vmId, List<BootOrderItem> items)
    {
        var requestedItems = items?.ToArray();
        var validation = ValidateBootOrderItems(vmId, requestedItems);
        if (!validation.Success) return validation;
        return await Task.Run(async () =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null) return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";
                int generation = isGen2 ? 2 : settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:1" ? 1 : 0;
                var condition = GetBootOrderModificationCondition(generation, VmMapper.MapState(Convert.ToUInt16(vm["EnabledState"])));
                if (!condition.CanModify) return condition.ToResponse();

                if (isGen2)
                {
                    // 陈旧引用防护：读取时捕获的 boot-source 路径(含 InstanceID)在运行态/装完系统后会随实例重建而失效，
                    // 原样回填 → 0x80070490(元素未找到)。改为重读当前 BootSourceOrder，用 FirmwareDevicePath(稳定键)
                    // 把用户顺序映射到"当前有效"的路径；结果保证是当前路径的一个排列——不漏源、不会 element-not-found。
                    if (settings["BootSourceOrder"] is not string[] currentPaths || currentPaths.Length == 0)
                        return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                    var currentFw = new string?[currentPaths.Length];
                    for (int i = 0; i < currentPaths.Length; i++)
                    {
                        try
                        {
                            var r = await WmiApi.GetByPathAsync(currentPaths[i], o => new { FirmwarePath = o["FirmwareDevicePath"]?.ToString() });
                            if (!r.Success) return ApiResponse.Fail(r.Error, r.Code, r.ErrorSource, r.Exception, r.ErrorKind);
                            if (!r.HasData) return ApiResponse.Fail("启动源已变化，请重新读取启动顺序。", kind: ApiErrorKind.Conflict);
                            currentFw[i] = r.Data.FirmwarePath;
                        }
                        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
                    }

                    var ordered = OrderCurrentBootSources(requestedItems!, currentPaths, currentFw);
                    if (!ordered.HasData)
                        return ApiResponse.Fail(ordered.Error, ordered.Code, ordered.ErrorSource, ordered.Exception, ordered.ErrorKind);
                    settings["BootSourceOrder"] = ordered.Data;
                }
                else
                {
                    if (settings["BootOrder"] is not ushort[] currentOrder)
                        return ApiResponse.Fail("当前配置未提供启动设备顺序。", kind: ApiErrorKind.Unsupported);
                    if (requestedItems!.Any(item => item.Reference is not int code || code < 0 || code > ushort.MaxValue || !currentOrder.Contains((ushort)code)))
                        return ApiResponse.Fail("启动设备已变化，请重新读取启动顺序。", kind: ApiErrorKind.Conflict);
                    settings["BootOrder"] = requestedItems.Select(item => (ushort)(int)item.Reference).ToArray();
                }

                string xml = settings.GetText(TextFormat.CimDtd20);

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifySystemSettings",
                    p => p["SystemSettings"] = xml);

                return result;
            }
            catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
        });
    }

    internal static ApiResponse ValidateBootOrderItems(Guid vmId, IEnumerable<BootOrderItem>? items)
    {
        if (vmId == Guid.Empty || items == null)
            return ApiResponse.Fail("虚拟机 GUID 和启动顺序不能为空。", kind: ApiErrorKind.InvalidArgument);
        var snapshot = items.ToArray();
        if (snapshot.Any(item => item == null || item.OwnerVmId != vmId || item.Reference == null))
            return ApiResponse.Fail("启动项不属于目标虚拟机，请重新读取该虚拟机的启动顺序。", kind: ApiErrorKind.Conflict);
        if (snapshot.Select(item => item.Reference).Distinct().Count() != snapshot.Length)
            return ApiResponse.Fail("启动顺序包含重复设备。", kind: ApiErrorKind.InvalidArgument);
        return ApiResponse.Ok();
    }

    internal static ApiResponse<string[]> OrderCurrentBootSources(IReadOnlyList<BootOrderItem> items,
        IReadOnlyList<string> currentPaths, IReadOnlyList<string?> currentFirmwarePaths)
    {
        var used = new bool[currentPaths.Count];
        var ordered = new List<string>(currentPaths.Count);
        foreach (var item in items)
        {
            int matched = -1;
            for (int i = 0; i < currentPaths.Count; i++)
            {
                bool sameSource = !string.IsNullOrEmpty(item.FwPath)
                    ? string.Equals(currentFirmwarePaths[i], item.FwPath, StringComparison.Ordinal)
                    : string.Equals(currentPaths[i], item.Reference as string, StringComparison.OrdinalIgnoreCase);
                if (!used[i] && sameSource) { matched = i; break; }
            }
            if (matched < 0)
                return ApiResponse<string[]>.Fail("启动设备已变化，请重新读取启动顺序。", kind: ApiErrorKind.Conflict);
            ordered.Add(currentPaths[matched]);
            used[matched] = true;
        }
        for (int i = 0; i < currentPaths.Count; i++)
            if (!used[i]) ordered.Add(currentPaths[i]);
        return ApiResponse<string[]>.Ok(ordered.ToArray());
    }

    // 将安装介质(ISO)所在的光盘引导项移到引导顺序首位。复用 GetBootOrderAsync/SetBootOrderAsync，
    // Gen1/Gen2 通用：两代下光盘引导项的 Description 均为挂载的介质文件名(以 .iso 结尾)，据此定位。
    // 创建带 ISO 的虚拟机后调用，避免默认网络(PXE)优先导致首次开机空等/落到空盘。
    // 尽力而为：无 ISO 项或它已在首位时直接成功返回(无需改动)；读不到引导项时返回失败原因，但内部不抛异常。
    /// <summary>将已挂载 ISO 的光驱调整到启动顺序首位。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>已处于首位或没有可提升的 ISO 光驱时也视为成功；否则返回调整结果。</returns>
    /// <editentry label="优先从光驱引导" category="引导"/>
    public static async Task<ApiResponse> SetIsoFirstAsync(Guid vmId)
    {
        var response = await GetBootOrderAsync(vmId);
        if (!response.HasData)
            return ApiResponse.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
        var order = response.Data;
        if (order.Count == 0) return ApiResponse.Ok();

        int idx = order.FindIndex(i =>
            i.Description?.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) == true);

        if (idx <= 0) return ApiResponse.Ok(); // 没有 ISO 引导项，或它已在首位：无需改动

        var iso = order[idx];
        order.RemoveAt(idx);
        order.Insert(0, iso);

        return await SetBootOrderAsync(vmId, order);
    }

    // ── 启动时 NumLock（BIOSNumLock 固件设置）──────────────────────────
    // 第 2 代虚拟机此项默认 false，开机即把 NumLock 置关，控制台（RDP）连上同步键盘 LED 时会把主机 NumLock 一并带掉。
    // 微软对 Gen2 不提供 Set-VMBios，但底层 Msvm_VirtualSystemSettingData.BIOSNumLock 不分代际、可直接经 WMI 设。
    // 只能在关机态修改（运行态 ModifySystemSettings 返回 32768），改完下次开机生效。
    /// <summary>读取当前固件配置的引导 Num Lock 设置，不按虚拟机代数过滤。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>成功时返回实际 Num Lock 开关；虚拟机/配置不存在或查询失败返回失败。</returns>
    public static async Task<ApiResponse<bool>> GetBootNumLockAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<bool>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        return await Task.Run(() =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null) return ApiResponse<bool>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);
                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return ApiResponse<bool>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);
                return settings["BIOSNumLock"] is bool value ? ApiResponse<bool>.Ok(value)
                    : ApiResponse<bool>.Fail("当前配置未提供引导 Num Lock 设置。", kind: ApiErrorKind.Unsupported);
            }
            catch (Exception ex) { return ApiResponse<bool>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
        });
    }

    /// <summary>在虚拟机未运行时写入引导 Num Lock 设置。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="enable">true 请求启用该设置，false 请求关闭。</param>
    /// <returns>写入成功或包含校验/执行原因的失败响应。</returns>
    public static async Task<ApiResponse> SetBootNumLockAsync(Guid vmId, bool enable)
    {
        if (vmId == Guid.Empty)
            return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        return await Task.Run(async () =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null) return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                var condition = GetNumLockModificationCondition(VmMapper.MapState(Convert.ToUInt16(vm["EnabledState"])));
                if (!condition.CanModify) return condition.ToResponse();

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

                settings["BIOSNumLock"] = enable;

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifySystemSettings",
                    p => p["SystemSettings"] = settings.GetText(TextFormat.CimDtd20));

                return result;
            }
            catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
        });
    }

    // ── 业务逻辑 ────────────────────────────────────────────────

    /// <summary>按 Gen1 设备码建引导项；名称跟随本次调用语言，Description 由调用方补。</summary>
    internal static BootOrderItem CreateGen1(ushort code)
    {
        var (name, kind) = code switch
        {
            0 => (Properties.Resources.BootOrderItem_FloppyDisk, BootDeviceKind.Floppy),
            1 => (Properties.Resources.BootOrderItem_OpticalDrive, BootDeviceKind.OpticalDrive),
            2 => (Properties.Resources.BootOrderItem_IdeHardDisk, BootDeviceKind.HardDisk),
            3 => (Properties.Resources.BootOrderItem_PxeNetworkBoot, BootDeviceKind.Network),
            4 => (Properties.Resources.BootOrderItem_ScsiHardDisk, BootDeviceKind.HardDisk),
            _ => (Properties.Resources.BootOrderItem_UnknownDevice, BootDeviceKind.Unknown)
        };
        return new BootOrderItem { Name = name, Kind = kind, Reference = (int)code };
    }

    private static string GetGen1HardwareSummary(int code, List<ManagementObject> all,
        Dictionary<string, ManagementObject> map,
        Dictionary<string, List<ManagementObject>> children)
    {
        if (code == 3) return Properties.Resources.BootOrderItem_PxeNetworkBoot;

        var matchedDevices = all.Where(dev =>
        {
            ushort resType = Convert.ToUInt16(dev["ResourceType"]);
            string pId = GetParentId(dev);
            var ctrl = (pId != null && map.ContainsKey(pId)) ? map[pId] : null;
            ushort ctrlResType = ctrl != null ? Convert.ToUInt16(ctrl["ResourceType"]) : (ushort)0;

            if (code == 0 && resType == 14) return true;
            if (code == 1 && resType == 16) return true;
            if (code == 2 && (resType == 17 || resType == 22) && ctrlResType == 5) return true;
            if (code == 4 && (resType == 17 || resType == 22) && ctrlResType == 6) return true;
            return false;
        }).ToList();

        if (!matchedDevices.Any()) return string.Empty;

        var sorted = matchedDevices
            .OrderBy(d =>
            {
                string pId = GetParentId(d);
                return (pId != null && map.ContainsKey(pId))
                    ? Convert.ToInt32(map[pId]["Address"] ?? 0) : 0;
            })
            .ThenBy(d => Convert.ToInt32(d["AddressOnParent"] ?? 0))
            .ToList();

        return GetMediaFile(sorted.First(), children) ?? string.Empty;
    }

    private static void ParseGen2BootInfo(BootOrderItem item, string fwPath,
        List<ManagementObject> all,
        Dictionary<string, ManagementObject> map,
        Dictionary<string, List<ManagementObject>> children,
        Dictionary<int, string> osDiskModels)
    {
        if (string.IsNullOrEmpty(fwPath)) { item.Kind = BootDeviceKind.Unknown; return; }

        if (fwPath.Contains("Scsi("))
        {
            var m = Regex.Match(fwPath, @"Scsi\((\d+),(\d+)\)");
            if (m.Success)
            {
                int cIdx = int.Parse(m.Groups[1].Value);
                int sIdx = int.Parse(m.Groups[2].Value);

                var drive = all.FirstOrDefault(d =>
                {
                    int resType = Convert.ToInt32(d["ResourceType"] ?? 0);
                    if (resType != 16 && resType != 17 && resType != 22) return false;
                    if (Convert.ToInt32(d["AddressOnParent"] ?? -1) != sIdx) return false;
                    string pId = GetParentId(d);
                    if (pId != null && map.ContainsKey(pId))
                        return Convert.ToInt32(map[pId]["Address"] ?? 0) == cIdx;
                    return false;
                });

                string? file = GetMediaFile(drive, children);
                if (!string.IsNullOrEmpty(file))
                {
                    item.Description = file;
                    item.Kind = file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? BootDeviceKind.OpticalDrive : BootDeviceKind.HardDisk;
                }
                else
                {
                    // 无介质文件：物理直通盘取磁盘型号(与存储视图一致，如 WDC WDS500G2B0C-00PXH0)；
                    // 取不到型号再按驱动器类型兜底：空光驱=光驱、无盘=SCSI 硬盘、槽位无设备=未知设备。
                    string? physModel = drive != null ? ResolvePhysicalDiskModel(drive, osDiskModels) : null;
                    if (physModel != null)
                    {
                        item.Kind = BootDeviceKind.HardDisk;
                        item.Description = physModel;
                    }
                    else
                    {
                        int rt = drive != null ? Convert.ToInt32(drive["ResourceType"] ?? 0) : -1;
                        item.Kind = rt == 16 ? BootDeviceKind.OpticalDrive : BootDeviceKind.HardDisk;
                        item.Description = rt == 16 ? Properties.Resources.BootOrderItem_OpticalDrive
                            : rt is 17 or 22 ? Properties.Resources.BootOrderItem_ScsiHardDisk
                            : Properties.Resources.BootOrderItem_UnknownDevice;
                    }
                }
            }
        }
        else if (fwPath.Contains("MAC("))
        {
            item.Kind = BootDeviceKind.Network;
            item.Description = Properties.Resources.BootOrderItem_PxeNetworkBoot;
        }
        else if (fwPath.Contains(".efi"))
        {
            item.Kind = BootDeviceKind.FirmwareFile;
            item.Description = Properties.Resources.VmBootService_WindowsBootManager;
        }
        else
        {
            item.Kind = BootDeviceKind.Firmware;
            item.Description = Properties.Resources.VmBootService_SystemBuiltinBootEntry;
        }
    }

    // 物理直通盘：从驱动器 HostResource(Msvm_DiskDrive 引用)解析磁盘号 → Win32_DiskDrive 型号(如 WDC WDS500G2B0C-00PXH0)。
    // 非物理盘或取不到型号返回 null，交调用方走通用兜底。
    private static string? ResolvePhysicalDiskModel(ManagementObject drive, Dictionary<int, string> osDiskModels)
    {
        var hostRes = drive["HostResource"] as string[];
        string raw = hostRes?.Length > 0 ? hostRes[0] : "";
        if (string.IsNullOrEmpty(raw)) return null;
        bool isPhys = raw.IndexOf("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) >= 0
                   || raw.ToUpper().Contains("PHYSICALDRIVE");
        if (!isPhys) return null;

        int diskNum = -1;
        var dm = Regex.Match(raw, "DeviceID=\"([^\"]+)\"");
        if (dm.Success)
        {
            string devId = dm.Groups[1].Value.Replace("\\\\", "\\");
            var tail = Regex.Match(devId, @"\\(\d+)$");
            if (tail.Success) diskNum = int.Parse(tail.Groups[1].Value);
        }
        if (diskNum < 0)
        {
            var pm = Regex.Match(raw, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
            if (pm.Success) diskNum = int.Parse(pm.Groups[1].Value);
        }
        return diskNum >= 0 && osDiskModels.TryGetValue(diskNum, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model.Trim() : null;
    }

    // 磁盘号 → 型号(Win32_DiskDrive)，供引导项里物理直通盘显示型号名。
    private static Dictionary<int, string> BuildOsDiskModels()
    {
        var map = new Dictionary<int, string>();
        try
        {
            var resp = WmiApi.QueryAsync(
                "SELECT Index, Model FROM Win32_DiskDrive",
                obj => (idx: WmiApi.Prop<int>(obj, "Index", -1), model: WmiApi.PropStr(obj, "Model") ?? ""),
                WmiScope.CimV2).GetAwaiter().GetResult();
            if (resp.Success && resp.Data != null)
                foreach (var (idx, model) in resp.Data)
                    if (idx >= 0) map[idx] = model;
        }
        catch { }
        return map;
    }

    private static string? GetMediaFile(ManagementObject? drive,
        Dictionary<string, List<ManagementObject>> children)
    {
        if (drive == null) return null;
        string dId = NormalizeId(drive["InstanceID"]?.ToString());
        if (!children.ContainsKey(dId)) return null;

        foreach (var media in children[dId])
        {
            ushort type = Convert.ToUInt16(media["ResourceType"]);
            if (type != 31 && type != 16 && type != 22) continue;

            if (media["HostResource"] is System.Collections.IEnumerable enumerable)
            {
                foreach (var pathObj in enumerable)
                {
                    string? p = pathObj?.ToString();
                    if (!string.IsNullOrEmpty(p))
                        return System.IO.Path.GetFileName(
                            p.Replace("file://", "").Replace("/", "\\"));
                }
            }
        }
        return null;
    }

    private static List<ManagementObject> GetVmHardware(ManagementObject settings)
    {
        var list = settings.GetRelated(
            "Msvm_ResourceAllocationSettingData",
            "Msvm_VirtualSystemSettingDataComponent",
            null, null, null, null, false, null)
            .Cast<ManagementObject>().ToList();

        list.AddRange(settings.GetRelated(
            "Msvm_StorageAllocationSettingData",
            "Msvm_VirtualSystemSettingDataComponent",
            null, null, null, null, false, null)
            .Cast<ManagementObject>().ToList());

        return list;
    }

    private static Dictionary<string, List<ManagementObject>> BuildChildrenMap(
        List<ManagementObject> hardware)
    {
        var map = new Dictionary<string, List<ManagementObject>>();
        foreach (var res in hardware)
        {
            string? pId = GetParentId(res);
            if (pId == null) continue;
            if (!map.ContainsKey(pId)) map[pId] = new List<ManagementObject>();
            map[pId].Add(res);
        }
        return map;
    }

    private static string? GetParentId(ManagementObject res)
    {
        string? p = res["Parent"]?.ToString();
        if (string.IsNullOrEmpty(p)) return null;
        var m = Regex.Match(p, @"InstanceID=[""']([^""']+)[""']");
        return m.Success ? NormalizeId(m.Groups[1].Value) : null;
    }

    private static string? NormalizeId(string? id) => id?.Replace("\\\\", "\\");
}
