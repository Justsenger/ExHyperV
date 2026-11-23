using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

// Rename/delete the handle whose identity was verified, without reopening its pathname.
internal static class OwnedFileOperations
{
    internal static SafeFileHandle PinImage(string path, string expected)
    {
        VmPurgeFileSafety.EnsureNoReparse(path);
        var handle = CreateFileW(path, 0x80 /* READ_ATTRIBUTES */, 3 /* share read/write only */, 0, 3, 0x00200000, 0);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
            RequireSingleLink(handle);
            if (HostDiskResource.Image(handle) != expected) throw new IOException("Disk file identity changed: " + path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void MoveOrDelete(string source, string identity, string? destination, bool replaceExisting = false)
    {
        VmPurgeFileSafety.EnsureNoReparse(source);
        using var file = CreateFileW(source, 0x10080 /* DELETE | FILE_READ_ATTRIBUTES */, 0,
            0, 3, 0x00200000 /* OPEN_REPARSE_POINT */, 0);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), source);
        RequireSingleLink(file);
        if (HostDiskResource.Image(file) != identity) throw new IOException("File identity changed: " + source);
        byte[] info;
        int informationClass;
        if (destination == null)
        {
            informationClass = 4; // FileDispositionInfo
            info = [1];
        }
        else
        {
            VmPurgeFileSafety.EnsureNoReparse(Path.GetDirectoryName(destination)!);
            informationClass = 3; // FileRenameInfo
            byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
            int lengthOffset = IntPtr.Size * 2;
            info = new byte[lengthOffset + 4 + name.Length + 2];
            info[0] = replaceExisting ? (byte)1 : (byte)0;
            BitConverter.GetBytes(name.Length).CopyTo(info, lengthOffset);
            name.CopyTo(info, lengthOffset + 4);
        }
        if (!SetFileInformationByHandle(file, informationClass, info, (uint)info.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), source);
    }

    internal static void DeleteEmptyDirectory(SafeFileHandle directory)
    {
        if (!SetFileInformationByHandle(directory, 4, [1], 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void RequireSingleLink(SafeFileHandle file)
    {
        if (!GetFileInformationByHandleEx(file, 1 /* FileStandardInfo */, out var info, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (info.Links != 1 || info.DeletePending != 0 || info.Directory != 0)
            throw new IOException("A unique, regular file is required.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        internal long AllocationSize, EndOfFile;
        internal uint Links;
        internal byte DeletePending, Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out StandardInfo info, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint mode, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] data, uint length);
}
