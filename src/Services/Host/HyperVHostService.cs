using System.Management;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services
{
    /// <summary>
    /// 提供 Hyper-V 环境检测、状态查询及功能管理服务。
    /// </summary>
    public static class HyperVHostService
    {
        // ── 环境检测 ────────────────────────────────────────────────

        /// <summary>
        /// 检测 CPU 虚拟化是否可用（BIOS 开启且 CPU 支持）。
        /// 逻辑：如果 Hypervisor 正在运行，则虚拟化必定开启；否则检查 CPU 固件标志。
        /// </summary>
        public static bool IsVirtualizationEnabled()
        {
            try
            {
                if (IsHypervisorPresent()) return true;
                var response = WmiApi.QueryAsync(
                    "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor",
                    obj => obj["VirtualizationFirmwareEnabled"] is bool enabled && enabled,
                    WmiScope.CimV2).GetAwaiter().GetResult();
                return response.Success && (response.Data?.Any(x => x) ?? false);
            }
            catch { return false; }
        }

        /// <summary>
        /// 仅检测 Hypervisor（Hyper-V）是否正在运行。
        /// </summary>
        public static bool IsHypervisorPresent()
        {
            try
            {
                var response = WmiApi.QueryAsync(
                    "SELECT HypervisorPresent FROM Win32_ComputerSystem",
                    obj => obj["HypervisorPresent"] is bool present && present,
                    WmiScope.CimV2).GetAwaiter().GetResult();
                return response.Success && (response.Data?.Any(x => x) ?? false);
            }
            catch { return false; }
        }

        /// <summary>
        /// 检测 IOMMU（VT-d / AMD-Vi）状态。
        /// 通过 Win32_DeviceGuard 获取可用安全属性，属性值 3 表示 IOMMU 已启用。
        /// </summary>
        public static bool IsIommuEnabled()
        {
            try
            {
                var response = WmiApi.QueryAsync(
                    "SELECT AvailableSecurityProperties FROM Win32_DeviceGuard",
                    obj => obj["AvailableSecurityProperties"] as int[],
                    WmiScope.DeviceGuard).GetAwaiter().GetResult();
                return response.Success &&
                       (response.Data?.Any(props => props?.Contains(3) ?? false) ?? false);
            }
            catch { return false; }
        }

        /// <summary>
        /// 检测 Hyper-V 虚拟机管理服务（vmms）的运行状态。
        /// 返回值：0 = 未安装，1 = 正在运行，2 = 已停止
        /// </summary>
        public static int GetVmmsStatus()
        {
            try
            {
                var response = WmiApi.QueryAsync(
                    "SELECT State FROM Win32_Service WHERE Name = 'vmms'",
                    obj => obj["State"]?.ToString() ?? string.Empty,
                    WmiScope.CimV2).GetAwaiter().GetResult();
                if (!response.Success || response.Data == null || response.Data.Count == 0)
                    return 0;
                return response.Data.Any(s => s.Equals("Running", StringComparison.OrdinalIgnoreCase))
                    ? 1 : 2;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 检查当前系统是否为 Server 系统。
        /// 只要不是 "WinNT"（工作站），即视为 Server。
        /// </summary>
        public static bool IsServerSystem()
        {
            try
            {
                using var key = Registry.LocalMachine
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
                var type = key?.GetValue("ProductType")?.ToString();
                return type != null && !type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ── Hyper-V 状态 ────────────────────────────────────────────

        public static bool IsHyperVWmiNamespaceAvailable()
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

        public static async Task<(bool IsReady, bool IsInstalled, string StatusText)> GetHyperVStatusAsync()
        {
            var hTask = Task.Run(IsHypervisorPresent);
            var vTask = Task.Run(GetVmmsStatus);
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

        // ── GPU / DISM ──────────────────────────────────────────────

        public static bool GetGpuStrategyEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\HyperV");
                if (key == null) return false;
                return key.GetValue("RequireSecureDeviceAssignment") != null
                    && key.GetValue("RequireSupportedDeviceAssignment") != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 禁用 Hyper-V：不卸载平台，仅 bcdedit /set hypervisorlaunchtype off 关闭监控程序的开机加载。
        /// 保留 WMI 管理层与全部 VM/交换机配置（应用仍可用、再启用秒级），也是与 VMware/VBox 等共存的标准做法。
        /// 卸载整个 Microsoft-Hyper-V-All 会一并移除本应用依赖的管理层、且重装很慢，故不采用。
        /// 与 EnableHyperVAsync 的 hypervisorlaunchtype auto 对称（issue #211 延伸）。
        /// </summary>
        public static async Task<bool> DisableHyperVAsync()
        {
            return await SetHypervisorLaunchTypeAsync("off");
        }

        /// <summary>
        /// 启用 Hyper-V，并显式将 hypervisorlaunchtype 设回 auto（监控程序开机加载）。
        /// 功能名按系统分支：客户端用伞包 Microsoft-Hyper-V-All（含管理工具）；
        /// Windows Server 上**不存在**该名，须用 Microsoft-Hyper-V（平台功能，含 vmms + WMI 管理层，足够本应用使用）。
        /// 容错：禁用只关 launchtype、从不卸载 → 再启用时平台本就装着（vmms 已注册）；
        /// 此时 DISM 即便因"已启用/名称差异"返回非零，只要 launchtype 设上、vmms 在，就算成功（重启即生效），不误报失败。
        /// issue #211。
        /// </summary>
        public static async Task<bool> EnableHyperVAsync()
        {
            string feature = IsServerSystem() ? "Microsoft-Hyper-V" : "Microsoft-Hyper-V-All";
            var dism = await DismApi.EnableFeatureAsync(feature, enableAll: true);
            bool launchOk = await SetHypervisorLaunchTypeAsync("auto");
            bool featurePresent = dism.Success || await Task.Run(() => GetVmmsStatus() != 0);
            return launchOk && featurePresent;
        }

        /// <summary>
        /// 设置 bcdedit 的 hypervisorlaunchtype（auto/off），控制虚拟机监控程序是否在启动时加载。
        /// 仅启用 Hyper-V 功能不足以让其运行——该值为 off 时监控程序不会加载。
        /// </summary>
        private static async Task<bool> SetHypervisorLaunchTypeAsync(string mode)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit.exe",
                    Arguments = $"/set hypervisorlaunchtype {mode}",
                    Verb = "runas",                 // 提权执行（应用已提权时不再弹 UAC）
                    UseShellExecute = true,         // Verb=runas 要求为 true
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return false;
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // ── 内部 ────────────────────────────────────────────────────

        private static string BuildHyperVStatusText(bool hypervisor, int vmmsStatus, bool wmiReady)
        {
            if (hypervisor && vmmsStatus == 1 && wmiReady)
                return string.Empty;
            var missing = new List<string>();
            if (!hypervisor) missing.Add(Properties.Resources.HostPageViewModel_HypervisorInactive);
            if (vmmsStatus == 0) missing.Add(Properties.Resources.HostPageViewModel_VmmsMissing);
            else if (vmmsStatus != 1) missing.Add(Properties.Resources.HostPageViewModel_VmmsNotRunning);
            if (!wmiReady) missing.Add(Properties.Resources.HostPageViewModel_WmiNamespaceMissing);
            return missing.Count > 0
                ? string.Format(Properties.Resources.HostPageViewModel_MissingComponents, string.Join("；", missing))
                : Properties.Resources.HostPageViewModel_StatusUnknown;
        }
    }
}