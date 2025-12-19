using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class HyperVNUMAService
    {
        /// <summary>
        /// 使用 PowerShell 获取宿主机 NUMA 跨越状态
        /// </summary>
        public static async Task<bool> GetNumaSpanningEnabledAsync()
        {
            try
            {
                // 命令：直接获取布尔值属性
                // 使用 Utils.Run2 异步执行
                var results = await Utils.Run2("(Get-VMHost).NumaSpanningEnabled");

                if (results != null && results.Count > 0)
                {
                    var output = results[0]?.BaseObject?.ToString();
                    if (!string.IsNullOrEmpty(output) && bool.TryParse(output.Trim(), out bool result))
                    {
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] Get NUMA via Utils: {ex.Message}");
            }

            return true; // 默认为开启，作为兜底
        }

        /// <summary>
        /// 使用 PowerShell 设置宿主机 NUMA 跨越状态
        /// </summary>
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
                // 修改这里：直接返回 psEx.Message，去掉 "PowerShell 错误: " 前缀
                // Trim() 可以去掉末尾可能存在的多余换行符
                return (false, psEx.Message.Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}