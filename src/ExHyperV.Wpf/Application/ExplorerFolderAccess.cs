using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ExHyperV.Application
{
    /// <summary>
    /// 在调用普通权限的资源管理器前，为当前用户补充默认 Hyper-V 配置目录的读取权限。
    /// </summary>
    internal static class ExplorerFolderAccess
    {
        private static readonly object AclLock = new();

        /// <summary>对 Hyper-V 默认受保护路径补充当前用户读取权限，便于由资源管理器打开。</summary>
        /// <param name="path">要供资源管理器访问的宿主目录路径。</param>
        /// <returns>范围内返回 ACL 操作结果；范围外跳过权限检查和修改，直接返回成功。</returns>
        public static (bool Success, string Error) EnsureExplorerCanRead(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!IsDefaultProtectedHyperVPath(fullPath))
                    return (true, string.Empty);

                using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                SecurityIdentifier? currentUser = identity.User;
                if (currentUser == null)
                    return (false, Properties.Resources.VmPage_CurrentUserUnavailable);

                using var explorer = ExplorerAccessCheck.Capture(currentUser);

                lock (AclLock)
                {
                    var directory = new DirectoryInfo(fullPath);
                    const AccessControlSections sections = AccessControlSections.Owner |
                        AccessControlSections.Group | AccessControlSections.Access;
                    DirectorySecurity security = directory.GetAccessControl(sections);
                    bool readable = explorer.CanReadAndExecute(security);
                    // null DACL 表示没有访问限制；不能加条目使它变成受限 DACL。
                    bool unrestricted = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0)
                        .DiscretionaryAcl == null;
                    if (!unrestricted && !HasInheritableReadRule(security, currentUser))
                    {
                        security.AddAccessRule(new FileSystemAccessRule(
                            currentUser,
                            FileSystemRights.ReadAndExecute,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                        directory.SetAccessControl(security);
                        security = directory.GetAccessControl(sections);
                        readable = explorer.CanReadAndExecute(security);
                    }
                    if (!readable)
                        return (false, "资源管理器仍无此目录的读取权限；已有权限规则已保留，请检查目录访问限制。");
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VM Folder Access] {ex}");
                return (false, $"{Properties.Resources.VmPage_FolderAccessFailed}：{ex.Message}");
            }
        }

        private static bool HasInheritableReadRule(DirectorySecurity security, SecurityIdentifier currentUser)
        {
            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            return security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    currentUser.Equals(rule.IdentityReference) &&
                    (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
                    (rule.InheritanceFlags & inheritance) == inheritance &&
                    rule.PropagationFlags == PropagationFlags.None);
        }

        private static bool IsDefaultProtectedHyperVPath(string fullPath)
        {
            string protectedRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Hyper-V"));

            string relativePath = Path.GetRelativePath(protectedRoot, fullPath);
            return !Path.IsPathRooted(relativePath) &&
                   !relativePath.Equals("..", StringComparison.Ordinal) &&
                   !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }
}
