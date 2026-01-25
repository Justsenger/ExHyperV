using ExHyperV.Models;
using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public interface IVmMemoryService
    {
        Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName);
        Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings memorySettings);
    }

    public class VmMemoryService : IVmMemoryService
    {
        private const string Namespace = @"root\virtualization\v2";

        public async Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var vmSearcher = new ManagementObjectSearcher(Namespace,
                        $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'");

                    using var vmEntry = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmEntry == null) return null;

                    using var settingsSearcher = new ManagementObjectSearcher(Namespace,
                        $"ASSOCIATORS OF {{{vmEntry.Path.Path}}} WHERE ResultClass = Msvm_VirtualSystemSettingData");

                    using var settingData = settingsSearcher.Get().Cast<ManagementObject>()
                        .FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized");

                    if (settingData == null) return null;

                    using var memSearcher = new ManagementObjectSearcher(Namespace,
                        $"ASSOCIATORS OF {{{settingData.Path.Path}}} WHERE ResultClass = Msvm_MemorySettingData");

                    using var memData = memSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (memData == null) return null;

                    var settings = new VmMemorySettings
                    {
                        Startup = Convert.ToInt64(memData["VirtualQuantity"]),
                        Minimum = Convert.ToInt64(memData["Reservation"]),
                        Maximum = Convert.ToInt64(memData["Limit"]),
                        Priority = Convert.ToInt32(memData["Weight"]) / 100,
                        DynamicMemoryEnabled = Convert.ToBoolean(memData["DynamicMemoryEnabled"]),

                        // 读取高级属性
                        EnableEpf = GetBoolProperty(memData, "EnableEpf"),
                        HugePagesEnabled = GetBoolProperty(memData, "HugePagesEnabled"),
                        EnableHotHint = GetBoolProperty(memData, "EnableHotHint"),
                        EnableColdHint = GetBoolProperty(memData, "EnableColdHint")
                    };

                    if (memData["TargetMemoryBuffer"] != null)
                        settings.Buffer = Convert.ToInt32(memData["TargetMemoryBuffer"]);
                    else
                        settings.Buffer = 20;

                    return settings;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WMI 读取内存失败 [{vmName}]: {ex.Message}");
                    return null;
                }
            });
        }

        private bool GetBoolProperty(ManagementObject obj, string propName)
        {
            try { return obj[propName] != null && Convert.ToBoolean(obj[propName]); }
            catch { return false; }
        }

        public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings memorySettings)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var scope = new ManagementScope(Namespace);
                    scope.Connect();

                    // 1. 获取虚拟机对象
                    var queryVm = new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{vmName.Replace("'", "''")}'");
                    using var searcherVm = new ManagementObjectSearcher(scope, queryVm);
                    using var vmObject = searcherVm.Get().Cast<ManagementObject>().FirstOrDefault();

                    if (vmObject == null)
                        return (false, "找不到指定的虚拟机");

                    // 2. 获取管理服务 (ModifyResourceSettings 的入口)
                    using var classVsms = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null);
                    using var vsms = classVsms.GetInstances().Cast<ManagementObject>().FirstOrDefault();

                    if (vsms == null)
                        return (false, "无法获取 Hyper-V 管理服务");

                    // 3. 获取 Realized 状态下的设置数据
                    var settingsQuery = new ObjectQuery($"ASSOCIATORS OF {{{vmObject.Path.Path}}} WHERE ResultClass = Msvm_VirtualSystemSettingData");
                    using var settingsSearcher = new ManagementObjectSearcher(scope, settingsQuery);
                    using var settingData = settingsSearcher.Get().Cast<ManagementObject>()
                        .FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized");

                    if (settingData == null) return (false, "无法获取虚拟机配置数据");

                    // 4. 获取内存配置对象
                    var memQuery = new ObjectQuery($"ASSOCIATORS OF {{{settingData.Path.Path}}} WHERE ResultClass = Msvm_MemorySettingData");
                    using var memSearcher = new ManagementObjectSearcher(scope, memQuery);
                    using var memData = memSearcher.Get().Cast<ManagementObject>().FirstOrDefault();

                    if (memData == null) return (false, "无法获取虚拟机内存配置");

                    // 5. 应用属性修改
                    long startup = memorySettings.Startup;
                    memData["VirtualQuantity"] = startup;
                    memData["Weight"] = memorySettings.Priority * 100; // UI 0-100 -> WMI 0-10000

                    // 高级辅助开关
                    memData["EnableEpf"] = memorySettings.EnableEpf;
                    memData["EnableHotHint"] = memorySettings.EnableHotHint;
                    memData["EnableColdHint"] = memorySettings.EnableColdHint;

                    // 定义一个 1TB 的大页对齐上限 (1024 * 1024 MB)
                    // 使用这个数值可以解决诸如 127128 这种不对齐的默认值导致的报错，并预留足够的增长空间
                    const long SafeMaxAlignLimit = 1048576;

                    // ★★★ 处理大页内存与内存对齐逻辑 ★★★
                    if (memorySettings.HugePagesEnabled)
                    {
                        // 开启大页内存必须：关闭动态内存 + 全额预留
                        memData["DynamicMemoryEnabled"] = false;
                        memData["Reservation"] = startup;
                        memData["Limit"] = startup;

                        // 修正对齐报错：将 NUMA 上限设为 1TB。
                        // 这不仅解决了开启大页时的校验失败，还确保用户以后加内存到 512G 等大数值时不会冲突。
                        memData["MaxMemoryBlocksPerNumaNode"] = Math.Max(startup, SafeMaxAlignLimit);

                        memData["HugePagesEnabled"] = true;
                    }
                    else
                    {
                        memData["HugePagesEnabled"] = false;
                        memData["DynamicMemoryEnabled"] = memorySettings.DynamicMemoryEnabled;

                        if (memorySettings.DynamicMemoryEnabled)
                        {
                            memData["Reservation"] = memorySettings.Minimum;
                            memData["Limit"] = memorySettings.Maximum;
                            memData["TargetMemoryBuffer"] = memorySettings.Buffer;

                            // 确保 NUMA 上限不小于最大内存设置
                            long currentNumaLimit = Convert.ToInt64(memData["MaxMemoryBlocksPerNumaNode"]);
                            if (currentNumaLimit < memorySettings.Maximum)
                            {
                                memData["MaxMemoryBlocksPerNumaNode"] = SafeMaxAlignLimit;
                            }
                        }
                        else
                        {
                            memData["Reservation"] = startup;
                            memData["Limit"] = startup;
                        }
                    }

                    // 6. 序列化并提交修改
                    // 使用 CimDtd20 (1) 格式将本地对象转为 WMI 期望的嵌入式实例 XML
                    string embeddedInstance = memData.GetText(TextFormat.CimDtd20);

                    using var inParams = vsms.GetMethodParameters("ModifyResourceSettings");
                    inParams["ResourceSettings"] = new string[] { embeddedInstance };

                    using var outParams = vsms.InvokeMethod("ModifyResourceSettings", inParams, null);

                    uint returnValue = (uint)outParams["ReturnValue"];

                    // 0: 成功, 4096: 异步任务已启动
                    if (returnValue == 0 || returnValue == 4096)
                    {
                        return (true, Properties.Resources.SettingsSavedSuccessfully ?? "设置已成功保存");
                    }
                    else
                    {
                        return (false, $"修改失败，WMI 错误代码: {returnValue}");
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"保存设置时发生异常: {ex.Message}");
                }
            });
        }
    }
}