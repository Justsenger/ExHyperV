using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

// Write, validate and rename the same open file. A name-based reopen between
// validation and commit could substitute a different archive before source deletion.
internal sealed class ExportArchiveLease : IDisposable
{
    internal FileStream Stream { get; }
    internal string Path { get; }
    internal bool Committed { get; private set; }

    internal ExportArchiveLease(string path)
    {
        Path = path;
        var handle = CreateFileW(path, 0xC0010000 /* read, write, DELETE */, 1, 0, 1 /* CREATE_NEW */, 0x00200000, 0);
        if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        try { Stream = new FileStream(handle, FileAccess.ReadWrite, 1024 * 1024, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    internal void Commit(string destination)
    {
        Stream.Flush(flushToDisk: true);
        byte[] name = Encoding.Unicode.GetBytes(System.IO.Path.GetFullPath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        int size = checked(nameOffset + name.Length + 2); // Include the WCHAR terminator for Win32 path conversion.
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            // ReplaceIfExists=false, RootDirectory=null. Commit never overwrites.
            if (!SetFileInformationByHandle(Stream.SafeFileHandle, 3 /* FileRenameInfo */, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Committed = true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal string? TryCleanup()
    {
        if (Committed) return null;
        try
        {
            byte delete = 1;
            if (!SetFileDisposition(Stream.SafeFileHandle, 4, ref delete, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Stream.Dispose();
            return null;
        }
        catch (Exception error) { return error.Message; }
    }

    public void Dispose() => Stream.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, nint security, uint disposition, uint attributes, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, nint info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileDisposition(SafeFileHandle handle, int kind, ref byte info, uint size);
}
