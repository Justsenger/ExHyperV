using ExHyperV.Tools;
using System.Threading.Tasks;

//电源服务

namespace ExHyperV.Services
{
    public class VmPowerService
    {
        public async Task ExecuteControlActionAsync(string vmName, string action)
        {
            // 1. 复杂/慢速操作 -> 走 PowerShell
            if (action == "Stop" || action == "Restart" || action == "Save" || action == "Suspend")
            {
                string cmd = BuildPsCommand(vmName, action);
                if (!string.IsNullOrEmpty(cmd))
                {
                    await Task.Run(() => Utils.Run(cmd));
                }
                return;
            }

            // 2. 瞬时/底层操作 -> 走 WMI
            await Task.Run(() =>
            {
                int targetState = action switch
                {
                    "Start" => 2,   // Enabled
                    "TurnOff" => 3, // Disabled
                    _ => -1
                };

                if (targetState != -1)
                {
                    Utils.Wmi.RequestStateChange(vmName, targetState);
                }
            });
        }

        private string BuildPsCommand(string vmName, string action)
        {
            // 防止 PS 注入
            var safeName = vmName.Replace("'", "''");
            return action switch
            {
                "Restart" => $"Restart-VM -Name '{safeName}' -Force -Confirm:$false",
                "Stop" => $"Stop-VM -Name '{safeName}' -ErrorAction Stop",
                "Save" => $"Save-VM -Name '{safeName}'",
                "Suspend" => $"Suspend-VM -Name '{safeName}'",
                _ => ""
            };
        }
    }
}