using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public interface IVmProcessorService
    {
        Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName);
        Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VmProcessorSettings processorSettings);
    }

    public class VmProcessorService : IVmProcessorService
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

        // 1. 读取部分
        public async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
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

                    using var procData = settingData.GetRelated("Msvm_ProcessorSettingData").Cast<ManagementObject>().FirstOrDefault();
                    if (procData == null) return null;

                    return new VmProcessorSettings
                    {
                        Count = Convert.ToInt32(procData["VirtualQuantity"]),
                        Reserve = Convert.ToInt32(procData["Reservation"]) / 1000,
                        Maximum = Convert.ToInt32(procData["Limit"]) / 1000,
                        RelativeWeight = Convert.ToInt32(procData["Weight"]),

                        ExposeVirtualizationExtensions = GetBoolProperty(procData, "ExposeVirtualizationExtensions"),
                        EnableHostResourceProtection = GetBoolProperty(procData, "EnableHostResourceProtection"),
                        CompatibilityForMigrationEnabled = GetBoolProperty(procData, "LimitProcessorFeatures"),
                        CompatibilityForOlderOperatingSystemsEnabled = GetBoolProperty(procData, "LimitCPUID"),
                        SmtMode = ConvertHwThreadsToSmtMode(Convert.ToUInt32(procData["HwThreadsPerCore"])),

                        // 新增高级功能读取
                        DisableSpeculationControls = GetBoolProperty(procData, "DisableSpeculationControls"),
                        HideHypervisorPresent = GetBoolProperty(procData, "HideHypervisorPresent"),
                        EnablePerfmonArchPmu = GetBoolProperty(procData, "EnablePerfmonArchPmu"),
                        // 已移除 EnablePerfmonIpt (Intel 处理器追踪)
                        AllowAcountMcount = GetBoolProperty(procData, "AllowAcountMcount"),
                        EnableSocketTopology = GetBoolProperty(procData, "EnableSocketTopology")
                    };
                }
                catch { return null; }
            });
        }

        // 2. 修改逻辑
        public async Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VmProcessorSettings newSettings)
        {
            var current = await GetVmProcessorAsync(vmName);
            if (current == null) return (false, "找不到虚拟机处理器设置。");

            // 【主机资源保护修复】单独优先处理
            if (current.EnableHostResourceProtection != newSettings.EnableHostResourceProtection)
            {
                try
                {
                    var safeName = vmName.Replace("'", "''");
                    var value = newSettings.EnableHostResourceProtection ? "$true" : "$false";
                    await Utils.Run2($"Set-VMProcessor -VMName '{safeName}' -EnableHostResourceProtection {value}");
                    current.EnableHostResourceProtection = newSettings.EnableHostResourceProtection;
                }
                catch (Exception ex)
                {
                    return (false, $"设置主机资源保护失败: {ex.Message}");
                }
            }

            // 2. 判定是否需要走 WMI 高级修改流程
            bool isAdvancedChanged =
                current.SmtMode != newSettings.SmtMode ||
                current.DisableSpeculationControls != newSettings.DisableSpeculationControls ||
                current.HideHypervisorPresent != newSettings.HideHypervisorPresent ||
                current.EnablePerfmonArchPmu != newSettings.EnablePerfmonArchPmu ||
// 已移除 EnablePerfmonIpt 判断
                current.AllowAcountMcount != newSettings.AllowAcountMcount ||
                current.EnableSocketTopology != newSettings.EnableSocketTopology; // 新增检测

            if (isAdvancedChanged)
            {
                // 走 WMI 流程
                return await SetVmProcessorWmiAsync(vmName, newSettings);
            }
            else
            {
                // 走常规 PowerShell 流程
                return await SetVmProcessorPowerShellAsync(vmName, newSettings);
            }
        }

        #region WMI Implementation (核心修改逻辑)

        private async Task<(bool Success, string Message)> SetVmProcessorWmiAsync(string vmName, VmProcessorSettings processorSettings)
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

                using var rawProcData = settingData.GetRelated("Msvm_ProcessorSettingData").Cast<ManagementObject>().FirstOrDefault();
                if (rawProcData == null) return (false, "找不到处理器配置。");

                using var procData = new ManagementObject(scope, rawProcData.Path, null);
                procData.Get();

                // 应用属性映射
                ApplyProcessorSettingsToWmiObject(procData, processorSettings);

                string xml = procData.GetText(TextFormat.CimDtd20);
                using var inParams = vsms.GetMethodParameters("ModifyResourceSettings");
                inParams["ResourceSettings"] = new string[] { xml };

                using var outParams = vsms.InvokeMethod("ModifyResourceSettings", inParams, null);
                uint ret = (uint)outParams["ReturnValue"];

                if (ret == 0) return (true, "处理器设置已成功应用");
                if (ret == 4096) return await WaitForJobAsync(outParams, scope);

                return (false, $"修改失败(错误码: {ret})");
            }
            catch (Exception ex)
            {
                return (false, $"WMI 异常: {ex.Message}");
            }
        }

        private void ApplyProcessorSettingsToWmiObject(ManagementObject procData, VmProcessorSettings settings)
        {
            // 检查这个配置对象是否属于“运行中”的虚拟机
            bool isRealized = procData.Path.Path.Contains("Realized");

            // 如果是运行中的虚拟机，严禁写入核心数(VirtualQuantity)
            if (!isRealized)
            {
                procData["VirtualQuantity"] = (ulong)settings.Count;
            }

            // 支持运行时热修改的属性
            procData["Reservation"] = (ulong)(settings.Reserve * 1000);
            procData["Limit"] = (ulong)(settings.Maximum * 1000);
            procData["Weight"] = (uint)settings.RelativeWeight;

            // 基础开关
            procData["ExposeVirtualizationExtensions"] = settings.ExposeVirtualizationExtensions;
            procData["EnableHostResourceProtection"] = settings.EnableHostResourceProtection;
            procData["LimitProcessorFeatures"] = settings.CompatibilityForMigrationEnabled;
            procData["LimitCPUID"] = settings.CompatibilityForOlderOperatingSystemsEnabled;

            // SMT 模式
            procData["HwThreadsPerCore"] = (ulong)ConvertSmtModeToHwThreads(settings.SmtMode);

            // 高级功能写入
            TrySetProperty(procData, "DisableSpeculationControls", settings.DisableSpeculationControls);
            TrySetProperty(procData, "HideHypervisorPresent", settings.HideHypervisorPresent);
            TrySetProperty(procData, "EnablePerfmonArchPmu", settings.EnablePerfmonArchPmu);
            // 已移除 EnablePerfmonIpt 的写入
            TrySetProperty(procData, "AllowAcountMcount", settings.AllowAcountMcount);
            TrySetProperty(procData, "EnableSocketTopology", settings.EnableSocketTopology);
        }
        #endregion

        private async Task<(bool Success, string Message)> SetVmProcessorPowerShellAsync(string vmName, VmProcessorSettings settings)
        {
            try
            {
                // 1. 获取当前线上数据进行对比
                var current = await GetVmProcessorAsync(vmName);
                if (current == null) return (false, "无法获取当前配置。");

                var sb = new StringBuilder();
                var safeVmName = vmName.Replace("'", "''");
                sb.Append($"Set-VMProcessor -VMName '{safeVmName}' ");

                bool hasChange = false;

                // 2. 只有值变了，才把参数拼接到命令里
                if (current.Count != settings.Count) { sb.Append($"-Count {settings.Count} "); hasChange = true; }
                if (current.Reserve != settings.Reserve) { sb.Append($"-Reserve {settings.Reserve} "); hasChange = true; }
                if (current.Maximum != settings.Maximum) { sb.Append($"-Maximum {settings.Maximum} "); hasChange = true; }
                if (current.RelativeWeight != settings.RelativeWeight) { sb.Append($"-RelativeWeight {settings.RelativeWeight} "); hasChange = true; }

                // 基础开关
                if (current.EnableHostResourceProtection != settings.EnableHostResourceProtection)
                { sb.Append($"-EnableHostResourceProtection {(settings.EnableHostResourceProtection ? "$true" : "$false")} "); hasChange = true; }

                if (!hasChange) return (true, "配置无变动。");

                await Utils.Run2(sb.ToString());
                return (true, "设置已成功应用");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        #region Helpers & Converters
        private async Task<(bool Success, string Message)> WaitForJobAsync(ManagementBaseObject outParams, ManagementScope scope)
        {
            if (outParams["Job"] == null) return (true, "完成");
            string jobPath = outParams["Job"].ToString();
            return await Task.Run(() =>
            {
                using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
                job.Get();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while ((ushort)job["JobState"] == 4 || (ushort)job["JobState"] == 3)
                {
                    if (watch.ElapsedMilliseconds > 30000) return (false, "任务超时");
                    System.Threading.Thread.Sleep(500);
                    job.Get();
                }
                if ((ushort)job["JobState"] == 7) return (true, "设置已成功应用");
                return (false, job["ErrorDescription"]?.ToString() ?? "任务失败");
            });
        }

        private SmtMode ConvertHwThreadsToSmtMode(uint hwThreads) => hwThreads == 1 ? SmtMode.SingleThread : SmtMode.MultiThread;
        private uint ConvertSmtModeToHwThreads(SmtMode smtMode) => smtMode == SmtMode.SingleThread ? 1u : 2u;

        private bool HasProperty(ManagementObject obj, string propName) => obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
        private void TrySetProperty(ManagementObject obj, string propName, object value) { if (HasProperty(obj, propName)) { try { obj[propName] = value; } catch { } } }
        private bool GetBoolProperty(ManagementObject obj, string propName)
        {
            if (!HasProperty(obj, propName) || obj[propName] == null) return false;
            try { return Convert.ToBoolean(obj[propName]); } catch { return false; }
        }
        #endregion
    }
}