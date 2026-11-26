using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 每次离线部署独享；不在服务实例或进程之间缓存来宾目录权限。
internal sealed class GpuDeploymentFileOperations(GuestVolumeFiles volume, Action<string> reportWarning)
{
    private readonly HashSet<string> preparedDirectories = new(StringComparer.OrdinalIgnoreCase);

    internal void PrepareDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        using var parents = volume.PinDirectory(fullPath);
        lock (preparedDirectories)
        {
            if (preparedDirectories.Contains(fullPath)) return;

            bool ownershipSucceeded = false;
            bool accessSucceeded = false;
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            try
            {
                TemporaryPrivileges.Run(["SeTakeOwnershipPrivilege", "SeRestorePrivilege"], () =>
                {
                    var dirInfo = new DirectoryInfo(fullPath);
                    var ownerSecurity = new DirectorySecurity();
                    ownerSecurity.SetOwner(admins);
                    dirInfo.SetAccessControl(ownerSecurity);
                    return true;
                });
                ownershipSucceeded = true;
            }
            catch (Exception error)
            {
                Warn("GuestFileOwnerUpdateFailed", error, $"[GPU Permissions] {fullPath}: Owner update failed: {error.Message}");
            }

            try
            {
                TemporaryPrivileges.Run(["SeTakeOwnershipPrivilege", "SeRestorePrivilege"], () =>
                {
                    var dirInfo = new DirectoryInfo(fullPath);
                    DirectorySecurity acl = dirInfo.GetAccessControl(AccessControlSections.Access);
                    acl.AddAccessRule(new FileSystemAccessRule(
                        admins,
                        FileSystemRights.FullControl,
                        InheritanceFlags.None,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    dirInfo.SetAccessControl(acl);
                    return true;
                });
                accessSucceeded = true;
            }
            catch (Exception error)
            {
                Warn("GuestFileAclUpdateFailed", error, $"[GPU Permissions] {fullPath}: ACL grant failed: {error.Message}");
            }

            if (ownershipSucceeded && accessSucceeded)
                preparedDirectories.Add(fullPath);
        }
    }

    internal void ReplaceWithLink(string linkPath, string guestTarget, bool replaceExisting)
    {
        using var parents = volume.PinDirectory(Path.GetDirectoryName(linkPath)!);
        string? backupPath = null;
        string? identity = null;
        if (replaceExisting)
        {
            using (var old = volume.PinFile(linkPath)) identity = HostDiskResource.Image(old);
            backupPath = $"{linkPath}.exhyperv-backup-{Guid.NewGuid():N}";
            // 用刚核对的普通文件身份备份，失败时按同一身份移回。
            OwnedFileOperations.MoveOrDelete(linkPath, identity, backupPath);
        }

        try
        {
            // guestTarget 是来宾盘内的绝对路径，不解析成宿主挂载路径。
            File.CreateSymbolicLink(linkPath, guestTarget);
        }
        catch (Exception createError)
        {
            if (backupPath != null)
            {
                try { OwnedFileOperations.MoveOrDelete(backupPath, identity!, linkPath); }
                catch (Exception restoreError)
                {
                    // 目标若被其他操作占用，不删除它；保留备份并报告恢复位置。
                    throw new AggregateException(
                        $"Unable to create link {linkPath}. Rollback failed. Backup retained at {backupPath}.",
                        createError, restoreError);
                }
            }
            throw;
        }

        if (backupPath != null)
        {
            try
            {
                OwnedFileOperations.MoveOrDelete(backupPath, identity!, null);
            }
            catch (Exception error)
            {
                Warn("GuestLinkBackupCleanupFailed", error, $"[GPU Link] Link created, but backup cleanup failed for {backupPath}: {error.Message}");
            }
        }
    }

    private void Warn(string eventName, Exception error, string message)
    {
        CoreDiagnostics.Report(eventName, error: error);
        try { reportWarning(message); }
        catch (Exception diagnosticError) { CoreDiagnostics.Report("WarningObserverFailed", error: diagnosticError); }
    }
}
