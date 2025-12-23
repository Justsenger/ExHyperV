using ExHyperV.Tools;
using ExHyperV.ViewModels;
using System.Text;
using System.Text.RegularExpressions;

namespace ExHyperV.Services
{
    public class VmProcessorService : IVmProcessorService
    {
        public async Task<VMProcessorViewModel?> GetVmProcessorAsync(string vmName)
        {
            try
            {
                var safeVmName = vmName.Replace("'", "''");
                var script = $"Get-VM -Name '{safeVmName}' -ErrorAction Stop | Get-VMProcessor | Select-Object " +
                             "Count, Reservation, Maximum, RelativeWeight, " +
                             "ExposeVirtualizationExtensions, EnableHostResourceProtection, " +
                             "CompatibilityForMigrationEnabled, CompatibilityForOlderOperatingSystemsEnabled, " +
                             "HwThreadCountPerCore";

                var results = await Utils.Run2(script);
                var psObj = results.FirstOrDefault();

                if (psObj == null) return null;

                dynamic data = psObj;

                return new VMProcessorViewModel
                {
                    Count = GetLong(data.Count),
                    Reserve = GetLong(data.Reservation),
                    Maximum = GetLong(data.Maximum),
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

        public async Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VMProcessorViewModel processorSettings)
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

                return (true, ExHyperV.Properties.Resources.SettingsSavedSuccessfully);
            }
            catch (Exception ex)
            {
                var friendlyMsg = GetFriendlyErrorMessage(ex.Message);
                return (false, friendlyMsg);
            }
        }
        private string GetFriendlyErrorMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return ExHyperV.Properties.Resources.UnknownError;
            string cleanMsg = rawMessage.Trim();
            cleanMsg = Regex.Replace(cleanMsg, @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "");
            cleanMsg = cleanMsg.Replace("\r", "").Replace("\n", " ");
            var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();
            if (parts.Count >= 2)
            {
                var lastPart = parts.Last();
                if (lastPart.Length > 2)
                {
                    return lastPart + "。";
                }
            }

            return cleanMsg;
        }

        private SmtMode ConvertHwThreadsToSmtMode(uint hwThreads) => hwThreads switch
        {
            0 => SmtMode.Inherit,
            1 => SmtMode.SingleThread,
            2 => SmtMode.MultiThread,
            _ => SmtMode.Inherit
        };

        private uint ConvertSmtModeToHwThreads(SmtMode smtMode) => smtMode switch
        {
            SmtMode.Inherit => 0,
            SmtMode.SingleThread => 1,
            SmtMode.MultiThread => 2,
            _ => 0
        };

        private string ToPsBool(bool value) => value ? "$true" : "$false";

        private long GetLong(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt64(value); } catch { return 0; }
        }

        private bool GetBool(object value)
        {
            if (value == null) return false;
            try { return Convert.ToBoolean(value); } catch { return false; }
        }
    }
}