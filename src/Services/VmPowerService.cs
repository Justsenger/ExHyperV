using ExHyperV.Tools;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class VmPowerService
    {
        /// <summary>
        /// 已重构：所有电源操作统一使用 PowerShell，以确保错误能够被正确抛出和捕获。
        /// </summary>
        public async Task ExecuteControlActionAsync(string vmName, string action)
        {
            string cmd = BuildPsCommand(vmName, action);
            if (!string.IsNullOrEmpty(cmd))
            {
                // Task.Run 在这里是正确的用法，因为它执行的是一个外部进程 (powershell.exe)
                await Task.Run(() => Utils.Run(cmd));
            }
        }

        private string BuildPsCommand(string vmName, string action)
        {
            // 防止 PS 注入，这是个好习惯
            var safeName = vmName.Replace("'", "''");

            return action switch
            {
                // --- 已将 Start 和 TurnOff 移至此处 ---
                "Start" => $"Start-VM -Name '{safeName}' -ErrorAction Stop",
                "TurnOff" => $"Stop-VM -Name '{safeName}' -TurnOff -Force -Confirm:$false -ErrorAction Stop",

                // --- 原有的 PowerShell 命令 ---
                "Restart" => $"Restart-VM -Name '{safeName}' -Force -Confirm:$false -ErrorAction Stop",
                "Stop" => $"Stop-VM -Name '{safeName}' -ErrorAction Stop", // 这是优雅关机
                "Save" => $"Save-VM -Name '{safeName}' -ErrorAction Stop",
                "Suspend" => $"Suspend-VM -Name '{safeName}' -ErrorAction Stop",

                _ => ""
            };
        }
    }
}