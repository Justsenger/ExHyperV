using Microsoft.Win32;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ExHyperV.Services
{
    /// <summary>读取和设置宿主从文件加载 OpenHCL 固件的策略。</summary>
    public static class HostOpenHclService
    {
        private static readonly object FirmwareAclLock = new();
        private static readonly SecurityIdentifier HyperVVirtualMachinesSid = new("S-1-5-83-0");

        private const string VirtualizationKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
        private const string AllowFirmwareLoadFromFileValue = "AllowFirmwareLoadFromFile";

        /// <summary>读取主机从文件加载 OpenHCL 固件的策略。</summary>
        /// <returns>64 位注册表视图中的策略 DWORD 为 1 时为 true，未配置或其他值为 false；注册表读取异常直接传播。</returns>
        public static bool IsFirmwareLoadFromFileEnabled()
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(VirtualizationKey);
            return key?.GetValue(AllowFirmwareLoadFromFileValue) is int value && value == 1;
        }

        /// <summary>写入或删除主机从文件加载固件的策略。</summary>
        /// <remarks>这是宿主策略。关闭时删除启用配置，恢复系统默认；虚拟机的固件文件路径需另行配置。</remarks>
        /// <returns>成功标志及失败时的错误消息。</returns>
        /// <param name="enabled">true 启用从文件加载，false 删除启用策略。</param>
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

        /// <summary>给指定固件文件补充 Hyper-V 虚拟机账户组的读取权限。</summary>
        /// <remarks>需要修改文件 ACL 的权限；只修改指定文件，不递归放宽目录，不添加其他账户的权限。</remarks>
        /// <returns>成功标志及错误消息；文件不存在或 ACL 操作失败时返回失败。</returns>
        /// <param name="filePath">要修改访问控制的固件文件完整路径。</param>
        internal static (bool Success, string Error) GrantFirmwareReadAccess(string filePath)
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
