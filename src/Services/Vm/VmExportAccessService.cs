using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ExHyperV.Services;

/// <summary>
/// Ensures that files created by the Hyper-V service remain accessible to the
/// user who initiated the export.
/// </summary>
internal static class VmExportAccessService
{
    private static readonly object AclLock = new();

    public static void EnsureCurrentUserCanModifyTree(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
            throw new DirectoryNotFoundException(path);

        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier? currentUser = identity.User;
        if (currentUser == null)
            throw new InvalidOperationException(Properties.Resources.VmPage_CurrentUserUnavailable);

        lock (AclLock)
        {
            DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier));

            const InheritanceFlags requiredInheritance =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            bool alreadyGranted = rules
                .OfType<FileSystemAccessRule>()
                .Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow
                    && currentUser.Equals(rule.IdentityReference)
                    && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                    && (rule.InheritanceFlags & requiredInheritance) == requiredInheritance
                    && rule.PropagationFlags == PropagationFlags.None);

            if (alreadyGranted)
                return;

            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.Modify,
                requiredInheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
    }
}
