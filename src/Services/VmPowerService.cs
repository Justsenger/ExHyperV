using ExHyperV.Tools;
namespace ExHyperV.Services
{
    public class VmPowerService
    {
        public async Task ExecuteControlActionAsync(string vmName, string action)
        {
            string cmd = BuildPsCommand(vmName, action);
            if (!string.IsNullOrEmpty(cmd))
            {
                await Task.Run(() => Utils.Run(cmd));
            }
        }

        private string BuildPsCommand(string vmName, string action)
        {
            var safeName = vmName.Replace("'", "''");

            return action switch
            {
                "Start" => $"Start-VM -Name '{safeName}' -ErrorAction Stop",
                "TurnOff" => $"Stop-VM -Name '{safeName}' -TurnOff -Force -Confirm:$false -ErrorAction Stop",
                "Restart" => $"Restart-VM -Name '{safeName}' -Force -Confirm:$false -ErrorAction Stop",
                "Stop" => $"Stop-VM -Name '{safeName}' -ErrorAction Stop", 
                "Save" => $"Save-VM -Name '{safeName}' -ErrorAction Stop",
                "Suspend" => $"Suspend-VM -Name '{safeName}' -ErrorAction Stop",

                _ => ""
            };
        }
    }
}