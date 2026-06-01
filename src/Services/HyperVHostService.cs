using System.Management;
using ExHyperV.Api;
using Microsoft.Win32;

namespace ExHyperV.Services
{
    /// <summary>
    /// 提供 Hyper-V 环境检测、状态查询及功能管理服务。
    /// </summary>
    public class HyperVHostService
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

        public bool GetGpuStrategyEnabled()
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
        /// 禁用 Hyper-V。Microsoft-Hyper-V-All 涵盖所有子组件，removePayload=false 保留文件可重新启用。
        /// </summary>
        public async Task<bool> DisableHyperVAsync()
        {
            var result = await DismApi.DisableFeatureAsync("Microsoft-Hyper-V-All", removePayload: false);
            return result.Success;
        }

        /// <summary>
        /// 启用 Hyper-V。Microsoft-Hyper-V-All + enableAll=true 自动处理所有子组件依赖。
        /// </summary>
        public async Task<bool> EnableHyperVAsync()
        {
            var result = await DismApi.EnableFeatureAsync("Microsoft-Hyper-V-All", enableAll: true);
            return result.Success;
        }

        // ── 内部 ────────────────────────────────────────────────────

        private string BuildHyperVStatusText(bool hypervisor, int vmmsStatus, bool wmiReady)
        {
            if (hypervisor && vmmsStatus == 1 && wmiReady)
                return string.Empty;
            var missing = new List<string>();
            if (!hypervisor) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_HypervisorInactive);
            if (vmmsStatus == 0) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_VmmsMissing);
            else if (vmmsStatus != 1) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_VmmsNotRunning);
            if (!wmiReady) missing.Add(ExHyperV.Properties.Resources.HostPageViewModel_WmiNamespaceMissing);
            return missing.Count > 0
                ? string.Format(ExHyperV.Properties.Resources.HostPageViewModel_MissingComponents, string.Join("；", missing))
                : ExHyperV.Properties.Resources.HostPageViewModel_StatusUnknown;
        }
    }
}