using ExHyperV.Models;
using System;
using System.Collections.Generic;
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
        private const string NamespacePath = @"\\.\root\virtualization\v2";

        private ManagementScope GetConnectedScope()
        {
            var options = new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true
            };
            var scope = new ManagementScope(NamespacePath, options);
            scope.Connect();
            return scope;
        }

        public async Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var scope = GetConnectedScope();
                    using var vmSearcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{vmName.Replace("'", "''")}'"));
                    using var vmEntry = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmEntry == null) return null;

                    var allSettings = vmEntry.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                    using var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                                         ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                    if (settingData == null) return null;

                    using var memData = settingData.GetRelated("Msvm_MemorySettingData").Cast<ManagementObject>().FirstOrDefault();
                    if (memData == null) return null;

                    var settings = new VmMemorySettings
                    {
                        Startup = Convert.ToInt64(memData["VirtualQuantity"]),
                        Minimum = Convert.ToInt64(memData["Reservation"]),
                        Maximum = Convert.ToInt64(memData["Limit"]),
                        Priority = Convert.ToInt32(memData["Weight"]) / 100,
                        DynamicMemoryEnabled = Convert.ToBoolean(memData["DynamicMemoryEnabled"]),

                        // 读取属性值及检测宿主机支持情况
                        EnableEpf = GetBoolProperty(memData, "EnableEpf"),
                        IsEpfSupported = HasProperty(memData, "EnableEpf"),

                        HugePagesEnabled = GetBoolProperty(memData, "HugePagesEnabled"),
                        IsHugePagesSupported = HasProperty(memData, "HugePagesEnabled"),

                        EnableHotHint = GetBoolProperty(memData, "EnableHotHint"),
                        IsHotHintSupported = HasProperty(memData, "EnableHotHint"),

                        EnableColdHint = GetBoolProperty(memData, "EnableColdHint"),
                        IsColdHintSupported = HasProperty(memData, "EnableColdHint")
                    };

                    settings.Buffer = memData["TargetMemoryBuffer"] != null ? Convert.ToInt32(memData["TargetMemoryBuffer"]) : 20;
                    return settings;
                }
                catch { return null; }
            });
        }

        public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings memorySettings)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var scope = GetConnectedScope();

                    // 1. 获取虚拟机及管理服务
                    using var vmSearcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{vmName.Replace("'", "''")}'"));
                    using var vmObject = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmObject == null) return (false, "找不到虚拟机");

                    using var classVsms = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null);
                    using var vsms = classVsms.GetInstances().Cast<ManagementObject>().FirstOrDefault();

                    // 2. 定位活动配置
                    var allSettings = vmObject.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                    using var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                                         ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                    using var rawMemData = settingData.GetRelated("Msvm_MemorySettingData").Cast<ManagementObject>().FirstOrDefault();

                    // 3. 强力绑定并刷新实例路径，解决专业版“找不到”问题
                    using var memData = new ManagementObject(scope, rawMemData.Path, null);
                    memData.Get();

                    // 4. 应用基础修改
                    long startup = memorySettings.Startup;
                    memData["VirtualQuantity"] = startup;
                    memData["Weight"] = memorySettings.Priority * 100;

                    // 5. 应用高级属性 (带检测功能，防崩溃)
                    TrySetProperty(memData, "EnableEpf", memorySettings.EnableEpf);
                    TrySetProperty(memData, "EnableHotHint", memorySettings.EnableHotHint);
                    TrySetProperty(memData, "EnableColdHint", memorySettings.EnableColdHint);
                    TrySetProperty(memData, "HugePagesEnabled", memorySettings.HugePagesEnabled);

                    // 6. 大页内存逻辑处理 (包含对齐修复)
                    const long SafeMaxAlignLimit = 1048576; // 1TB
                    if (memorySettings.HugePagesEnabled && HasProperty(memData, "HugePagesEnabled"))
                    {
                        memData["DynamicMemoryEnabled"] = false;
                        memData["Reservation"] = startup;
                        memData["Limit"] = startup;
                        TrySetProperty(memData, "MaxMemoryBlocksPerNumaNode", Math.Max(startup, SafeMaxAlignLimit));
                    }
                    else
                    {
                        memData["DynamicMemoryEnabled"] = memorySettings.DynamicMemoryEnabled;
                        if (memorySettings.DynamicMemoryEnabled)
                        {
                            memData["Reservation"] = memorySettings.Minimum;
                            memData["Limit"] = memorySettings.Maximum;
                            TrySetProperty(memData, "TargetMemoryBuffer", memorySettings.Buffer);

                            // 确保 NUMA 限制不小于最大内存
                            if (HasProperty(memData, "MaxMemoryBlocksPerNumaNode"))
                            {
                                long currentNumaLimit = Convert.ToInt64(memData["MaxMemoryBlocksPerNumaNode"]);
                                if (currentNumaLimit < memorySettings.Maximum)
                                    memData["MaxMemoryBlocksPerNumaNode"] = SafeMaxAlignLimit;
                            }
                        }
                        else
                        {
                            memData["Reservation"] = startup;
                            memData["Limit"] = startup;
                        }
                    }

                    // 7. 序列化并提交修改
                    string xml = memData.GetText(TextFormat.CimDtd20);
                    using var inParams = vsms.GetMethodParameters("ModifyResourceSettings");
                    inParams["ResourceSettings"] = new string[] { xml };

                    using var outParams = vsms.InvokeMethod("ModifyResourceSettings", inParams, null);
                    uint ret = (uint)outParams["ReturnValue"];

                    return (ret == 0 || ret == 4096) ? (true, "设置已成功应用") : (false, $"修改失败(错误码: {ret})");
                }
                catch (Exception ex)
                {
                    return (false, $"系统异常: {ex.Message}");
                }
            });
        }

        private bool HasProperty(ManagementObject obj, string propName)
        {
            return obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
        }

        private void TrySetProperty(ManagementObject obj, string propName, object value)
        {
            if (HasProperty(obj, propName))
            {
                try { obj[propName] = value; } catch { }
            }
        }

        private bool GetBoolProperty(ManagementObject obj, string propName)
        {
            if (HasProperty(obj, propName))
            {
                try { return obj[propName] != null && Convert.ToBoolean(obj[propName]); } catch { }
            }
            return false;
        }
    }
}