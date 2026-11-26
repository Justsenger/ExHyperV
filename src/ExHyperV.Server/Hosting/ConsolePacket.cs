using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

internal sealed class ConsolePacket(int capacity) : Stream
{
    private byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
    private int length, references = 1;
    internal ReadOnlyMemory<byte> Data => buffer.AsMemory(0, length);
    internal Span<byte> Bytes => buffer.AsSpan(0, length);
    internal ConsolePacket Retain() { Interlocked.Increment(ref references); return this; }
    internal Span<byte> Append(int count)
    {
        int end = checked(length + count);
        if (end > buffer.Length)
        {
            var larger = ArrayPool<byte>.Shared.Rent(Math.Max(end, checked(buffer.Length * 2)));
            Bytes.CopyTo(larger);
            ArrayPool<byte>.Shared.Return(buffer, true);
            buffer = larger;
        }
        var added = buffer.AsSpan(length, count);
        length = end;
        return added;
    }

    public override void Write(ReadOnlySpan<byte> data) => data.CopyTo(Append(data.Length));
    public override void Write(byte[] b, int offset, int count) => Write(b.AsSpan(offset, count));
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Decrement(ref references) == 0) ArrayPool<byte>.Shared.Return(buffer, true);
    }
    public override long Length => length;
    public override long Position { get => length; set => throw new NotSupportedException(); }
    public override bool CanSeek => false;
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override int Read(byte[] b, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal static class ConsolePng
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint value = (uint)i;
        for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
        return value;
    }).ToArray();

    private static int Chunk(ConsolePacket target, ReadOnlySpan<byte> type)
    {
        int offset = (int)target.Length;
        target.Append(4).Clear();
        target.Write(type);
        return offset;
    }

    private static void End(ConsolePacket target, int offset)
    {
        var chunk = target.Bytes[(offset + 4)..];
        BinaryPrimitives.WriteInt32BigEndian(target.Bytes[offset..], chunk.Length - 4);
        uint crc = uint.MaxValue;
        foreach (byte b in chunk) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(target.Append(4), ~crc);
    }

    internal static void Write(ConsolePacket target, ConsoleFrame frame, ConsoleFrameRegion rect)
    {
        target.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        int header = Chunk(target, "IHDR"u8);
        BinaryPrimitives.WriteInt32BigEndian(target.Append(4), rect.Width);
        BinaryPrimitives.WriteInt32BigEndian(target.Append(4), rect.Height);
        target.Write([8, 6, 0, 0, 0]);
        End(target, header);
        int data = Chunk(target, "IDAT"u8);
        int size = checked(rect.Width * 4 + 1);
        var row = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            row[0] = 0;
            using var compressed = new ZLibStream(target, CompressionLevel.Fastest, true);
            for (int y = 0; y < rect.Height; y++)
            {
                var src = frame.Pixels.Slice((rect.Y + y) * frame.Stride + rect.X * 4, size - 1);
                for (int i = 0; i < src.Length; i += 4)
                {
                    row[i + 1] = src[i + 2]; row[i + 2] = src[i + 1];
                    row[i + 3] = src[i]; row[i + 4] = src[i + 3];
                }
                compressed.Write(row, 0, size);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(row, true); }
        End(target, data);
        End(target, Chunk(target, "IEND"u8));
    }
}
