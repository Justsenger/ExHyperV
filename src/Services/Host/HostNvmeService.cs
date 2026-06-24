using System.Diagnostics;
using Microsoft.Win32;

namespace ExHyperV.Services
{
    /// <summary>
    /// 启用/查询 Windows Server 2025（及 Win11 24H2+）宿主原生 NVMe 驱动。
    /// 机制：Feature Management 覆盖开关（HKLM 策略，需管理员 + 重启生效）。
    /// 开启后宿主 NVMe 走原生多队列（替代旧的 NVMe→SCSI 翻译/单队列），
    /// VHDX 后端 I/O 更快 → VHDX 虚拟机间接受益（客户机仍为合成 SCSI）。
    /// 注：合成存储栈 storvsp/storvsc 始终是 SCSI，guest 拿不到 NVMe 接口（要真 NVMe 只能 DDA）。
    /// </summary>
    public static class HostNvmeService
    {
        private const string OverridesKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
        private const string FeatureValue = "1176759950"; // KB5066835 原生 NVMe 的 Feature ID

        public static bool IsNativeNvmeEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(OverridesKey);
                return key?.GetValue(FeatureValue) is int v && v == 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"IsNativeNvmeEnabled failed: {ex.Message}");
                return false;
            }
        }

        public static void EnableNativeNvme()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(OverridesKey);
                key.SetValue(FeatureValue, 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnableNativeNvme failed: {ex.Message}");
            }
        }

        public static void DisableNativeNvme()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(OverridesKey, writable: true);
                key?.DeleteValue(FeatureValue, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DisableNativeNvme failed: {ex.Message}");
            }
        }
    }
}
