using Microsoft.Win32;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ExHyperV.Services
{
    /// <summary>
    /// 管理 Hyper-V 从文件加载 OpenHCL/IGVM 开发固件所需的主机全局策略。
    /// </summary>
    public static class HostOpenHclService
    {
        private static readonly object FirmwareAclLock = new();
        private static readonly SecurityIdentifier HyperVVirtualMachinesSid = new("S-1-5-83-0");

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

        /// <summary>
        /// 仅在用户选择的 IGVM 文件上增加 Hyper-V 虚拟机组的读取权限。
        /// 不修改父目录权限，也不复制或移动文件。
        /// </summary>
        public static (bool Success, string Error) GrantFirmwareReadAccess(string filePath)
        {
            try
            {
                lock (FirmwareAclLock)
                {
                    var file = new FileInfo(filePath);
                    if (!file.Exists)
                        return (false, Properties.Resources.VmPage_OpenHclIgvmRequired);

                    FileSecurity security = file.GetAccessControl(AccessControlSections.Access);
                    var rules = security.GetAccessRules(
                        includeExplicit: true,
                        includeInherited: true,
                        targetType: typeof(SecurityIdentifier));

                    bool alreadyReadable = rules
                        .OfType<FileSystemAccessRule>()
                        .Any(rule =>
                            rule.AccessControlType == AccessControlType.Allow &&
                            HyperVVirtualMachinesSid.Equals(rule.IdentityReference) &&
                            (rule.FileSystemRights & FileSystemRights.ReadData) != 0);

                    if (!alreadyReadable)
                    {
                        security.AddAccessRule(new FileSystemAccessRule(
                            HyperVVirtualMachinesSid,
                            FileSystemRights.Read,
                            AccessControlType.Allow));
                        file.SetAccessControl(security);
                    }
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
