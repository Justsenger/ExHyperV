using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExHyperV.Services;

internal sealed class GuestVolumeBoundaryException(string message) : IOException(message)
{
    internal static bool IsBoundaryFailure(Exception error) => error is GuestVolumeBoundaryException
        || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(IsBoundaryFailure);
}

// Paths used after mounting are relative to a specific volume GUID. Hold each
// directory open without write/delete sharing until the operation below it ends.
internal sealed class GuestVolumeFiles : IDisposable
{
    private readonly SafeFileHandle rootHandle;
    private readonly uint volumeSerial;
    internal string Root { get; }

    internal GuestVolumeFiles(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        rootHandle = Open(Root, 0x80, 1, 3, directory: true);
        try { volumeSerial = Inspect(rootHandle, directory: true).Volume; }
        catch { rootHandle.Dispose(); throw; }
    }

    internal static string PartitionVolume(IEnumerable<string> accessPaths, char assignedLetter)
    {
        var paths = accessPaths.ToArray();
        if (!paths.Contains($"{assignedLetter}:\\", StringComparer.OrdinalIgnoreCase))
            throw new GuestVolumeBoundaryException("临时盘符与目标分区不一致。");
        var roots = paths.Where(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(@"}\", StringComparison.Ordinal) && Guid.TryParseExact(p[11..^2], "D", out _)).ToArray();
        return roots.Length == 1 ? roots[0] : throw new GuestVolumeBoundaryException("无法唯一确定来宾分区的卷 GUID。");
    }

    internal IDisposable PinDirectory(string directory, bool create = false)
    {
        string relative = Path.GetRelativePath(Root, Path.GetFullPath(directory));
        var pins = new DirectoryPins();
        try
        {
            if (relative == ".") return pins;
            if (Path.IsPathRooted(relative)) throw new GuestVolumeBoundaryException("目标目录位于来宾卷之外。");
            string current = Root;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar))
            {
                if (component is "" or "." or ".." || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new GuestVolumeBoundaryException("无效的来宾目录路径。");
                current = Path.Combine(current, component);
                if (create) Directory.CreateDirectory(current);
                var handle = Open(current, 0x80, 1, 3, directory: true);
                pins.Handles.Add(handle);
                CheckVolume(handle, directory: true);
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    internal SafeFileHandle PinFile(string path, bool allowWrites = false)
    {
        CheckLeafName(path);
        using var parents = PinDirectory(Path.GetDirectoryName(path)!);
        var handle = Open(path, 0x80, allowWrites ? 3u : 1u, 3, directory: false);
        try { CheckVolume(handle, directory: false); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal void CopyFile(string source, string destination, bool readOnly)
    {
        CheckLeafName(destination);
        using var parents = PinDirectory(Path.GetDirectoryName(destination)!, create: true);
        // Clear read-only via the verified leaf handle. Never follow an existing link.
        try
        {
            using var existing = Open(destination, 0x180, 0, 3, directory: false);
            var attrs = CheckVolume(existing, directory: false).Attributes;
            if ((attrs & FileAttributes.ReadOnly) != 0) SetAttributes(existing, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { }
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        // OPEN_ALWAYS avoids truncating before checking the handle.
        using var target = Open(destination, 0x40000180, 0, 4, directory: false);
        var info = CheckVolume(target, directory: false);
        using var output = new FileStream(target, FileAccess.Write);
        output.SetLength(0);
        input.CopyTo(output);
        output.Flush();
        if (readOnly) SetAttributes(target, info.Attributes | FileAttributes.ReadOnly);
    }

    private static void CheckLeafName(string path)
    {
        string name = Path.GetFileName(path);
        if (name is "" or "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new GuestVolumeBoundaryException("无效的来宾文件路径。");
    }

    private FileInformation CheckVolume(SafeFileHandle handle, bool directory)
    {
        var info = Inspect(handle, directory);
        if (info.Volume != volumeSerial) throw new GuestVolumeBoundaryException("目标属于不同的卷。");
        return info;
    }

    private static FileInformation Inspect(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Attributes.HasFlag(FileAttributes.Directory) != directory
            || !directory && info.Links != 1) throw new GuestVolumeBoundaryException("目标具有重解析点、额外硬链接或意外的对象类型。");
        return info;
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, bool directory)
    {
        var file = CreateFileW(path, access, share, 0, disposition, 0x00200000u | (directory ? 0x02000000u : 0), 0);
        if (!file.IsInvalid) return file;
        int error = Marshal.GetLastWin32Error();
        file.Dispose();
        throw new Win32Exception(error, path);
    }

    private static void SetAttributes(SafeFileHandle handle, FileAttributes attributes)
    {
        var basic = new BasicInfo { Attributes = attributes == 0 ? FileAttributes.Normal : attributes };
        if (!SetFileInformationByHandle(handle, 0, ref basic, (uint)Marshal.SizeOf<BasicInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private sealed class DirectoryPins : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { internal long Creation, Access, Write, Change; internal FileAttributes Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref BasicInfo info, uint size);
    public void Dispose() => rootHandle.Dispose();
}
