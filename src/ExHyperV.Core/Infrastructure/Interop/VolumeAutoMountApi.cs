using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

/// <summary>通过 WDK mountmgr.h 定义的 IOCTL 读写宿主卷自动挂载策略。</summary>
internal static class VolumeAutoMountApi
{
    private const uint QueryAutoMount = (0x6Du << 16) | (15u << 2);
    private const uint SetAutoMount = (0x6Du << 16) | (3u << 14) | (16u << 2);

    internal static unsafe bool GetEnabled()
    {
        using var handle = Open(write: false);
        uint state = uint.MaxValue;
        if (!Native.DeviceIoControl(handle, QueryAutoMount, null, 0, &state, sizeof(uint), out uint returned, 0))
            throw NativeError("读取卷自动挂载策略");
        return DecodeState(state, returned);
    }

    internal static unsafe void SetEnabled(bool enabled)
    {
        using var handle = Open(write: true);
        uint state = enabled ? 1u : 0u; // MOUNTMGR_AUTO_MOUNT_STATE: Disabled = 0, Enabled = 1
        if (!Native.DeviceIoControl(handle, SetAutoMount, &state, sizeof(uint), null, 0, out _, 0))
            throw NativeError("修改卷自动挂载策略");
    }

    internal static bool DecodeState(uint state, uint returned)
    {
        if (returned != sizeof(uint) || state > 1)
            throw new InvalidDataException("Mount Manager 返回的自动挂载状态或数据长度无效。");
        return state == 1;
    }

    private static SafeFileHandle Open(bool write)
    {
        // QUERY 为 FILE_ANY_ACCESS；SET 要求句柄同时具有读写权限。
        var handle = Native.CreateFileW(@"\\.\MountPointManager", write ? 0xC0000000u : 0u,
            3, 0, 3, 0, 0); // FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING
        if (!handle.IsInvalid) return handle;
        var error = NativeError("打开 Mount Manager");
        handle.Dispose();
        throw error;
    }

    private static Win32Exception NativeError(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{operation}失败：{new Win32Exception(code).Message}");
    }

    private static class Native
    {
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            nint security, uint disposition, uint flags, nint template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool DeviceIoControl(SafeFileHandle handle, uint code,
            void* input, uint inputLength, void* output, uint outputLength, out uint returned, nint overlapped);
    }
}
