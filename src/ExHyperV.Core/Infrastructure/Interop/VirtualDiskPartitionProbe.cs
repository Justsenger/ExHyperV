using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ExHyperV.Models;
using ExHyperV.Services;
using ICSharpCode.SharpZipLib.Checksum;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Tools;

internal sealed record VirtualDiskLayout(VmPartitionLayoutMode Mode, long? LastEnd, string Message);

// Non-permanent attach. Closing this specific handle also releases its attachment,
// including on exceptions. No persistent lease or host drive letter is requested.
internal static class VirtualDiskPartitionProbe
{
    internal static VirtualDiskLayout Read(string path, long capacity)
    {
        HostResourceLock.AssertHeld(HostDiskResource.Image(path));
        var storage = new StorageType { Vendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B") };
        var open = new OpenParameters { Version = 1, Depth = 0 };
        Check(OpenVirtualDisk(ref storage, path, 0xD0000 /* ATTACH_RO | DETACH | GET_INFO */, 0, ref open, out var raw));
        using var image = new SafeFileHandle(raw, true);
        var loaded = new LoadedInfo { Version = 13 };
        uint infoSize = (uint)Marshal.SizeOf<LoadedInfo>();
        Check(GetVirtualDiskInformation(image, ref infoSize, ref loaded, out uint used));
        if (used < 12 || loaded.IsLoaded != 0)
            return new(VmPartitionLayoutMode.Unavailable, null, "磁盘已在使用或挂载状态无法确认，请重新检测。");
        var attach = new AttachParameters { Version = 1 };
        Check(AttachVirtualDisk(image, 0, 3 /* READ_ONLY | NO_DRIVE_LETTER */, 0, ref attach, 0));
        try
        {
            uint length = 1024;
            var physical = new StringBuilder((int)length / 2);
            Check(GetVirtualDiskPhysicalPath(image, ref length, physical));
            using var stream = new FileStream(physical.ToString(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] data = ReadLayout(stream.SafeFileHandle);
            return DecodeLayout(data, capacity, offset =>
            {
                stream.Position = offset;
                var sector = new byte[(int)Math.Min(4096, capacity - offset)];
                stream.ReadExactly(sector);
                return sector;
            });
        }
        finally
        {
            uint detach = DetachVirtualDisk(image, 0, 0);
            if (detach != 0) throw new CoreOperationException(ApiResponse.Fail(
                "临时只读挂载的显式卸载失败，请重新检查宿主挂载状态。", (int)detach,
                ApiErrorSource.Win32, kind: ApiErrorKind.CleanupRequired));
        }
    }

    private static byte[] ReadLayout(SafeFileHandle file)
    {
        for (int size = 4096; size <= 4 * 1024 * 1024; size *= 2)
        {
            byte[] buffer = new byte[size];
            if (DeviceIoControl(file, 0x70050 /* IOCTL_DISK_GET_DRIVE_LAYOUT_EX */, 0, 0,
                buffer, size, out int used, 0)) return buffer[..used];
            int error = Marshal.GetLastWin32Error();
            if (error is not (122 or 234)) throw new Win32Exception(error);
        }
        throw new InvalidDataException("分区表过大，无法确认完整边界。");
    }

    internal static VirtualDiskLayout DecodeLayout(byte[] data, long capacity, Func<long, byte[]> readSector)
    {
        const int headerSize = 48, entrySize = 144;
        VirtualDiskLayout Invalid() => new(VmPartitionLayoutMode.Corrupt, null, "分区表无法完整验证，已禁止收缩。");
        if (data.Length < headerSize || capacity <= 0) return Invalid();
        int style = BitConverter.ToInt32(data), count = BitConverter.ToInt32(data, 4);
        if (style == 2) return Uninitialized(readSector(0));
        if (style is not (0 or 1) || count < 0 || count > (data.Length - headerSize) / entrySize) return Invalid();
        var mbr = readSector(0);
        if (mbr.Length < 512 || mbr[510] != 0x55 || mbr[511] != 0xAA)
            return style == 0 ? Uninitialized(mbr) : Invalid();
        var primaryTypes = Enumerable.Range(0, 4).Select(i => mbr[446 + 16 * i + 4]).ToArray();
        if (primaryTypes.Contains((byte)0xee) != (style == 1)) return Invalid();
        long last = 0;
        bool conservative = false;
        var extents = new List<(long Start, long End)>();
        var mbrRanges = new HashSet<(byte Type, long Start, long Length)>();
        for (int i = 0; i < count; i++)
        {
            var entry = data.AsSpan(headerSize + i * entrySize, entrySize);
            if (BinaryPrimitives.ReadInt32LittleEndian(entry) != style) return Invalid();
            byte mbrType = entry[32];
            Guid gptType = new(entry.Slice(32, 16));
            if (style == 0 ? mbrType == 0 : gptType == Guid.Empty) continue;
            long start = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            long length = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (start <= 0 || length <= 0 || start > capacity - length) return Invalid();
            last = Math.Max(last, start + length);
            if (style == 0) mbrRanges.Add((mbrType, start, length));
            // Extended MBR containers enclose their logical partitions.
            if (style == 0 && mbrType is 0x05 or 0x0F or 0x85) continue;
            extents.Add((start, start + length));
            if (style == 1 && gptType == new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")) continue; // MSR
            var sector = readSector(start);
            if (sector.Length < 90) return Invalid();
            conservative |= !sector.AsSpan(3, 8).SequenceEqual("NTFS    "u8)
                && !sector.AsSpan(82, 5).SequenceEqual("FAT32"u8)
                && !sector.AsSpan(54, 3).SequenceEqual("FAT"u8);
        }
        long previousEnd = 0;
        foreach (var extent in extents.OrderBy(x => x.Start))
        {
            if (extent.Start < previousEnd) return Invalid();
            previousEnd = extent.End;
        }
        if (style == 1 && !VerifyGpt(mbr, capacity, readSector, extents.Count, last)) return Invalid();
        if (style == 0 && primaryTypes.All(type => type == 0) != (last == 0)) return Invalid();
        if (style == 0 && !new[] { 512L, 4096L }.Any(sector =>
            Enumerable.Range(0, 4).Where(i => primaryTypes[i] != 0).All(i =>
                mbrRanges.Contains((primaryTypes[i], BitConverter.ToUInt32(mbr, 454 + i * 16) * sector,
                    BitConverter.ToUInt32(mbr, 458 + i * 16) * sector))))) return Invalid();
        if (last == 0) return new(VmPartitionLayoutMode.Empty, 0, "磁盘没有已分配分区。");
        return conservative
            ? new(VmPartitionLayoutMode.Conservative, last, "可能包含 BitLocker、Linux 或未知文件系统，仅按完整分区外边界计算。")
            : new(VmPartitionLayoutMode.Regular, last, "已读取完整分区边界。");
    }

    private static VirtualDiskLayout Uninitialized(byte[] sector) =>
        sector.Length >= 512 && !sector.AsSpan().ContainsAnyExcept((byte)0)
            ? new(VmPartitionLayoutMode.Empty, null, "未发现已初始化的分区表，无法证明原始数据区为空，已禁止收缩。")
            : new(VmPartitionLayoutMode.Corrupt, null, "无法识别分区表，已禁止收缩。");

    // A provider may recover a degraded GPT from one copy. Before permitting
    // shrink, require both on-disk headers and entry arrays to agree with Windows.
    private static bool VerifyGpt(byte[] firstBlock, long capacity, Func<long, byte[]> read,
        int partitionCount, long lastEnd)
    {
        int sector = firstBlock.Length >= 1024 && firstBlock.AsSpan(512, 8).SequenceEqual("EFI PART"u8) ? 512 : 4096;
        if (capacity < 4 * sector || capacity % sector != 0) return false;
        long lastLba = capacity / sector - 1;
        byte[] primary = read(sector), backup = read(lastLba * sector);
        bool Header(byte[] h, long self, long other)
        {
            if (h.Length < sector || !h.AsSpan(0, 8).SequenceEqual("EFI PART"u8) ||
                BitConverter.ToInt64(h, 24) != self || BitConverter.ToInt64(h, 32) != other) return false;
            int size = BitConverter.ToInt32(h, 12);
            if (size < 92 || size > sector) return false;
            byte[] header = h[..size];
            Array.Clear(header, 16, 4);
            var crc = new Crc32();
            crc.Update(header);
            return crc.Value == BitConverter.ToUInt32(h, 16);
        }
        if (!Header(primary, 1, lastLba) || !Header(backup, lastLba, 1) ||
            !primary.AsSpan(40, 32).SequenceEqual(backup.AsSpan(40, 32)) ||
            !primary.AsSpan(80, 12).SequenceEqual(backup.AsSpan(80, 12))) return false;
        int count = BitConverter.ToInt32(primary, 80), stride = BitConverter.ToInt32(primary, 84);
        long bytes = (long)count * stride;
        if (count <= 0 || stride < 128 || stride % 128 != 0 || bytes > 16 * 1024 * 1024) return false;
        long firstUsable = BitConverter.ToInt64(primary, 40), lastUsable = BitConverter.ToInt64(primary, 48);
        long primaryTable = BitConverter.ToInt64(primary, 72), backupTable = BitConverter.ToInt64(backup, 72);
        long sectors = 1 + (bytes - 1) / sector;
        if (firstUsable <= 1 || lastUsable < firstUsable || lastUsable >= lastLba ||
            primaryTable < 2 || primaryTable > firstUsable - sectors ||
            backupTable <= lastUsable || backupTable > lastLba - sectors) return false;
        var table = new byte[(int)bytes];
        for (int offset = 0; offset < table.Length;)
        {
            int length = Math.Min(4096, table.Length - offset);
            byte[] front = read(primaryTable * sector + offset), back = read(backupTable * sector + offset);
            if (front.Length < length || back.Length < length || !front.AsSpan(0, length).SequenceEqual(back.AsSpan(0, length))) return false;
            front.AsSpan(0, length).CopyTo(table.AsSpan(offset));
            offset += length;
        }
        var checksum = new Crc32();
        checksum.Update(table);
        if (checksum.Value != BitConverter.ToUInt32(primary, 88)) return false;
        long max = 0;
        int allocated = 0;
        for (int offset = 0; offset < table.Length; offset += stride)
        {
            if (new Guid(table.AsSpan(offset, 16)) == Guid.Empty) continue;
            long start = BitConverter.ToInt64(table, offset + 32), end = BitConverter.ToInt64(table, offset + 40);
            if (start < firstUsable || end < start || end > lastUsable) return false;
            max = Math.Max(max, (end + 1) * sector);
            allocated++;
        }
        return allocated == partitionCount && max == lastEnd;
    }

    private static void Check(uint code) { if (code != 0) throw new Win32Exception((int)code); }
    [StructLayout(LayoutKind.Sequential)] private struct StorageType { internal uint Device; internal Guid Vendor; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenParameters { internal uint Version, Depth; }
    [StructLayout(LayoutKind.Sequential)] private struct AttachParameters { internal uint Version, Reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct LoadedInfo
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(8)] internal int IsLoaded;
    }

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern uint GetVirtualDiskInformation(SafeFileHandle disk, ref uint size, ref LoadedInfo info, out uint used);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint OpenVirtualDisk(ref StorageType type, string path, uint access, uint flags, ref OpenParameters parameters, out nint handle);
    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern uint AttachVirtualDisk(SafeFileHandle disk, nint security, uint flags, uint providerFlags, ref AttachParameters parameters, nint overlapped);
    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern uint DetachVirtualDisk(SafeFileHandle disk, uint flags, uint providerFlags);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle disk, ref uint bufferBytes, StringBuilder path);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, nint overlapped);
}
