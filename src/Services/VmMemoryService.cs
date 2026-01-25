using ExHyperV.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
// 确保你的 Utils 类所在的命名空间被正确引用
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

        // 读取部分: 保持 WMI 不变
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

                        EnableEpf = GetBoolProperty(memData, "EnableEpf"),
                        IsEpfSupported = HasProperty(memData, "EnableEpf"),
                        HugePagesEnabled = GetBoolProperty(memData, "HugePagesEnabled"),
                        IsHugePagesSupported = HasProperty(memData, "HugePagesEnabled"),
                        EnableHotHint = GetBoolProperty(memData, "EnableHotHint"),
                        IsHotHintSupported = HasProperty(memData, "EnableHotHint"),
                        EnableColdHint = GetBoolProperty(memData, "EnableColdHint"),
                        IsColdHintSupported = HasProperty(memData, "EnableColdHint")
                    };
                    return settings;
                }
                catch { return null; }
            });
        }

        // 修改部分: 优先使用 PowerShell
        public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings newSettings)
        {
            var currentSettings = await GetVmMemorySettingsAsync(vmName);
            if (currentSettings == null)
            {
                return (false, "找不到虚拟机或其内存设置。");
            }

            // 检查是否有 PowerShell 不支持的高级属性被修改
            bool advancedPropertyChanged =
                currentSettings.EnableEpf != newSettings.EnableEpf ||
                currentSettings.HugePagesEnabled != newSettings.HugePagesEnabled ||
                currentSettings.EnableHotHint != newSettings.EnableHotHint ||
                currentSettings.EnableColdHint != newSettings.EnableColdHint;

            if (advancedPropertyChanged)
            {
                // 如果修改了高级属性, 则回退到 WMI 方法 (已修复)
                return await SetVmMemorySettingsWmiAsync(vmName, newSettings);
            }
            else
            {
                // 否则, 使用更稳定简洁的 PowerShell 方法
                return await SetVmMemorySettingsPowerShellAsync(vmName, newSettings);
            }
        }

        #region PowerShell Implementation
        private async Task<(bool Success, string Message)> SetVmMemorySettingsPowerShellAsync(string vmName, VmMemorySettings memorySettings)
        {
            try
            {
                // 参数校验
                if (memorySettings.DynamicMemoryEnabled)
                {
                    if (memorySettings.Minimum > memorySettings.Startup)
                        return (false, "最小内存不能大于启动内存。");
                    if (memorySettings.Startup > memorySettings.Maximum)
                        return (false, "启动内存不能大于最大内存。");
                }

                var scriptBuilder = new StringBuilder();
                // 使用单引号防止注入, 并正确处理包含空格的名称
                var sanitizedVmName = vmName.Replace("'", "''");

                scriptBuilder.Append($"Set-VMMemory -VMName '{sanitizedVmName}' ");
                scriptBuilder.Append($"-StartupBytes {memorySettings.Startup}MB ");
                scriptBuilder.Append($"-Priority {memorySettings.Priority} ");
                scriptBuilder.Append($"-DynamicMemoryEnabled ${(memorySettings.DynamicMemoryEnabled).ToString().ToLower()} ");

                if (memorySettings.DynamicMemoryEnabled)
                {
                    scriptBuilder.Append($"-MinimumBytes {memorySettings.Minimum}MB ");
                    scriptBuilder.Append($"-MaximumBytes {memorySettings.Maximum}MB ");
                    scriptBuilder.Append($"-Buffer {memorySettings.Buffer} ");
                }

                // 使用你提供的 Utils.Run2 方法执行脚本
                await Utils.Run2(scriptBuilder.ToString());

                return (true, "设置已成功应用");
            }
            catch (PowerShellScriptException ex)
            {
                // 捕获由 Run2 抛出的特定异常
                return (false, $"PowerShell 执行失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"系统异常: {ex.Message}");
            }
        }
        #endregion

        #region WMI Implementation (Fallback with Job Waiting Fix)

        private async Task<(bool Success, string Message)> SetVmMemorySettingsWmiAsync(string vmName, VmMemorySettings memorySettings)
        {
            try
            {
                var scope = GetConnectedScope();

                using var vmSearcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ComputerSystem", $"ElementName = '{vmName.Replace("'", "''")}'"));
                using var vmObject = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (vmObject == null) return (false, "找不到虚拟机");

                using var classVsms = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemManagementService"), null);
                using var vsms = classVsms.GetInstances().Cast<ManagementObject>().FirstOrDefault();
                if (vsms == null) return (false, "找不到Hyper-V管理服务。");

                var allSettings = vmObject.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                using var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                                     ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");
                if (settingData == null) return (false, "找不到虚拟机配置。");

                using var rawMemData = settingData.GetRelated("Msvm_MemorySettingData").Cast<ManagementObject>().FirstOrDefault();
                if (rawMemData == null) return (false, "找不到虚拟机内存配置。");

                using var memData = new ManagementObject(scope, rawMemData.Path, null);
                memData.Get();

                // 应用所有修改（标准 + 高级）
                ApplyMemorySettingsToWmiObject(memData, memorySettings);

                string xml = memData.GetText(TextFormat.CimDtd20);
                using var inParams = vsms.GetMethodParameters("ModifyResourceSettings");
                inParams["ResourceSettings"] = new string[] { xml };

                using var outParams = vsms.InvokeMethod("ModifyResourceSettings", inParams, null);
                uint ret = (uint)outParams["ReturnValue"];

                if (ret == 0) // 同步完成
                {
                    return (true, "设置已成功应用");
                }
                if (ret == 4096) // 异步任务已启动
                {
                    // [关键修复] 等待WMI Job完成
                    return await WaitForJobAsync(outParams, scope);
                }

                return (false, $"修改失败(错误码: {ret})");
            }
            catch (Exception ex)
            {
                return (false, $"系统异常: {ex.Message}");
            }
        }

        private void ApplyMemorySettingsToWmiObject(ManagementObject memData, VmMemorySettings memorySettings)
        {
            long startup = memorySettings.Startup;
            memData["VirtualQuantity"] = startup;
            memData["Weight"] = memorySettings.Priority * 100;

            // 应用高级属性
            TrySetProperty(memData, "EnableEpf", memorySettings.EnableEpf);
            TrySetProperty(memData, "EnableHotHint", memorySettings.EnableHotHint);
            TrySetProperty(memData, "EnableColdHint", memorySettings.EnableColdHint);
            TrySetProperty(memData, "HugePagesEnabled", memorySettings.HugePagesEnabled);

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
        }

        private async Task<(bool Success, string Message)> WaitForJobAsync(ManagementBaseObject outParams, ManagementScope scope)
        {
            if (outParams["Job"] == null)
                return (true, "操作成功完成 (无任务)");

            string jobPath = outParams["Job"].ToString();

            return await Task.Run(() =>
            {
                using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
                job.Get();

                // 等待任务完成，设置一个超时，例如30秒
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while ((ushort)job["JobState"] == 4 /* Running */ || (ushort)job["JobState"] == 3 /* Starting */)
                {
                    if (watch.ElapsedMilliseconds > 30000)
                    {
                        return (false, "应用设置超时。");
                    }
                    System.Threading.Thread.Sleep(500);
                    job.Get();
                }
                watch.Stop();

                ushort finalState = (ushort)job["JobState"];
                if (finalState == 7) // Completed
                {
                    return (true, "设置已成功应用");
                }
                else
                {
                    string errorDesc = job["ErrorDescription"]?.ToString() ?? "未知错误";
                    ushort errorCode = (ushort)(job["ErrorCode"] ?? 0);
                    return (false, $"应用设置失败: {errorDesc} (代码: {errorCode})");
                }
            });
        }

        #endregion

        #region WMI Helpers
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
            if (!HasProperty(obj, propName) || obj[propName] == null) return false;
            try { return Convert.ToBoolean(obj[propName]); } catch { return false; }
        }
        #endregion
    }
}