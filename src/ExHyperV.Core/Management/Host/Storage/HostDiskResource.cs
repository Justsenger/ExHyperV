using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

/// <summary>宿主存储操作共享的资源身份；映像先于它对应的磁盘编号取锁。</summary>
internal static class HostDiskResource
{
    internal static string Disk(int diskNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskNumber);
        return "1:disk-number:" + diskNumber.ToString(CultureInfo.InvariantCulture);
    }

    internal static string Image(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        // 元数据查询不读写文件内容；共享已有读、写和删除访问。
        // 卷号与 FileId128 使大小写、链接和重命名别名仍指向同一个协调资源。
        // https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_id_info
        using SafeFileHandle file = CreateFileW(Path.GetFullPath(imagePath), 0, 7, 0, 3, 0, 0);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取映像文件身份，未执行存储操作。");
        return Image(file);
    }

    internal static SafeFileHandle PinImage(string path)
    {
        VmPurgeFileSafety.EnsureNoReparse(path);
        var file = CreateFileW(Path.GetFullPath(path), 0, 3, 0, 3, 0x00200000, 0);
        if (!file.IsInvalid) return file;
        int error = Marshal.GetLastWin32Error();
        file.Dispose();
        throw new Win32Exception(error, path);
    }

    internal static SafeFileHandle PinDirectory(string path)
    {
        VmPurgeFileSafety.EnsureNoReparse(path);
        var handle = CreateFileW(path, 0x10080 /* DELETE | READ_ATTRIBUTES */, 1, 0, 3,
            0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, 0);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, 8))
                throw new Win32Exception(Marshal.GetLastWin32Error(), path);
            if ((tag.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("不是可核实的常规目录：" + path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static string Image(SafeFileHandle file)
    {
        if (!GetFileInformationByHandleEx(file, 18 /* FileIdInfo */, out FileIdentity identity, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取映像文件身份，未执行存储操作。");
        if (identity.IdLow == 0 && identity.IdHigh == 0)
            throw new NotSupportedException("文件系统没有提供可用的映像文件身份，未执行存储操作。");
        return $"0:image:{identity.VolumeSerial:X16}:{identity.IdLow:X16}:{identity.IdHigh:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { internal FileAttributes Attributes; internal uint Tag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal ulong VolumeSerial, IdLow, IdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint sharing,
        nint security, uint disposition, uint attributes, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out AttributeTag information, uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, uint informationSize);
}
