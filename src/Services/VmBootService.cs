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
            { 0, ("软盘", "\uE7F1") },
            { 1, ("光驱", "\uE958") },
            { 2, ("IDE 硬盘", "\uEDA2") },
            { 3, ("网络启动 (PXE)", "\uE774") },
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
                catch (Exception ex) { Console.WriteLine("BootOrder Query Error: " + ex.Message); }
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
                    if (vm == null)
                    {
                        Debug.WriteLine($"[BOOT-ERROR] 找不到虚拟机: {vmName}");
                        return false;
                    }
                    using var settings = GetVmSettings(vm);

                    bool isGen2 = settings["VirtualSystemSubType"]?.ToString() == "Microsoft:Hyper-V:SubType:2";
                    Debug.WriteLine($"[BOOT-TRACE] 开始设置 {vmName} ({(isGen2 ? "Gen2" : "Gen1")}) 的引导顺序");

                    if (isGen2)
                    {
                        string[] newOrder = items.Select(i => i.Reference.ToString()).ToArray();
                        // 打印每一项的路径，核对顺序是否真的变了
                        for (int i = 0; i < newOrder.Length; i++)
                            Debug.WriteLine($"[BOOT-DATA] Index {i}: {newOrder[i]}");

                        settings["BootSourceOrder"] = newOrder;
                    }
                    else
                    {
                        ushort[] newOrder = items.Select(i => Convert.ToUInt16(i.Reference)).ToArray();
                        Debug.WriteLine($"[BOOT-DATA] Gen1 Order: {string.Join(", ", newOrder)}");
                        settings["BootOrder"] = newOrder;
                    }

                    using var vmSvc = GetManagementService();
                    using var inParams = vmSvc.GetMethodParameters("ModifySystemSettings");
                    inParams["SystemSettings"] = settings.GetText(TextFormat.CimDtd20);

                    using var outParams = vmSvc.InvokeMethod("ModifySystemSettings", inParams, null);
                    uint returnValue = (uint)outParams["ReturnValue"];

                    Debug.WriteLine($"[BOOT-RESULT] WMI 返回值: {returnValue} (0=成功, 4096=处理中)");

                    return returnValue == 0 || returnValue == 4096;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BOOT-CRITICAL] 发生异常: {ex.Message}\n{ex.StackTrace}");
                    return false;
                }
            });
        }

        #region 核心工具方法

        private string GetGen1HardwareSummary(int code, List<ManagementObject> all, Dictionary<string, ManagementObject> map, Dictionary<string, List<ManagementObject>> children)
        {
            if (code == 3) return "所有网络适配器";

            // 严格匹配 PS 脚本中的逻辑
            var matchedDevices = all.Where(dev => {
                ushort resType = Convert.ToUInt16(dev["ResourceType"]);
                string pId = GetParentId(dev);
                var ctrl = (pId != null && map.ContainsKey(pId)) ? map[pId] : null;
                ushort ctrlResType = ctrl != null ? Convert.ToUInt16(ctrl["ResourceType"]) : (ushort)0;

                if (code == 0 && resType == 14) return true; // Floppy
                if (code == 1 && resType == 16) return true; // CD-ROM
                if (code == 2 && (resType == 17 || resType == 22) && ctrlResType == 5) return true; // IDE HD
                if (code == 4 && (resType == 17 || resType == 22) && ctrlResType == 6) return true; // SCSI HD
                return false;
            }).ToList();

            if (!matchedDevices.Any()) return "无 (未挂载此类设备)";

            // 排序：按控制器地址和插槽地址排序
            var sorted = matchedDevices.OrderBy(d => {
                string pId = GetParentId(d);
                return (pId != null && map.ContainsKey(pId)) ? Convert.ToInt32(map[pId]["Address"] ?? 0) : 0;
            }).ThenBy(d => Convert.ToInt32(d["AddressOnParent"] ?? 0)).ToList();

            var first = sorted.First();
            string fileName = GetMediaFile(first, children);

            if (!string.IsNullOrEmpty(fileName)) return fileName;

            // 如果没有文件，显示物理位置
            string pIdObj = GetParentId(first);
            var ctrlObj = (pIdObj != null && map.ContainsKey(pIdObj)) ? map[pIdObj] : null;
            string ctrlName = ctrlObj != null ? (Convert.ToUInt16(ctrlObj["ResourceType"]) == 5 ? "IDE" : "SCSI") : "未知";
            return $"{ctrlName} 控制器 {ctrlObj?["Address"] ?? 0}, 端口 {first["AddressOnParent"]}";
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
                            int ctrlAddr = Convert.ToInt32(parent["Address"] ?? 0);
                            return ctrlAddr == cIdx;
                        }
                        return false;
                    });

                    item.Description = GetMediaFile(drive, children) ?? $"SCSI {cIdx}:{sIdx}";
                    item.Icon = (item.Description.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ? "\uE958" : "\uEDA2";
                }
            }
            else if (fwPath.Contains("MAC("))
            {
                item.Icon = "\uE774";
                var m = Regex.Match(fwPath, @"MAC\(([A-F0-9]+)\)");
                item.Description = m.Success ? $"网络启动 (MAC: {m.Groups[1].Value})" : "网络启动";
            }
            else if (fwPath.Contains(".efi"))
            {
                item.Icon = "\uE7E8";
                item.Description = "Windows Boot Manager";
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
                    // 31 是二代 VHDX 常用的 SASD 类型
                    if (type == 31 || type == 16 || type == 22)
                    {
                        // 修正：兼容多种数组类型
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