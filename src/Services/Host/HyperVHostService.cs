using System.Management;
using System.Runtime.InteropServices;
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
        /// 检测 CPU 虚拟化是否可用。ARM64 读 VMMonitorModeExtensions；x64 走 CPUID。
        /// 旧逻辑"有 hypervisor 即已启用"在来宾里恒 true 会误报，故弃用。
        /// </summary>
        public static bool IsVirtualizationEnabled()
        {
            try
            {
                // ARM 无 CPUID；该标志不被 hypervisor 掩盖，宿主 true / 来宾无嵌套 false。
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                    return VmMonitorModeExtensionsEnabled();

                if (!CpuId.Supported)   // 非 x64 进程，跑不了 x64 CPUID 机器码
                    return IsHypervisorPresent() || VirtualizationFirmwareEnabled();

                if (!CpuId.HypervisorPresent())         // 物理机无 Hyper-V → 读 BIOS 标志
                    return VirtualizationFirmwareEnabled();
                if (CpuId.IsHyperVRootPartition())      // 物理机跑着 Hyper-V（VFE/VMX 被掩盖）→ 已启用
                    return true;
                return CpuId.HardwareVirtualizationExposed();   // 来宾：VMX/SVM 反映嵌套是否透传
            }
            catch { return false; }
        }

        // Win32_Processor.VirtualizationFirmwareEnabled：固件/BIOS 是否启用虚拟化。x64 物理机无 Hyper-V 时用。
        private static bool VirtualizationFirmwareEnabled()
        {
            var response = WmiApi.QueryAsync(
                "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor",
                obj => obj["VirtualizationFirmwareEnabled"] is bool enabled && enabled,
                WmiScope.CimV2).GetAwaiter().GetResult();
            return response.Success && (response.Data?.Any(x => x) ?? false);
        }

        // Win32_Processor.VMMonitorModeExtensions：ARM64 用。宿主 true / 来宾无嵌套 false，不被 hypervisor 掩盖。
        private static bool VmMonitorModeExtensionsEnabled()
        {
            var response = WmiApi.QueryAsync(
                "SELECT VMMonitorModeExtensions FROM Win32_Processor",
                obj => obj["VMMonitorModeExtensions"] is bool enabled && enabled,
                WmiScope.CimV2).GetAwaiter().GetResult();
            return response.Success && (response.Data?.Any(x => x) ?? false);
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

        /// <summary>
        /// 当前 SKU 是否适用"切换服务器版本"黑魔法(WinNT→ServerNT 欺骗 Hypervisor 解锁 PCIe 直通)。
        /// 不适用(置灰)：真 Server(已是 Server，但企业多会话 ServerRdsh 属客户端、排除在外)、
        /// 家庭版全系(Core*，不含 Hyper-V)、标准专业版/企业版(无衍生功能)。
        /// 其余含 Hyper-V 的衍生客户端版(专业教育/工作站/单语言/中文、教育、企业 LTSC/G/多会话、IoT 企业)→ 适用。
        /// 判定走 EditionID(真实 SKU)而非 ProductType——后者正是黑魔法改的值，用它会致被切的客户端版无法切回。
        /// </summary>
        public static bool IsServerSwitchApplicable()
        {
            return true;   // 已取消版本限制：放行全部 SKU；下方原 EditionID 判定保留，需恢复限制删此行即可
#pragma warning disable CS0162 // 原判定暂不可达，保留备用
            try
            {
                using var key = Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string edition = key?.GetValue("EditionID")?.ToString() ?? "";
                if (edition.Length == 0) return false;

                // 真 Server：EditionID 以 Server 开头（ServerRdsh=企业多会话除外，它是客户端）
                if (edition.StartsWith("Server", StringComparison.OrdinalIgnoreCase) &&
                    !edition.Equals("ServerRdsh", StringComparison.OrdinalIgnoreCase))
                    return false;

                // 家庭版全系列：不含 Hyper-V，黑魔法无前提
                if (edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                    return false;

                // 标准专业版 / 企业版（精确匹配；放行 ProfessionalEducation/EnterpriseS 等衍生版）
                if (edition.Equals("Professional", StringComparison.OrdinalIgnoreCase) ||
                    edition.Equals("ProfessionalN", StringComparison.OrdinalIgnoreCase) ||
                    edition.Equals("Enterprise", StringComparison.OrdinalIgnoreCase) ||
                    edition.Equals("EnterpriseN", StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            }
            catch { return false; }
#pragma warning restore CS0162
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

        // 读 BCD 的 hypervisorlaunchtype 是否非 Off。IsHypervisorActive 在无 CPUID(ARM64/x86)时的退路。
        private static bool IsHypervisorLaunchEnabled()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "bcdedit.exe",
                    Arguments = "/enum {current}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) return true;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                foreach (var raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    // 键名与值（Off/Auto）均不本地化；缺此行 = 默认 Auto = 启用。
                    if (line.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
                        return line.IndexOf("off", StringComparison.OrdinalIgnoreCase) < 0;
                }
                return true;
            }
            catch { return true; }
        }

        // Hyper-V 监控程序是否"真在本机运行"。VM 内 HypervisorPresent 被宿主污染恒 true 不能用。
        // x64：CPUID 根分区位（覆盖"装了却没跑"、不被污染）；ARM64/x86（无 CPUID）：退回 BCD launchtype。
        private static bool IsHypervisorActive()
        {
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64 || !CpuId.Supported)
                return IsHypervisorLaunchEnabled();
            return CpuId.IsHyperVRootPartition();
        }

        public static async Task<(bool IsReady, bool IsInstalled, string StatusText)> GetHyperVStatusAsync()
        {
            var hTask = Task.Run(IsHypervisorActive);
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