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
        if (!GetFileInformationByHandleEx(file, 18 /* FileIdInfo */, out FileIdentity identity, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取映像文件身份，未执行存储操作。");
        if (identity.IdLow == 0 && identity.IdHigh == 0)
            throw new NotSupportedException("文件系统没有提供可用的映像文件身份，未执行存储操作。");
        return $"0:image:{identity.VolumeSerial:X16}:{identity.IdLow:X16}:{identity.IdHigh:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal ulong VolumeSerial, IdLow, IdHigh;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint sharing,
        nint security, uint disposition, uint attributes, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, uint informationSize);
}
