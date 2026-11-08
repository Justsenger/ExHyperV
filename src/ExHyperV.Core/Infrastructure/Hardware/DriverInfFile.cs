using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

/// <summary>通过 Windows SetupAPI 只读解析驱动 INF；不执行导入或安装。</summary>
internal sealed class DriverInfFile : IDisposable
{
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorSectionNotFound = unchecked((int)0xe0000101);
    private const int ErrorLineNotFound = unchecked((int)0xe0000102);
    private readonly string _path;
    private readonly SafeInfHandle _handle;

    internal DriverInfFile(FileInfo file)
    {
        _path = file.FullName;
        _handle = Native.SetupOpenInfFileW(_path, null, 2 /* INF_STYLE_WIN4 */, out uint errorLine);
        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw ReadError("SetupOpenInfFileW", error, errorLine);
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>读取指定节的第一条指令；缺少节或键返回 null，解析错误保留原生诊断。</summary>
    internal string[]? ReadFields(string section, string key)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (Native.SetupFindFirstLineW(_handle, section, key, out InfContext context))
            return ReadFields(ref context);

        int error = Marshal.GetLastPInvokeError();
        if (error is ErrorSectionNotFound or ErrorLineNotFound) return null;
        throw ReadError("SetupFindFirstLineW", error);
    }

    /// <summary>枚举所有节中的同名指令，交由 SetupAPI 处理续行、引号和字符串引用。</summary>
    internal IReadOnlyList<string[]> ReadAllFields(string key)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var lines = new List<string[]>();
        for (uint index = 0; ; index++)
        {
            string? section = ReadSectionName(index);
            if (section == null) break;

            if (!Native.SetupFindFirstLineW(_handle, section, key, out InfContext context))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorLineNotFound) continue;
                throw ReadError("SetupFindFirstLineW", error);
            }

            while (true)
            {
                lines.Add(ReadFields(ref context));
                if (!Native.SetupFindNextMatchLineW(ref context, key, out InfContext next))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is ErrorLineNotFound or ErrorNoMoreItems) break;
                    throw ReadError("SetupFindNextMatchLineW", error);
                }
                context = next;
            }
        }
        return lines;
    }

    private string? ReadSectionName(uint index)
    {
        bool success = Native.SetupEnumInfSectionsW(_handle, index, null, 0, out uint needed);
        int error = Marshal.GetLastPInvokeError();
        if (!success && error == ErrorNoMoreItems) return null;
        if (!success && error != ErrorInsufficientBuffer)
            throw ReadError("SetupEnumInfSectionsW", error);

        var buffer = new StringBuilder(checked((int)needed));
        if (!Native.SetupEnumInfSectionsW(_handle, index, buffer, needed, out _))
            throw ReadError("SetupEnumInfSectionsW", Marshal.GetLastPInvokeError());
        return buffer.ToString();
    }

    private string[] ReadFields(ref InfContext context)
    {
        // INFCONTEXT 本身不持有句柄；覆盖全部字段读取期间保持所属 HINF 存活。
        try
        {
            uint count = Native.SetupGetFieldCount(ref context);
            var fields = new string[checked((int)count)];
            for (uint index = 1; index <= count; index++)
            {
                if (!Native.SetupGetStringFieldW(ref context, index, null, 0, out uint needed))
                    throw ReadError("SetupGetStringFieldW", Marshal.GetLastPInvokeError());

                var buffer = new StringBuilder(checked((int)needed));
                if (!Native.SetupGetStringFieldW(ref context, index, buffer, needed, out _))
                    throw ReadError("SetupGetStringFieldW", Marshal.GetLastPInvokeError());
                fields[index - 1] = buffer.ToString();
            }
            return fields;
        }
        finally { GC.KeepAlive(_handle); }
    }

    private InvalidOperationException ReadError(string operation, int error, uint line = 0) =>
        new($"无法读取驱动 INF：{_path}；{operation} 返回 0x{error:X8}。" +
            (line == 0 ? string.Empty : $" 错误行：{line}。"), new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct InfContext
    {
        public nint Inf;
        public nint CurrentInf;
        public uint Section;
        public uint Line;
    }

    private sealed class SafeInfHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeInfHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle()
        {
            Native.SetupCloseInfFile(handle);
            return true;
        }
    }

    private static class Native
    {
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeInfHandle SetupOpenInfFileW(string path, string? infClass, uint style, out uint errorLine);

        [DllImport("setupapi.dll", ExactSpelling = true)]
        internal static extern void SetupCloseInfFile(nint inf);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupEnumInfSectionsW(SafeInfHandle inf, uint index, StringBuilder? buffer, uint size, out uint needed);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupFindFirstLineW(SafeInfHandle inf, string section, string key, out InfContext context);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupFindNextMatchLineW(ref InfContext context, string key, out InfContext next);

        [DllImport("setupapi.dll", ExactSpelling = true)]
        internal static extern uint SetupGetFieldCount(ref InfContext context);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupGetStringFieldW(ref InfContext context, uint index, StringBuilder? buffer, uint size, out uint needed);
    }
}
