using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

// Keep the exact objects readable and non-replaceable until archive verification
// and cleanup finish. No recursive deletion and no traversal through reparse points.
internal sealed class ExportSourceLease : IDisposable
{
    internal sealed record SourceFile(string Path, FileStream Stream);
    private readonly List<(string Path, SafeFileHandle Handle)> directories = [];
    private readonly List<SafeFileHandle> ancestors = [];
    private readonly HashSet<string> ancestorPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceFile> files = [];
    internal IReadOnlyList<SourceFile> Files => files;
    private readonly string root;
    private ExportSourceLease(string root) => this.root = root;

    internal static ExportSourceLease Open(string root, string destination, CancellationToken token)
    {
        var lease = new ExportSourceLease(Path.GetFullPath(root));
        try
        {
            if (Directory.GetParent(lease.root) is null) throw new ArgumentException("源目录不能是卷或共享根目录。");
            lease.HoldAncestors(Directory.GetParent(lease.root)!.FullName);
            lease.HoldAncestors(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            lease.Visit(lease.root, token);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void HoldAncestors(string directory)
    {
        var parent = Directory.GetParent(directory);
        if (parent is not null) HoldAncestors(parent.FullName);
        if (!ancestorPaths.Add(directory)) return;
        var handle = OpenHandle(directory, 0x80, directory: true);
        try { Inspect(handle, directory, directory: true); ancestors.Add(handle); }
        catch { handle.Dispose(); throw; }
    }

    private void Visit(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        directories.Add((directory, OpenOwned(directory, directory: true)));
        foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("导出目录中存在重解析点，未跟随：" + path);
            if ((attributes & FileAttributes.Directory) != 0) Visit(path, token);
            else
            {
                var handle = OpenOwned(path, directory: false);
                try { files.Add(new(path, new FileStream(handle, FileAccess.Read, 1024 * 1024, isAsync: false))); }
                catch { handle.Dispose(); throw; }
            }
        }
    }

    private static SafeFileHandle OpenOwned(string path, bool directory)
    {
        FileIdentity original;
        // Grant only this already-opened object, with no inheritable ACE. Windows
        // must not recursively propagate this grant into an unchecked child/link.
        using (var metadata = OpenHandle(path, 0x02000000 /* MAXIMUM_ALLOWED: SetSecurityInfo does not propagate */, directory))
        {
            original = Inspect(metadata, path, directory);
            GrantModify(metadata, directory);
        }
        // Reopen with DELETE after the ACL grant. The identity check covers the
        // gap between handles; OPEN_REPARSE_POINT prevents a replacement link.
        var owned = OpenHandle(path, 0x10000u | (directory ? 0x80u : 0x80000000u), directory);
        try
        {
            var current = Inspect(owned, path, directory);
            if (original.Volume != current.Volume || original.IndexHigh != current.IndexHigh || original.IndexLow != current.IndexLow)
                throw new IOException("追加权限后对象身份发生变化，已停止打包：" + path);
            return owned;
        }
        catch { owned.Dispose(); throw; }
    }

    internal void VerifyInventory()
    {
        var expected = directories.Select(d => d.Path).Concat(files.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
            foreach (string path in Directory.EnumerateFileSystemEntries(directory.Path))
                if (!expected.Remove(path)) throw new IOException("导出暂存目录新增了未打包对象，源目录已保留：" + path);
        expected.Remove(root);
        if (expected.Count != 0) throw new IOException("导出暂存目录清单发生变化，源目录已保留。");
    }

    internal (bool Removed, string? Error, Exception? Exception) DeleteVerifiedSource()
    {
        try
        {
            VerifyInventory();
            foreach (var file in files)
            {
                DeleteExact(file.Stream.SafeFileHandle);
                file.Stream.Dispose();
            }
            foreach (var directory in directories.AsEnumerable().Reverse())
            {
                // Native directory deletion only succeeds when empty. An item
                // added after VerifyInventory is retained, never recursively removed.
                DeleteExact(directory.Handle);
                directory.Handle.Dispose();
            }
            return (true, null, null);
        }
        catch (Exception ex) { return (false, ex.Message, ex); }
    }

    private static void DeleteExact(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static SafeFileHandle OpenHandle(string path, uint access, bool directory)
    {
        var handle = CreateFileW(path, access, 1 /* share read only */, 0, 3,
            0x00200000u /* OPEN_REPARSE_POINT */ | (directory ? 0x02000000u : 0u), 0);
        if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code, "无法取得导出对象的独占修改保护：" + path); }
        return handle;
    }

    private static FileIdentity Inspect(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("导出路径包含重解析点，未跟随：" + path);
        if (((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new IOException("导出对象类型发生变化：" + path);
        if (!directory && info.Links != 1) throw new IOException("导出源文件含多个硬链接，未追加权限或删除：" + path);
        return info;
    }

    private static unsafe void GrantModify(SafeFileHandle handle, bool directory)
    {
        uint error = GetSecurityInfo(handle, 1, 4, out _, out _, out _, out _, out nint nativeDescriptor);
        if (error != 0) throw new Win32Exception((int)error);
        byte[] descriptor;
        try
        {
            descriptor = new byte[GetSecurityDescriptorLength(nativeDescriptor)];
            Marshal.Copy(nativeDescriptor, descriptor, 0, descriptor.Length);
        }
        finally { LocalFree(nativeDescriptor); }
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User ?? throw new InvalidOperationException("无法读取当前用户身份。");
        if (security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(rule => user.Equals(rule.IdentityReference) && rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)) return;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, AccessControlType.Allow));
        descriptor = security.GetSecurityDescriptorBinaryForm();
        fixed (byte* pinned = descriptor)
        {
            if (!GetSecurityDescriptorDacl((nint)pinned, out bool present, out nint dacl, out _) || !present || dacl == 0)
                throw new IOException("无法取得完整访问控制列表，未修改权限。");
            error = SetSecurityInfo(handle, 1 /* SE_FILE_OBJECT */, 4, 0, 0, dacl, 0);
            if (error != 0) throw new Win32Exception((int)error);
        }
    }

    public void Dispose()
    {
        foreach (var file in files) file.Stream.Dispose();
        foreach (var directory in directories.AsEnumerable().Reverse()) directory.Handle.Dispose();
        foreach (var handle in ancestors.AsEnumerable().Reverse()) handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, nint security, uint disposition, uint attributes, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte info, uint size);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint requested, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(nint descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint requested, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
