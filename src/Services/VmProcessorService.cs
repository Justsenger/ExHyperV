using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.ViewModels;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

                return (true, "设置已成功应用。");
            }
            catch (Exception ex)
            {
                // 【修改点】调用错误清洗方法，去掉废话
                var friendlyMsg = GetFriendlyErrorMessage(ex.Message);
                return (false, friendlyMsg);
            }
        }

        #region Helpers

        /// <summary>
        /// 简单清洗，主要用于去除 GUID，保留操作系统的原生本地化错误提示
        /// </summary>
        private string GetFriendlyErrorMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return "未知错误";

            string cleanMsg = rawMessage.Trim();

            // 去除 GUID (通用正则，匹配 (虚拟机 ID xxxx) 或 (Virtual Machine ID xxxx))
            // 这里的正则兼容了中文括号和英文括号，以及任何语言中包含 "ID GUID" 的模式
            cleanMsg = Regex.Replace(cleanMsg, @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "");

            // 去除换行符，变成一行显示
            cleanMsg = cleanMsg.Replace("\r", "").Replace("\n", " ");

            // Hyper-V 的错误通常是："无法修改 A。无法修改 A。原因 B。"
            // 我们尝试提取最后一部分，因为那通常是真正的“原因”
            var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();

            // 如果句子超过2句，且最后一句长度适中，通常最后一句是真正的 Human Readable 原因
            // 例如日文："プロセッサを変更できません。仮想マシンが実行中です。" -> 取最后一句
            if (parts.Count >= 2)
            {
                var lastPart = parts.Last();
                // 简单的防误判：如果最后一句太短（比如只是一个标点），就显示全文
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

        #endregion
    }
}