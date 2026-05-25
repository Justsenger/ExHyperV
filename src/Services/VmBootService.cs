using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Api;
using ExHyperV.Models;

namespace ExHyperV.Services;

public class VmBootService
{
    public async Task<List<BootOrderItem>> GetBootOrderAsync(string vmName)
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
                            var item = BootOrderItem.CreateGen1(code);
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
                                    IsGen2 = true,
                                    Reference = path
                                };
                                ParseGen2BootInfo(item, bs.FwPath, allHardware, hardwareMap, childrenMap);
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

    public async Task<bool> SetBootOrderAsync(string vmName, List<BootOrderItem> items)
    {
        return await Task.Run(async () =>
        {
            try
            {
                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null) return false;

                using var settings = WmiApi.GetVmSettings(vm);
                if (settings == null) return false;

                bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";

                if (isGen2)
                    settings["BootSourceOrder"] = items.Select(i => i.Reference.ToString()).ToArray();
                else
                    settings["BootOrder"] = items.Select(i => Convert.ToUInt16(i.Reference)).ToArray();

                string xml = settings.GetText(TextFormat.CimDtd20);

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifySystemSettings",
                    p => p["SystemSettings"] = xml);

                return result.Success;
            }
            catch { return false; }
        });
    }

    // ── 业务逻辑（不改动）────────────────────────────────────────

    private string GetGen1HardwareSummary(int code, List<ManagementObject> all,
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

    private void ParseGen2BootInfo(BootOrderItem item, string fwPath,
        List<ManagementObject> all,
        Dictionary<string, ManagementObject> map,
        Dictionary<string, List<ManagementObject>> children)
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

                item.Description = GetMediaFile(drive, children) ?? string.Empty;
                item.Icon = item.Description.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                    ? "\uE958" : "\uEDA2";
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

    private string? GetMediaFile(ManagementObject? drive,
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

    private List<ManagementObject> GetVmHardware(ManagementObject settings)
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

    private Dictionary<string, List<ManagementObject>> BuildChildrenMap(
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

    private string? GetParentId(ManagementObject res)
    {
        string? p = res["Parent"]?.ToString();
        if (string.IsNullOrEmpty(p)) return null;
        var m = Regex.Match(p, @"InstanceID=[""']([^""']+)[""']");
        return m.Success ? NormalizeId(m.Groups[1].Value) : null;
    }

    private string? NormalizeId(string? id) => id?.Replace("\\\\", "\\");
}