using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ExHyperV.Services
{
    /// <summary>
    /// 在调用普通权限的资源管理器前，为当前用户补充默认 Hyper-V 配置目录的读取权限。
    /// </summary>
    public static class VmFolderAccessService
    {
        private static readonly object AclLock = new();

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
                    return (false, "Unable to identify the current Windows user.");

                lock (AclLock)
                {
                    var directory = new DirectoryInfo(fullPath);
                    DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
                    var rules = security.GetAccessRules(
                        includeExplicit: true,
                        includeInherited: true,
                        targetType: typeof(SecurityIdentifier));

                    bool alreadyReadable = rules
                        .OfType<FileSystemAccessRule>()
                        .Any(rule =>
                            rule.AccessControlType == AccessControlType.Allow &&
                            currentUser.Equals(rule.IdentityReference) &&
                            (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute &&
                            (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0);

                    if (!alreadyReadable)
                    {
                        security.AddAccessRule(new FileSystemAccessRule(
                            currentUser,
                            FileSystemRights.ReadAndExecute,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                        directory.SetAccessControl(security);
                    }
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static bool IsDefaultProtectedHyperVPath(string fullPath)
        {
            string protectedRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Hyper-V"));

            string relativePath = Path.GetRelativePath(protectedRoot, fullPath);
            return relativePath != "." &&
                   !Path.IsPathRooted(relativePath) &&
                   !relativePath.Equals("..", StringComparison.Ordinal) &&
                   !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }
}
