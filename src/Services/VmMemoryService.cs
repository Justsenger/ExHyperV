using ExHyperV.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public interface IVmMemoryService
    {
        Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName);
        Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings newSettings);
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
                        Buffer = memData["TargetMemoryBuffer"] != null ? Convert.ToInt32(memData["TargetMemoryBuffer"]) : 20,

                        HugePagesEnabled = GetBoolProperty(memData, "HugePagesEnabled"),
                        IsHugePagesSupported = HasProperty(memData, "HugePagesEnabled"),

                        MemoryEncryptionPolicy = memData["MemoryEncryptionPolicy"] != null ? Convert.ToByte(memData["MemoryEncryptionPolicy"]) : (byte)0,
                        IsMemoryEncryptionSupported = HasProperty(memData, "MemoryEncryptionPolicy")
                    };

                    return settings;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"读取内存配置异常: {ex.Message}");
                    return null;
                }
            });
        }

        public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings newSettings)
        {
            // 获取当前物理机上的实际值进行对比
            var currentSettings = await GetVmMemorySettingsAsync(vmName);
            if (currentSettings == null)
                return (false, "无法获取虚拟机实时配置，请检查虚拟机是否存在。");

            // 检查是否修改了高级属性 (大页或加密策略)
            bool advancedPropertyChanged =
                currentSettings.HugePagesEnabled != newSettings.HugePagesEnabled ||
                currentSettings.MemoryEncryptionPolicy != newSettings.MemoryEncryptionPolicy;

            // 如果修改了高级属性，必须走 WMI 路径
            if (advancedPropertyChanged)
            {
                return await SetVmMemorySettingsWmiAsync(vmName, newSettings);
            }

            // 否则走 PowerShell 路径（更适合处理基础内存调整）
            return await SetVmMemorySettingsPowerShellAsync(vmName, newSettings);
        }

        private async Task<(bool Success, string Message)> SetVmMemorySettingsWmiAsync(string vmName, VmMemorySettings memorySettings)
        {
            try
            {
                var scope = GetConnectedScope();

                // 1. 定位虚拟机
                using var vmSearcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{vmName.Replace("'", "''")}'"));
                using var vmObject = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (vmObject == null) return (false, "找不到虚拟机实例。");

                // 2. 获取管理服务
                using var vsms = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null)
                    .GetInstances().Cast<ManagementObject>().FirstOrDefault();
                if (vsms == null) return (false, "Hyper-V 管理服务不可用。");

                // 3. 获取配置数据 (SettingData)
                var allSettings = vmObject.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                using var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                                     ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                // 4. 获取内存配置项 (MemorySettingData)
                using var rawMemData = settingData.GetRelated("Msvm_MemorySettingData").Cast<ManagementObject>().FirstOrDefault();
                if (rawMemData == null) return (false, "无法定位内存配置对象。");

                // 必须重新通过路径加载对象以确保其可写入性
                using var memData = new ManagementObject(scope, rawMemData.Path, null);
                memData.Get();

                // 5. 应用新值
                ApplyMemorySettingsToWmiObject(memData, memorySettings);

                // 6. 调用 WMI 修改方法
                string xml = memData.GetText(TextFormat.CimDtd20);
                using var inParams = vsms.GetMethodParameters("ModifyResourceSettings");
                inParams["ResourceSettings"] = new string[] { xml };

                using var outParams = vsms.InvokeMethod("ModifyResourceSettings", inParams, null);
                uint ret = (uint)outParams["ReturnValue"];

                if (ret == 0) return (true, "设置已保存。");
                if (ret == 4096) return await WaitForJobAsync(outParams, scope);

                return (false, $"WMI 错误代码: {ret}");
            }
            catch (Exception ex)
            {
                return (false, $"高级设置应用异常: {ex.Message}");
            }
        }

        private void ApplyMemorySettingsToWmiObject(ManagementObject memData, VmMemorySettings memorySettings)
        {
            // 基础属性
            memData["VirtualQuantity"] = (ulong)memorySettings.Startup;
            memData["Weight"] = (uint)(memorySettings.Priority * 100);

            // 内存加密 (UInt8)
            if (HasProperty(memData, "MemoryEncryptionPolicy"))
            {
                // 显式转换为 byte 以匹配 WMI UInt8
                memData["MemoryEncryptionPolicy"] = (byte)memorySettings.MemoryEncryptionPolicy;
            }

            // 大页内存 (Boolean)
            if (HasProperty(memData, "HugePagesEnabled"))
            {
                memData["HugePagesEnabled"] = memorySettings.HugePagesEnabled;
            }

            // 动态内存逻辑处理
            if (memorySettings.HugePagesEnabled)
            {
                // 开启大页时，Hyper-V 强制要求禁用动态内存
                memData["DynamicMemoryEnabled"] = false;
                memData["Reservation"] = (ulong)memorySettings.Startup;
                memData["Limit"] = (ulong)memorySettings.Startup;
            }
            else
            {
                memData["DynamicMemoryEnabled"] = memorySettings.DynamicMemoryEnabled;
                if (memorySettings.DynamicMemoryEnabled)
                {
                    memData["Reservation"] = (ulong)memorySettings.Minimum;
                    memData["Limit"] = (ulong)memorySettings.Maximum;
                    if (HasProperty(memData, "TargetMemoryBuffer"))
                        memData["TargetMemoryBuffer"] = (uint)memorySettings.Buffer;
                }
                else
                {
                    memData["Reservation"] = (ulong)memorySettings.Startup;
                    memData["Limit"] = (ulong)memorySettings.Startup;
                }
            }
        }

        #region Helpers & Standard Implementations (保持原样但增加健壮性)

        private async Task<(bool Success, string Message)> SetVmMemorySettingsPowerShellAsync(string vmName, VmMemorySettings memorySettings)
        {
            try
            {
                var script = new StringBuilder();
                var name = vmName.Replace("'", "''");
                script.Append($"Set-VMMemory -VMName '{name}' -StartupBytes {memorySettings.Startup}MB -Priority {memorySettings.Priority} ");
                script.Append($"-DynamicMemoryEnabled ${(memorySettings.DynamicMemoryEnabled ? "true" : "false")} ");

                if (memorySettings.DynamicMemoryEnabled)
                {
                    script.Append($"-MinimumBytes {memorySettings.Minimum}MB -MaximumBytes {memorySettings.Maximum}MB -Buffer {memorySettings.Buffer}");
                }

                await Utils.Run2(script.ToString());
                return (true, "基础内存设置已应用。");
            }
            catch (Exception ex) { return (false, $"PS执行失败: {ex.Message}"); }
        }

        private async Task<(bool Success, string Message)> WaitForJobAsync(ManagementBaseObject outParams, ManagementScope scope)
        {
            string jobPath = outParams["Job"]?.ToString();
            if (string.IsNullOrEmpty(jobPath)) return (true, "操作成功完成。");

            return await Task.Run(() =>
            {
                using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    job.Get();
                    ushort state = (ushort)job["JobState"];
                    if (state == 7) return (true, "异步任务成功。");
                    if (state == 10 || state == 8 || state == 9)
                        return (false, $"任务失败: {job["ErrorDescription"]}");

                    if (watch.Elapsed.TotalSeconds > 40) return (false, "任务执行超时。");
                    System.Threading.Thread.Sleep(500);
                }
            });
        }

        private bool HasProperty(ManagementObject obj, string propName) =>
            obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));

        private bool GetBoolProperty(ManagementObject obj, string propName)
        {
            if (!HasProperty(obj, propName) || obj[propName] == null) return false;
            return Convert.ToBoolean(obj[propName]);
        }
        #endregion
    }
}