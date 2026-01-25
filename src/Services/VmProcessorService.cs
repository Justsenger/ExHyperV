using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
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
        // Hyper-V WMI 命名空间
        private const string Namespace = @"root\virtualization\v2";

        public async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 1. 定位虚拟机对象
                    using var vmSearcher = new ManagementObjectSearcher(Namespace,
                        $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'");

                    using var vmEntry = vmSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmEntry == null) return null;

                    // 2. 获取当前生效的设置外壳 (Realized SettingData)
                    using var settingsSearcher = new ManagementObjectSearcher(Namespace,
                        $"ASSOCIATORS OF {{{vmEntry.Path.Path}}} WHERE ResultClass = Msvm_VirtualSystemSettingData");

                    using var settingData = settingsSearcher.Get().Cast<ManagementObject>()
                        .FirstOrDefault(s => s["VirtualSystemType"].ToString() == "Microsoft:Hyper-V:System:Realized");

                    if (settingData == null) return null;

                    // 3. 定位到具体的处理器设置组件 (ProcessorSettingData)
                    using var procSearcher = new ManagementObjectSearcher(Namespace,
                        $"ASSOCIATORS OF {{{settingData.Path.Path}}} WHERE ResultClass = Msvm_ProcessorSettingData");

                    using var procData = procSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (procData == null) return null;

                    // 4. 从 WMI 对象映射到你的模型
                    return new VmProcessorSettings
                    {
                        // 核心数
                        Count = Convert.ToInt32(procData["VirtualQuantity"]),

                        // 预留与限制：WMI 内部以 100,000 为 100%，需除以 1000 还原为 0-100 整数
                        Reserve = Convert.ToInt32(procData["Reservation"]) / 1000,
                        Maximum = Convert.ToInt32(procData["Limit"]) / 1000,

                        // 权重
                        RelativeWeight = Convert.ToInt32(procData["Weight"]),

                        // 开关项
                        ExposeVirtualizationExtensions = Convert.ToBoolean(procData["ExposeVirtualizationExtensions"]),
                        EnableHostResourceProtection = Convert.ToBoolean(procData["EnableHostResourceProtection"]),

                        // 兼容性项 (根据 WMI 映射)
                        CompatibilityForMigrationEnabled = Convert.ToBoolean(procData["LimitProcessorFeatures"]),
                        CompatibilityForOlderOperatingSystemsEnabled = Convert.ToBoolean(procData["LimitCPUID"]),

                        // SMT 模式 
                        // ★ 修正：根据你的 WMI 截图，属性名确定为 HwThreadsPerCore ★
                        SmtMode = ConvertHwThreadsToSmtMode(Convert.ToUInt32(procData["HwThreadsPerCore"]))
                    };
                }
                catch (Exception ex)
                {
                    // 调试用，生产环境下建议记录日志
                    System.Diagnostics.Debug.WriteLine($"WMI 读取失败 [{vmName}]: {ex.Message}");
                    return null;
                }
            });
        }
        public async Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VmProcessorSettings processorSettings)
        {
            try
            {
                var safeVmName = vmName.Replace("'", "''");
                var hwThreadCount = ConvertSmtModeToHwThreads(processorSettings.SmtMode);

                var sb = new StringBuilder();
                sb.Append($"$vm = Get-VM -Name '{safeVmName}' -ErrorAction Stop; ");
                sb.Append("Set-VMProcessor -VM $vm ");
                sb.Append($"-Count {processorSettings.Count} ");
                sb.Append($"-Reserve {processorSettings.Reserve} ");
                sb.Append($"-Maximum {processorSettings.Maximum} ");
                sb.Append($"-RelativeWeight {processorSettings.RelativeWeight} ");
                sb.Append($"-ExposeVirtualizationExtensions {ToPsBool(processorSettings.ExposeVirtualizationExtensions)} ");
                sb.Append($"-EnableHostResourceProtection {ToPsBool(processorSettings.EnableHostResourceProtection)} ");
                sb.Append($"-CompatibilityForMigrationEnabled {ToPsBool(processorSettings.CompatibilityForMigrationEnabled)} ");
                sb.Append($"-CompatibilityForOlderOperatingSystemsEnabled {ToPsBool(processorSettings.CompatibilityForOlderOperatingSystemsEnabled)} ");
                sb.Append($"-HwThreadCountPerCore {hwThreadCount} ");
                sb.Append("-ErrorAction Stop");

                await Utils.Run2(sb.ToString());

                return (true, Properties.Resources.SettingsSavedSuccessfully);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private SmtMode ConvertHwThreadsToSmtMode(uint hwThreads) => hwThreads switch
        {
            1 => SmtMode.SingleThread,
            2 => SmtMode.MultiThread,
            _ => SmtMode.Inherit
        };

        private uint ConvertSmtModeToHwThreads(SmtMode smtMode) => smtMode switch
        {
            SmtMode.SingleThread => 1,
            SmtMode.MultiThread => 2,
            _ => 0
        };

        private string ToPsBool(bool value) => value ? "$true" : "$false";
        private long GetLong(object value) { try { return Convert.ToInt64(value); } catch { return 0; } }
        private bool GetBool(object value) { try { return Convert.ToBoolean(value); } catch { return false; } }
    }
}