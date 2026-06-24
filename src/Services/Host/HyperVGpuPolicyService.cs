using System.Diagnostics;
using Microsoft.Win32;

namespace ExHyperV.Services
{
    /// <summary>
    /// 调整 Hyper-V GPU 分配相关的注册表策略。
    /// 注：这些是机器范围（HKLM）策略，需管理员权限。
    /// </summary>
    public static class HyperVGpuPolicyService
    {
        private const string GpuAssignmentPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\HyperV";

        /// <summary>
        /// 允许不受支持的 GPU 分配（关闭两个 require-secure/supported 限制）。
        /// </summary>
        public static void AllowUnsupportedGpuAssignment()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(GpuAssignmentPolicyKey);
                key.SetValue("RequireSecureDeviceAssignment", 0, RegistryValueKind.DWord);
                key.SetValue("RequireSupportedDeviceAssignment", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AllowUnsupportedGpuAssignment failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 重置 GPU 分配策略到默认（删除两个 require-secure/supported 注册表值）。
        /// </summary>
        public static void ResetGpuAssignmentPolicy()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(GpuAssignmentPolicyKey, writable: true);
                if (key == null) return;
                key.DeleteValue("RequireSecureDeviceAssignment", throwOnMissingValue: false);
                key.DeleteValue("RequireSupportedDeviceAssignment", throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ResetGpuAssignmentPolicy failed: {ex.Message}");
            }
        }
    }
}
