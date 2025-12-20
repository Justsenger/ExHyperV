using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class HyperVNUMAService
    {
        public static async Task<bool> GetNumaSpanningEnabledAsync()
        {
            try
            {
                var results = await Utils.Run2("(Get-VMHost).NumaSpanningEnabled");

                if (results != null && results.Any())
                {
                    var output = results[0]?.BaseObject?.ToString();
                    if (bool.TryParse(output?.Trim(), out bool result))
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Get NUMA via Utils: {ex.Message}");
            }

            return true;
        }

        public static async Task<(bool success, string message)> SetNumaSpanningEnabledAsync(bool enabled)
        {
            try
            {
                string boolStr = enabled ? "$true" : "$false";
                string command = $"Set-VMHost -NumaSpanningEnabled {boolStr}";

                await Utils.Run2(command);

                return (true, "设置已更新");
            }
            catch (PowerShellScriptException psEx)
            {
                return (false, psEx.Message.Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}