using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public class VmBootService
    {
        private const string Namespace = @"root\virtualization\v2";

        public async Task<List<BootOrderItem>> GetBootOrderAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                var result = new List<BootOrderItem>();
                try
                {
                    using var vm = GetVmObject(vmName);
                    if (vm == null) return result;

                    using var settings = GetVmSettings(vm);
                    bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";
                    var allHardware = GetVmHardware(settings);
                    var hardwareMap = allHardware.ToDictionary(h => NormalizeId(h["InstanceID"]?.ToString()), h => h);
                    var childrenMap = BuildChildrenMap(allHardware);

                    if (!isGen2)
                    {
                        var rawOrder = settings["BootOrder"];
                        if (rawOrder is ushort[] bootOrder)
                        {
                            var bootDict = new Dictionary<int, (string, string)> {
                                { 0, ("软盘", "\uE74E") },
                                { 1, ("光驱", "\uE958") },
                                { 2, ("IDE 硬盘", "\uEDA2") },
                                { 3, ("PXE 网络引导", "\uE774") },
                                { 4, ("SCSI 硬盘", "\uEDA2") }
                            };

                            foreach (var code in bootOrder)
                            {
                                var info = bootDict.ContainsKey((int)code) ? bootDict[(int)code] : ("未知设备", "\uE9CE");

                                var item = new BootOrderItem
                                {
                                    Name = info.Item1,
                                    Icon = info.Item2,
                                    IsGen2 = false,
                                    Reference = (int)code
                                };
                                item.Description = GetGen1HardwareSummary((int)code, allHardware, hardwareMap, childrenMap);
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
                                    using var bootSource = new ManagementObject(path);
                                    string desc = bootSource["BootSourceDescription"]?.ToString();
                                    string element = bootSource["ElementName"]?.ToString();
                                    string fwPath = bootSource["FirmwareDevicePath"]?.ToString();

                                    var item = new BootOrderItem
                                    {
                                        Name = string.IsNullOrWhiteSpace(desc) ? element : desc,
                                        IsGen2 = true,
                                        Reference = path
                                    };
                                    ParseGen2BootInfo(item, fwPath, allHardware, hardwareMap, childrenMap);
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
            return await Task.Run(() =>
            {
                try
                {
                    using var vm = GetVmObject(vmName);
                    if (vm == null) return false;
                    using var settings = GetVmSettings(vm);

                    bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";

                    if (isGen2)
                    {
                        settings["BootSourceOrder"] = items.Select(i => i.Reference.ToString()).ToArray();
                    }
                    else
                    {
                        settings["BootOrder"] = items.Select(i => Convert.ToUInt16(i.Reference)).ToArray();
                    }

                    using var vmSvc = GetManagementService();
                    using var inParams = vmSvc.GetMethodParameters("ModifySystemSettings");
                    inParams["SystemSettings"] = settings.GetText(TextFormat.CimDtd20);

                    using var outParams = vmSvc.InvokeMethod("ModifySystemSettings", inParams, null);
                    uint returnValue = (uint)outParams["ReturnValue"];

                    return returnValue == 0 || returnValue == 4096;
                }
                catch
                {
                    return false;
                }
            });
        }

        #region 核心工具方法

        private string GetGen1HardwareSummary(int code, List<ManagementObject> all, Dictionary<string, ManagementObject> map, Dictionary<string, List<ManagementObject>> children)
        {
            if (code == 3) return "PXE 网络引导";

            var matchedDevices = all.Where(dev => {
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

            var sorted = matchedDevices.OrderBy(d => {
                string pId = GetParentId(d);
                return (pId != null && map.ContainsKey(pId)) ? Convert.ToInt32(map[pId]["Address"] ?? 0) : 0;
            }).ThenBy(d => Convert.ToInt32(d["AddressOnParent"] ?? 0)).ToList();

            var first = sorted.First();
            return GetMediaFile(first, children) ?? string.Empty;
        }

        private void ParseGen2BootInfo(BootOrderItem item, string fwPath, List<ManagementObject> all, Dictionary<string, ManagementObject> map, Dictionary<string, List<ManagementObject>> children)
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
                        {
                            var parent = map[pId];
                            return Convert.ToInt32(parent["Address"] ?? 0) == cIdx;
                        }
                        return false;
                    });

                    item.Description = GetMediaFile(drive, children) ?? string.Empty;
                    item.Icon = (item.Description.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ? "\uE958" : "\uEDA2";
                }
            }
            else if (fwPath.Contains("MAC("))
            {
                item.Icon = "\uE774";
                item.Description = "PXE 网络引导";
            }
            else if (fwPath.Contains(".efi"))
            {
                item.Icon = "\uE74C";
                item.Description = "Windows 启动管理器";
            }
            else
            {
                item.Icon = "\uE713";
                item.Description = "系统内置引导项";
            }
        }

        private string GetMediaFile(ManagementObject drive, Dictionary<string, List<ManagementObject>> children)
        {
            if (drive == null) return null;
            string dId = NormalizeId(drive["InstanceID"]?.ToString());

            if (children.ContainsKey(dId))
            {
                foreach (var media in children[dId])
                {
                    ushort type = Convert.ToUInt16(media["ResourceType"]);
                    if (type == 31 || type == 16 || type == 22)
                    {
                        var hrRaw = media["HostResource"];
                        if (hrRaw is System.Collections.IEnumerable enumerable)
                        {
                            foreach (var pathObj in enumerable)
                            {
                                string path = pathObj?.ToString();
                                if (!string.IsNullOrEmpty(path))
                                {
                                    return System.IO.Path.GetFileName(path.Replace("file://", "").Replace("/", "\\"));
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }

        private ManagementObject GetVmObject(string vmName) =>
            new ManagementObjectSearcher($"\\\\.\\{Namespace}", $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName='{vmName}'")
            .Get().Cast<ManagementObject>().FirstOrDefault();

        private ManagementObject GetVmSettings(ManagementObject vm) =>
            vm.GetRelated("Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState", null, null, null, null, false, null)
            .Cast<ManagementObject>().FirstOrDefault();

        private List<ManagementObject> GetVmHardware(ManagementObject settings)
        {
            var list = settings.GetRelated("Msvm_ResourceAllocationSettingData", "Msvm_VirtualSystemSettingDataComponent", null, null, null, null, false, null).Cast<ManagementObject>().ToList();
            list.AddRange(settings.GetRelated("Msvm_StorageAllocationSettingData", "Msvm_VirtualSystemSettingDataComponent", null, null, null, null, false, null).Cast<ManagementObject>().ToList());
            return list;
        }

        private Dictionary<string, List<ManagementObject>> BuildChildrenMap(List<ManagementObject> hardware)
        {
            var map = new Dictionary<string, List<ManagementObject>>();
            foreach (var res in hardware)
            {
                string pId = GetParentId(res);
                if (pId != null)
                {
                    if (!map.ContainsKey(pId)) map[pId] = new List<ManagementObject>();
                    map[pId].Add(res);
                }
            }
            return map;
        }

        private string GetParentId(ManagementObject res)
        {
            string p = res["Parent"]?.ToString();
            if (string.IsNullOrEmpty(p)) return null;
            var m = Regex.Match(p, @"InstanceID=[""']([^""']+)[""']");
            return m.Success ? NormalizeId(m.Groups[1].Value) : null;
        }

        private string NormalizeId(string id) => id?.Replace("\\\\", "\\");

        private ManagementObject GetManagementService() =>
            new ManagementClass(new ManagementPath($"\\\\.\\{Namespace}:Msvm_VirtualSystemManagementService"))
            .GetInstances().Cast<ManagementObject>().First();

        #endregion
    }
}