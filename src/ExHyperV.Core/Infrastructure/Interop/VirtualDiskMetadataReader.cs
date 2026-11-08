using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

/// <summary>通过 VirtDisk 只读查询本机可访问的 VHD/VHDX 文件；不挂载、修复或修改磁盘。</summary>
/// <remarks>单次查询范围内复用信息缓冲区，每个文件句柄在读取后释放。实例不支持并发使用或并发释放。</remarks>
internal sealed class VirtualDiskMetadataReader : IDisposable
{
    private const int MaximumInformationSize = 1024 * 1024;
    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");
    private InformationBuffer? _buffer;
    private bool _disposed;

    internal (long Max, string Type) ReadSettings(string imagePath)
    {
        using var disk = Open(imagePath);
        uint type = ReadType(disk);
        ReadInformation(disk, 1 /* SIZE */, 32);
        long capacity = checked((long)_buffer!.Read<ulong>(8));
        return (capacity, type switch { 2 => "Fixed", 3 => "Dynamic", _ => "Differencing" });
    }

    /// <summary>仅独立盘返回 null；差分盘的父路径未解析成功时抛出错误。</summary>
    internal string? ReadParentPath(string imagePath)
    {
        using var disk = Open(imagePath);
        if (ReadType(disk) != 4) return null;
        uint used = ReadInformation(disk, 3 /* PARENT_LOCATION */, 14);
        return ReadResolvedParent(used);
    }

    private SafeFileHandle Open(string imagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!Path.IsPathFullyQualified(imagePath))
            throw new ArgumentException("虚拟磁盘路径必须为完整路径。", nameof(imagePath));

        var storage = new VirtualStorageType { VendorId = MicrosoftVendor };
        var parameters = new OpenParameters { Version = 2, GetInfoOnly = 1, ReadOnly = 1 };
        // 不打开父链：父盘失效不妨碍读取子盘容量；依赖查询另行检查 ParentResolved。
        uint code = Native.OpenVirtualDisk(ref storage, imagePath, 0 /* ACCESS_NONE */,
            1 /* NO_PARENTS */, ref parameters, out IntPtr handle);
        // 失败时 handle 未定义，不得关闭；函数直接返回 Win32 错误码，不使用 GetLastError。
        if (code != 0) throw NativeError("OpenVirtualDisk", code);
        var disk = new SafeFileHandle(handle, ownsHandle: true);
        if (disk.IsInvalid)
        {
            disk.Dispose();
            throw new InvalidDataException("虚拟磁盘查询未返回有效句柄。");
        }
        return disk;
    }

    private uint ReadType(SafeFileHandle disk)
    {
        ReadInformation(disk, 6 /* VIRTUAL_STORAGE_TYPE */, 28);
        uint format = _buffer!.Read<uint>(8);
        if (format is not (2 or 3) || _buffer.Read<Guid>(12) != MicrosoftVendor)
            throw new NotSupportedException("仅支持查询 Microsoft VHD/VHDX 磁盘元数据。");
        ReadInformation(disk, 7 /* PROVIDER_SUBTYPE */, 12);
        uint type = _buffer.Read<uint>(8);
        if (type is not (2 or 3 or 4))
            throw new NotSupportedException("未知虚拟磁盘类型，无法确认磁盘元数据。");
        return type;
    }

    private uint ReadInformation(SafeFileHandle disk, uint version, uint minimumSize)
    {
        _buffer ??= new InformationBuffer(32);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            _buffer.Clear();
            _buffer.Write(0, version);
            uint size = checked((uint)_buffer.ByteLength);
            uint code = Native.GetVirtualDiskInformation(disk, ref size, _buffer, out uint used);
            if (code is 122 or 234 /* INSUFFICIENT_BUFFER / MORE_DATA */ &&
                size > _buffer.ByteLength && size <= MaximumInformationSize)
            {
                var larger = new InformationBuffer(checked((int)size));
                _buffer.Dispose();
                _buffer = larger;
                continue;
            }
            if (code != 0) throw NativeError("GetVirtualDiskInformation", code);
            if (used < minimumSize || used > _buffer.ByteLength)
                throw new InvalidDataException("虚拟磁盘信息长度无效，不能确认查询结果。");
            return used;
        }
        throw new InvalidDataException("虚拟磁盘信息长度持续变化，无法完成查询。");
    }

    private string ReadResolvedParent(uint used)
    {
        // FALSE 后面是 MULTI_SZ 搜索清单，不能取第一项当作已确认父盘，更不能视为独立盘。
        if (_buffer!.Read<uint>(8) == 0)
            throw new InvalidDataException("无法确认差分磁盘的父盘，父盘可能缺失或身份不匹配。");
        if ((used - 12) % sizeof(char) != 0)
            throw new InvalidDataException("父盘路径的字符缓冲区长度无效。");
        var characters = new char[checked((int)(used - 12) / sizeof(char))];
        _buffer.ReadArray(12, characters, 0, characters.Length);
        int end = Array.IndexOf(characters, '\0');
        if (end <= 0 || characters.AsSpan(end + 1).ContainsAnyExcept('\0'))
            throw new InvalidDataException("父盘查询未返回唯一且完整的路径。");
        string parent = new(characters, 0, end);
        if (!Path.IsPathFullyQualified(parent))
            throw new InvalidDataException("已解析的父盘路径不是完整路径。");
        return Path.GetFullPath(parent);
    }

    private static Win32Exception NativeError(string operation, uint code) =>
        new(unchecked((int)code), $"{operation} 查询虚拟磁盘失败（0x{code:X8}）：{new Win32Exception(unchecked((int)code)).Message}");

    public void Dispose()
    {
        _disposed = true;
        _buffer?.Dispose();
        _buffer = null;
    }

    private sealed class InformationBuffer : SafeBuffer
    {
        internal InformationBuffer(int size) : base(ownsHandle: true)
        {
            SetHandle(Marshal.AllocHGlobal(size));
            Initialize(checked((ulong)size));
        }

        internal unsafe void Clear()
        {
            byte* pointer = null;
            AcquirePointer(ref pointer);
            try { new Span<byte>(pointer, checked((int)ByteLength)).Clear(); }
            finally { ReleasePointer(); }
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        internal uint DeviceId;
        internal Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenParameters
    {
        internal uint Version;
        internal int GetInfoOnly;
        internal int ReadOnly;
        internal Guid ResiliencyGuid;
    }

    private static class Native
    {
        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint OpenVirtualDisk(ref VirtualStorageType storageType, string path,
            uint accessMask, uint flags, ref OpenParameters parameters, out IntPtr handle);

        [DllImport("virtdisk.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint informationSize,
            InformationBuffer information, out uint sizeUsed);
    }
}
