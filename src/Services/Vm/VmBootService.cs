using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services;

public static class VmBootService
{
    public static async Task<List<BootOrderItem>> GetBootOrderAsync(string vmName)
    {
        return await Task.Run(() =>
        {
            var result = new List<BootOrderItem>();
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null) return result;

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return result;

                bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";
                var allHardware = GetVmHardware(settings);
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

                                if (!bootSourceResponse.HasData) continue;
                                var bs = bootSourceResponse.Data!;

                                var item = new BootOrderItem
                                {
                                    Name = string.IsNullOrWhiteSpace(bs.Desc) ? bs.Element : bs.Desc,
                                    Reference = path,
                                    FwPath = bs.FwPath
                                };
                                ParseGen2BootInfo(item, bs.FwPath, allHardware, hardwareMap, childrenMap, osDiskModels);
                                result.Add(item);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return result;
        });
    }

    // 返回 (是否成功, 失败原因)：原因透出给前端显示，失败时前端据此回滚 UI 顺序到后端真值。
    public static async Task<(bool Success, string Message)> SetBootOrderAsync(string vmName, List<BootOrderItem> items)
    {
        return await Task.Run(async () =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null) return (false, Properties.Resources.Error_Net_VmNotFound);

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return (false, Properties.Resources.Error_Net_VmNotFound);

                bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";

                if (isGen2)
                {
                    // 陈旧引用防护：读取时捕获的 boot-source 路径(含 InstanceID)在运行态/装完系统后会随实例重建而失效，
                    // 原样回填 → 0x80070490(元素未找到)。改为重读当前 BootSourceOrder，用 FirmwareDevicePath(稳定键)
                    // 把用户顺序映射到"当前有效"的路径；结果保证是当前路径的一个排列——不漏源、不会 element-not-found。
                    if (settings["BootSourceOrder"] is not string[] currentPaths || currentPaths.Length == 0)
                        return (false, Properties.Resources.Error_Net_VmNotFound);

                    var currentFw = new string?[currentPaths.Length];
                    for (int i = 0; i < currentPaths.Length; i++)
                    {
                        try
                        {
                            var r = await WmiApi.GetByPathAsync(currentPaths[i], o => o["FirmwareDevicePath"]?.ToString());
                            currentFw[i] = r.HasData ? r.Data : null;
                        }
                        catch { }
                    }

                    var used = new bool[currentPaths.Length];
                    var ordered = new List<string>(currentPaths.Length);
                    foreach (var it in items)
                    {
                        if (string.IsNullOrEmpty(it.FwPath)) continue;
                        for (int i = 0; i < currentPaths.Length; i++)
                        {
                            if (!used[i] && currentFw[i] == it.FwPath)
                            {
                                ordered.Add(currentPaths[i]);
                                used[i] = true;
                                break;
                            }
                        }
                    }
                    // 用户项没匹配到的当前源(新出现的/键变了的)补到末尾，绝不漏写
                    for (int i = 0; i < currentPaths.Length; i++)
                        if (!used[i]) ordered.Add(currentPaths[i]);

                    settings["BootSourceOrder"] = ordered.ToArray();
                }
                else
                    settings["BootOrder"] = items.Select(i => Convert.ToUInt16(i.Reference)).ToArray();

                string xml = settings.GetText(TextFormat.CimDtd20);

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifySystemSettings",
                    p => p["SystemSettings"] = xml);

                return result.Success ? (true, string.Empty) : (false, result.Error);
            }
            catch (Exception ex) { return (false, ex.Message); }
        });
    }

    // 将安装介质(ISO)所在的光盘引导项移到引导顺序首位。复用 GetBootOrderAsync/SetBootOrderAsync，
    // Gen1/Gen2 通用：两代下光盘引导项的 Description 均为挂载的介质文件名(以 .iso 结尾)，据此定位。
    // 创建带 ISO 的虚拟机后调用，避免默认网络(PXE)优先导致首次开机空等/落到空盘。
    // 尽力而为：无 ISO 项或它已在首位时直接成功返回(无需改动)；读不到引导项时返回失败原因，但内部不抛异常。
    public static async Task<(bool Success, string Message)> SetIsoFirstAsync(string vmName)
    {
        var order = await GetBootOrderAsync(vmName);
        if (order.Count == 0) return (false, Properties.Resources.Error_Net_VmNotFound);

        int idx = order.FindIndex(i =>
            i.Description?.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) == true);

        if (idx <= 0) return (true, string.Empty); // 没有 ISO 引导项，或它已在首位：无需改动

        var iso = order[idx];
        order.RemoveAt(idx);
        order.Insert(0, iso);

        return await SetBootOrderAsync(vmName, order);
    }

    // ── 启动时 NumLock（BIOSNumLock 固件设置）──────────────────────────
    // 第 2 代虚拟机此项默认 false，开机即把 NumLock 置关，控制台（RDP）连上同步键盘 LED 时会把宿主 NumLock 一并带掉。
    // 微软对 Gen2 不提供 Set-VMBios，但底层 Msvm_VirtualSystemSettingData.BIOSNumLock 不分代际、可直接经 WMI 设。
    // 实测：只能在关机态修改（运行态 ModifySystemSettings 返回 32768），改完下次开机生效。
    public static async Task<bool> GetBootNumLockAsync(string vmName)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null) return false;
                using var settings = WmiApi.GetVmSettings(vm);
                return settings?["BIOSNumLock"] is bool b && b;
            }
            catch { return false; }
        });
    }

    public static async Task<(bool Success, string Message)> SetBootNumLockAsync(string vmName, bool enable)
    {
        return await Task.Run(async () =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null) return (false, Properties.Resources.Error_Net_VmNotFound);

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return (false, Properties.Resources.Error_Net_VmNotFound);

                settings["BIOSNumLock"] = enable;

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifySystemSettings",
                    p => p["SystemSettings"] = settings.GetText(TextFormat.CimDtd20));

                return result.Success ? (true, string.Empty) : (false, result.Error);
            }
            catch (Exception ex) { return (false, ex.Message); }
        });
    }

    // ── 业务逻辑 ────────────────────────────────────────────────

    private static readonly Dictionary<int, (string Name, string Icon)> Gen1DeviceMapping = new()
    {
        { 0, (Properties.Resources.BootOrderItem_FloppyDisk, "\uE74E") },
        { 1, (Properties.Resources.BootOrderItem_OpticalDrive, "\uE958") },
        { 2, (Properties.Resources.BootOrderItem_IdeHardDisk, "\uEDA2") },
        { 3, (Properties.Resources.BootOrderItem_PxeNetworkBoot, "\uE774") },
        { 4, (Properties.Resources.BootOrderItem_ScsiHardDisk, "\uEDA2") }
    };

    /// <summary>按 Gen1 设备码建引导项（名称+图标查映射表；Description 由调用方补）。</summary>
    private static BootOrderItem CreateGen1(ushort code)
    {
        var exists = Gen1DeviceMapping.TryGetValue(code, out var info);
        var (name, icon) = exists ? info : (Properties.Resources.BootOrderItem_UnknownDevice, "\uE9CE");
        return new BootOrderItem { Name = name, Icon = icon, Reference = (int)code };
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
        if (string.IsNullOrEmpty(fwPath)) { item.Icon = "\uE9CE"; return; }

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
                    item.Icon = file.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? "\uE958" : "\uEDA2";
                }
                else
                {
                    // 无介质文件：物理直通盘取磁盘型号(与存储视图一致，如 WDC WDS500G2B0C-00PXH0)；
                    // 取不到型号再按驱动器类型兜底：空光驱=光驱、无盘=SCSI 硬盘、槽位无设备=未知设备。
                    string? physModel = drive != null ? ResolvePhysicalDiskModel(drive, osDiskModels) : null;
                    if (physModel != null)
                    {
                        item.Icon = "\uEDA2";
                        item.Description = physModel;
                    }
                    else
                    {
                        int rt = drive != null ? Convert.ToInt32(drive["ResourceType"] ?? 0) : -1;
                        item.Icon = rt == 16 ? "\uE958" : "\uEDA2";
                        item.Description = rt == 16 ? Properties.Resources.BootOrderItem_OpticalDrive
                            : rt is 17 or 22 ? Properties.Resources.BootOrderItem_ScsiHardDisk
                            : Properties.Resources.BootOrderItem_UnknownDevice;
                    }
                }
            }
        }
        else if (fwPath.Contains("MAC("))
        {
            item.Icon = "\uE774";
            item.Description = Properties.Resources.BootOrderItem_PxeNetworkBoot;
        }
        else if (fwPath.Contains(".efi"))
        {
            item.Icon = "\uE74C";
            item.Description = Properties.Resources.VmBootService_WindowsBootManager;
        }
        else
        {
            item.Icon = "\uE713";
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