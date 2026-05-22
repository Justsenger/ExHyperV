using System.Management;
using ExHyperV.Tools.Api;

namespace ExHyperV.Services
{
    public class HyperVHostService
    {
        public bool IsHyperVWmiNamespaceAvailable()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\virtualization\v2");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
                using var collection = searcher.Get();
                return collection.Count > 0;
            }
            catch { return false; }
        }

        public async Task<(bool IsReady, bool IsInstalled, string StatusText)> GetHyperVStatusAsync()
        {
            var hTask = Task.Run(() => HyperVEnvironmentService.IsHypervisorPresent());
            var vTask = Task.Run(() => HyperVEnvironmentService.GetVmmsStatus());
            var wmiTask = Task.Run(IsHyperVWmiNamespaceAvailable);

            await Task.WhenAll(hTask, vTask, wmiTask);

            bool hypervisor = hTask.Result;
            int vmms = vTask.Result;
            bool wmiReady = wmiTask.Result;

            bool isReady = hypervisor && vmms == 1 && wmiReady;
            bool isInstalled = vmms != 0;
            string statusText = BuildHyperVStatusText(hypervisor, vmms, wmiReady);

            return (isReady, isInstalled, statusText);
        }

        public bool GetGpuStrategyEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\HyperV");
                if (key == null) return false;
                return key.GetValue("RequireSecureDeviceAssignment") != null
                    && key.GetValue("RequireSupportedDeviceAssignment") != null;
            }
            catch { return false; }
        }

        public async Task<bool> DisableHyperVAsync()
        {
            // Microsoft-Hyper-V-All 涵盖所有子组件，removePayload=false 保留文件可重新启用
            // 如果某些机器上 All 不够用，可以补充：
            // "Microsoft-Hyper-V;Microsoft-Hyper-V-Services;Microsoft-Hyper-V-Management-PowerShell;Microsoft-Hyper-V-Management-Clients"
            var result = await DismApi.DisableFeatureAsync("Microsoft-Hyper-V-All", removePayload: false);
            return result.Success;
        }

        public async Task<bool> EnableHyperVAsync()
        {
            // Microsoft-Hyper-V-All + enableAll=true 自动处理所有子组件依赖
            // 如果某些机器上 All 不够用，可以补充：
            // "Microsoft-Hyper-V;Microsoft-Hyper-V-Services;Microsoft-Hyper-V-Management-PowerShell;Microsoft-Hyper-V-Management-Clients"
            var result = await DismApi.EnableFeatureAsync("Microsoft-Hyper-V-All", enableAll: true);
            return result.Success;
        }

        private string BuildHyperVStatusText(bool hypervisor, int vmmsStatus, bool wmiReady)
        {
            if (hypervisor && vmmsStatus == 1 && wmiReady)
                return string.Empty;

            var missing = new List<string>();
            if (!hypervisor) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_5);
            if (vmmsStatus == 0) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_6);
            else if (vmmsStatus != 1) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_7);
            if (!wmiReady) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_9);

            return missing.Count > 0
                ? string.Format(ExHyperV.Properties.Resources.HostPageViewModel_10, string.Join("；", missing))
                : ExHyperV.Properties.Resources.HostPageViewModel_11;
        }
    }
}