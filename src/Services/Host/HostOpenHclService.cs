using Microsoft.Win32;

namespace ExHyperV.Services
{
    /// <summary>
    /// 管理 Hyper-V 从文件加载 OpenHCL/IGVM 开发固件所需的宿主机全局策略。
    /// </summary>
    public static class HostOpenHclService
    {
        private const string VirtualizationKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
        private const string AllowFirmwareLoadFromFileValue = "AllowFirmwareLoadFromFile";

        public static bool IsFirmwareLoadFromFileEnabled()
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(VirtualizationKey);
            return key?.GetValue(AllowFirmwareLoadFromFileValue) is int value && value == 1;
        }

        public static (bool Success, string Error) SetFirmwareLoadFromFileEnabled(bool enabled)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

                if (enabled)
                {
                    using var key = baseKey.CreateSubKey(VirtualizationKey, writable: true);
                    if (key == null)
                        return (false, Properties.Resources.Error_Host_OpenHclRegistryUnavailable);

                    key.SetValue(AllowFirmwareLoadFromFileValue, 1, RegistryValueKind.DWord);
                }
                else
                {
                    using var key = baseKey.OpenSubKey(VirtualizationKey, writable: true);
                    key?.DeleteValue(AllowFirmwareLoadFromFileValue, throwOnMissingValue: false);
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
