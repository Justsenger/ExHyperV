using System.Buffers.Binary;

namespace ExHyperV.Tools;

// MS-VHDX 2.2.2 and 2.6.2.6. The parent's persistent virtual disk ID is
// independent of the DataWriteGuid stored in the child's parent locator.
internal static class VirtualDiskLinkage
{
    internal static Guid ReadDataWriteGuid(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadDataWriteGuid(stream);
    }

    internal static Guid ReadDataWriteGuid(Stream stream) => ReadHeader(stream).Data;

    private static (ulong Sequence, Guid Data, Guid Log) ReadHeader(Stream stream)
    {
        (ulong Sequence, Guid Data, Guid Log)? current = null;
        var buffer = new byte[4096];
        foreach (long offset in new[] { 65536L, 131072L })
        {
            stream.Position = offset;
            stream.ReadExactly(buffer);
            if (!buffer.AsSpan(0, 4).SequenceEqual("head"u8) || !ValidChecksum(buffer)) continue;
            ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8));
            if (sequence == current?.Sequence) throw new InvalidDataException("VHDX 活动标头不唯一。");
            if (current == null || sequence > current.Value.Sequence)
                current = (sequence, new Guid(buffer.AsSpan(32, 16)), new Guid(buffer.AsSpan(48, 16)));
        }
        return current ?? throw new InvalidDataException("无法验证 VHDX 活动标头。");
    }

    // Called only after VirtDisk identified a standalone dynamic VHDX. A blank
    // boot sector alone is insufficient evidence that RAW guest data is disposable.
    internal static bool HasEmptyAllocationTable(Stream stream)
    {
        if (ReadHeader(stream).Log != Guid.Empty) return false;
        var first = new byte[65536];
        var copy = new byte[first.Length];
        stream.Position = 192 * 1024;
        stream.ReadExactly(first);
        stream.ReadExactly(copy);
        if (!first.AsSpan().SequenceEqual(copy) || !first.AsSpan(0, 4).SequenceEqual("regi"u8) || !ValidChecksum(first)) return false;
        int count = BitConverter.ToInt32(first, 8);
        if (count is < 1 or > 2047) return false;
        const int mib = 1 << 20;
        long start = 0, length = 0;
        var bat = new Guid("2dc27766-f623-4200-9d64-115e9bfd4a08");
        for (int index = 0; index < count; index++)
        {
            int entry = 16 + index * 32;
            if (new Guid(first.AsSpan(entry, 16)) != bat) continue;
            if (start != 0) return false;
            start = BitConverter.ToInt64(first, entry + 16);
            length = BitConverter.ToUInt32(first, entry + 24);
            if (start < mib || start % mib != 0 || length < mib || length % mib != 0 || start > stream.Length - length) return false;
        }
        if (length == 0) return false;
        stream.Position = start;
        for (long offset = 0; offset < length; offset += copy.Length)
        {
            stream.ReadExactly(copy);
            if (copy.AsSpan().ContainsAnyExcept((byte)0)) return false;
        }
        return true;
    }

    private static bool ValidChecksum(byte[] buffer)
    {
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        buffer.AsSpan(4, 4).Clear();
        uint crc = uint.MaxValue;
        foreach (byte value in buffer)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0x82F63B78u);
        }
        return ~crc == expected;
    }
}
