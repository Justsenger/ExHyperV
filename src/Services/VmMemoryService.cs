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
                        Priority = memData["Weight"] != null ? Convert.ToInt32(memData["Weight"]) / 100 : 50, // 增加健壮性
                        DynamicMemoryEnabled = Convert.ToBoolean(memData["DynamicMemoryEnabled"]),
                        Buffer = memData["TargetMemoryBuffer"] != null ? Convert.ToInt32(memData["TargetMemoryBuffer"]) : 20,

                        // --- 升级：使用 BackingPageSize ---
                        BackingPageSize = memData["BackingPageSize"] != null ? Convert.ToByte(memData["BackingPageSize"]) : (byte)0,
                        IsPageSizeSelectionSupported = HasProperty(memData, "BackingPageSize"),

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
            var currentSettings = await GetVmMemorySettingsAsync(vmName);
            if (currentSettings == null)
                return (false, "无法获取虚拟机实时配置，请检查虚拟机是否存在。");

            // --- 升级：检查高级属性的修改 ---
            // 现在 BackingPageSize 和 MemoryEncryptionPolicy 都是高级属性
            bool advancedPropertyChanged =
                currentSettings.BackingPageSize != newSettings.BackingPageSize ||
                currentSettings.MemoryEncryptionPolicy != newSettings.MemoryEncryptionPolicy;

            // 只要修改了高级属性，或者动态内存的开关状态发生变化，就必须走 WMI 路径
            // 因为 PowerShell 不支持在禁用动态内存的同时修改 StartupBytes
            if (advancedPropertyChanged || currentSettings.DynamicMemoryEnabled != newSettings.DynamicMemoryEnabled)
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

                using var vmSearcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{vmName.Replace("'", "''")}'"));
                using var vmObject = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (vmObject == null) return (false, "找不到虚拟机实例。");

                using var vsms = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null)
                    .GetInstances().Cast<ManagementObject>().FirstOrDefault();
                if (vsms == null) return (false, "Hyper-V 管理服务不可用。");

                var allSettings = vmObject.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                using var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                                     ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                using var rawMemData = settingData.GetRelated("Msvm_MemorySettingData").Cast<ManagementObject>().FirstOrDefault();
                if (rawMemData == null) return (false, "无法定位内存配置对象。");

                using var memData = new ManagementObject(scope, rawMemData.Path, null);
                memData.Get();

                ApplyMemorySettingsToWmiObject(memData, memorySettings);

                string xml = memData.GetText(TextFormat.CimDtd20);
                using var inParams = vsms.GetMethodParameters("ModifyResourceSettings");
                inParams["ResourceSettings"] = new string[] { xml };

                using var outParams = vsms.InvokeMethod("ModifyResourceSettings", inParams, null);
                uint ret = (uint)outParams["ReturnValue"];

                if (ret == 0) return (true, "高级内存设置已应用。");
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
            // 默认对齐大小为 1MB (标准页模式下无特殊要求，但设为1以防万一)
            long alignment = 1;

            // --- 升级：写入 BackingPageSize 并确定对齐要求 ---
            if (HasProperty(memData, "BackingPageSize"))
            {
                byte pageSize = memorySettings.BackingPageSize;
                memData["BackingPageSize"] = pageSize;

                if (pageSize == 1) // 大页
                {
                    alignment = 2; // 必须是 2MB 的倍数
                }
                else if (pageSize == 2) // 巨页
                {
                    alignment = 1024; // 必须是 1GB (1024MB) 的倍数
                }
            }

            // --- 核心修正：对齐 VirtualQuantity (启动内存) ---
            long originalStartup = memorySettings.Startup;
            // 使用 (value + alignment - 1) / alignment * alignment 技巧进行向上取整
            long alignedStartup = (originalStartup + alignment - 1) / alignment * alignment;
            memData["VirtualQuantity"] = (ulong)alignedStartup;

            memData["Weight"] = (uint)(memorySettings.Priority * 100);

            if (HasProperty(memData, "MemoryEncryptionPolicy"))
            {
                memData["MemoryEncryptionPolicy"] = (byte)memorySettings.MemoryEncryptionPolicy;
            }

            // --- 核心修正：读取并对齐 MaxMemoryBlocksPerNumaNode ---
            if (HasProperty(memData, "MaxMemoryBlocksPerNumaNode"))
            {
                // 只有在大页模式下才需要关心对齐
                if (alignment > 1)
                {
                    // 读取当前的 NUMA 设置值
                    long currentNumaNodeSize = Convert.ToInt64(memData["MaxMemoryBlocksPerNumaNode"]);
                    // 对其进行向上取整
                    long alignedNumaNodeSize = (currentNumaNodeSize + alignment - 1) / alignment * alignment;
                    memData["MaxMemoryBlocksPerNumaNode"] = (ulong)alignedNumaNodeSize;
                }
                // 如果是标准页 (alignment=1)，我们不主动修改这个值，保留用户或系统的默认设置
            }

            // --- 升级：动态内存逻辑处理 ---
            if (memorySettings.BackingPageSize > 0)
            {
                // 开启大页/巨页时，强制禁用动态内存，并将 Reservation/Limit 设为对齐后的启动内存
                memData["DynamicMemoryEnabled"] = false;
                memData["Reservation"] = (ulong)alignedStartup;
                memData["Limit"] = (ulong)alignedStartup;
            }
            else
            {
                // 标准页(4KB)模式下，才允许启用动态内存
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
                    // 禁用动态内存时，Reservation/Limit 应等于原始（未对齐的）启动内存
                    memData["Reservation"] = (ulong)originalStartup;
                    memData["Limit"] = (ulong)originalStartup;
                }
            }
        }
        #region Helpers & Standard Implementations

        private async Task<(bool Success, string Message)> SetVmMemorySettingsPowerShellAsync(string vmName, VmMemorySettings memorySettings)
        {
            try
            {
                var script = new StringBuilder();
                var name = vmName.Replace("'", "''");
                // PowerShell 只处理动态内存开启时的基础设置调整
                script.Append($"Set-VMMemory -VMName '{name}' -StartupBytes {memorySettings.Startup}MB -Priority {memorySettings.Priority} ");
                script.Append($"-DynamicMemoryEnabled ${true} "); // 走到这里一定是 true
                script.Append($"-MinimumBytes {memorySettings.Minimum}MB -MaximumBytes {memorySettings.Maximum}MB -Buffer {memorySettings.Buffer}");

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

        #endregion
    }
}