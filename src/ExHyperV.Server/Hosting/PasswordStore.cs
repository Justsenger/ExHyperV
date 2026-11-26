using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace ExHyperV.Server.Hosting;

internal sealed record PasswordHash(int Iterations, byte[] Salt, byte[] Hash)
{
    private const int WorkFactor = 600_000;
    internal static PasswordHash Create(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024)
            throw new ArgumentException("口令不能为空，最多接受 1024 个字符。");
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        return new(WorkFactor, salt, Derive(password, salt, WorkFactor));
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    internal bool Verify(string password) => password.Length is > 0 and <= 1024
        && CryptographicOperations.FixedTimeEquals(Hash, Derive(password, Salt, Iterations));
}

internal sealed class PasswordStore(string directory) : StateFiles(directory)
{
    private string FilePath => StatePath("auth.json");
    internal PasswordHash? Load()
    {
        try
        {
            VerifyFile(FilePath);
            using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length <= 4096 && JsonSerializer.Deserialize<PasswordHash>(file) is
                { Iterations: >= 100_000 and <= 2_000_000, Salt.Length: 32, Hash.Length: 32 } hash) return hash;
            throw new InvalidDataException("auth.json 的口令记录无效。");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void Save(PasswordHash hash)
    {
        Write(FilePath, file => JsonSerializer.Serialize(file, hash));
    }

    internal void Clear()
    {
        try { VerifyFile(FilePath); File.Delete(FilePath); }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }
}

internal class StateFiles(string directory)
{
    protected string StatePath(string name) => Path.Combine(directory, name);

    protected void VerifyFile(string path)
    {
        VerifyDirectory();
        if (!DefaultLocation) return;
        VerifyTrusted(File.GetAttributes(path), () => new FileInfo(path).GetAccessControl());
    }

    protected void Write(string path, Action<Stream> serialize)
    {
        if (DefaultLocation) CreateDefaultDirectory();
        else Directory.CreateDirectory(directory);
        VerifyDirectory();
        try { VerifyFile(path); } catch (FileNotFoundException) { }
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serialize(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void VerifyTrustedDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        VerifyTrusted(File.GetAttributes(directory), () => info.GetAccessControl());
    }

    private bool DefaultLocation => Path.TrimEndingDirectorySeparator(directory).Equals(LaunchOptions.DefaultDirectory, StringComparison.OrdinalIgnoreCase);

    private void CreateDefaultDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var principals = new List<SecurityIdentifier> { administrators, new(WellKnownSidType.LocalSystemSid, null) };
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            security.SetOwner(administrators);
        else principals.Add(identity.User!);
        foreach (var sid in principals)
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.CreateDirectory(security, directory);
    }

    private void VerifyDirectory()
    {
        // ProgramData permits local users to create top-level directories. Reject a pre-created or writable default.
        if (!DefaultLocation) return;
        VerifyTrustedDirectory(directory);
    }

    internal static void VerifyTrusted(FileAttributes attributes, Func<FileSystemSecurity> readAcl)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("默认状态路径不能是重解析点。");
        var acl = readAcl();
        using var identity = WindowsIdentity.GetCurrent();
        bool Trusted(SecurityIdentifier sid) => sid == identity.User || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        if (!Trusted((SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))!) ||
            acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == AccessControlType.Allow && !Trusted((SecurityIdentifier)rule.IdentityReference)
                && (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                    | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0))
            throw new UnauthorizedAccessException("默认状态目录的所有者或写权限不安全，请检查目录权限。");
    }
}
