using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Linq;
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
        public async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
        {
            try
            {
                var safeVmName = vmName.Replace("'", "''");
                var script = $"Get-VM -Name '{safeVmName}' -ErrorAction Stop | Get-VMProcessor | Select-Object Count, Reservation, Maximum, RelativeWeight, ExposeVirtualizationExtensions, EnableHostResourceProtection, CompatibilityForMigrationEnabled, CompatibilityForOlderOperatingSystemsEnabled, HwThreadCountPerCore";

                var results = await Utils.Run2(script);
                var psObj = results.FirstOrDefault();

                if (psObj == null) return null;

                dynamic data = psObj;

                // ★★★ 重点修改：增加了 (int) 强转 ★★★
                return new VmProcessorSettings
                {
                    Count = (int)GetLong(data.Count),
                    Reserve = (int)GetLong(data.Reservation),
                    Maximum = (int)GetLong(data.Maximum),
                    RelativeWeight = (int)GetLong(data.RelativeWeight),

                    ExposeVirtualizationExtensions = GetBool(data.ExposeVirtualizationExtensions),
                    EnableHostResourceProtection = GetBool(data.EnableHostResourceProtection),
                    CompatibilityForMigrationEnabled = GetBool(data.CompatibilityForMigrationEnabled),
                    CompatibilityForOlderOperatingSystemsEnabled = GetBool(data.CompatibilityForOlderOperatingSystemsEnabled),
                    SmtMode = ConvertHwThreadsToSmtMode((uint)GetLong(data.HwThreadCountPerCore))
                };
            }
            catch
            {
                return null;
            }
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

        private ExHyperV.Models.SmtMode ConvertHwThreadsToSmtMode(uint hwThreads) => hwThreads switch
        {
            1 => ExHyperV.Models.SmtMode.SingleThread,
            2 => ExHyperV.Models.SmtMode.MultiThread,
            _ => ExHyperV.Models.SmtMode.Inherit
        };

        private uint ConvertSmtModeToHwThreads(ExHyperV.Models.SmtMode smtMode) => smtMode switch
        {
            ExHyperV.Models.SmtMode.SingleThread => 1,
            ExHyperV.Models.SmtMode.MultiThread => 2,
            _ => 0
        };

        private string ToPsBool(bool value) => value ? "$true" : "$false";
        private long GetLong(object value) { try { return Convert.ToInt64(value); } catch { return 0; } }
        private bool GetBool(object value) { try { return Convert.ToBoolean(value); } catch { return false; } }
    }
}