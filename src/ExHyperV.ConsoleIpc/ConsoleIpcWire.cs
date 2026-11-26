using System.Buffers.Binary;
using System.Text;

namespace ExHyperV.ConsoleIpc;

internal enum IpcMessage : ushort
{
    Hello = 1, InspectSnapshot = 2, GetModes = 4, AllowsFallback = 5, SnapshotChanged = 6,
    OpenSession = 16, StartSession, CloseSession, SessionConnected, SessionLoginCompleted, SessionDisconnected,
    ControlChanged, SetMode, CursorShape, CursorState, ClipboardAvailability, ClipboardData, SetClipboard,
    AudioAvailability, AudioOutput, AudioInput,
    InputKey = 40, InputKeys, InputPause, InputSyncLocks, InputPointer, ReleaseAllInputs,
    CpuRingAttach = 64, CpuFrameReady, FrameConsumedAck, GpuSurfaceAttach, GpuFrameReady, RequestCpuPath,
    OpenDelivery, CloseDelivery, TransferAck,
    Error = 0xffff
}

internal sealed class IpcFailure(uint code) : IOException("EXT-IPC")
{
    internal const uint Unavailable = 0xec000001, Incompatible = 0xec000002, Denied = 0xec000003;
    internal uint Code => code;
}

internal sealed record IpcPacket(IpcMessage Type, ulong Request, ulong Session, byte[] Body);

internal static class ConsoleIpcWire
{
    internal const int HeaderSize = 32, PrefixSize = 40, Limit = 16 << 20;
    internal const uint Magic = 0x43485845;
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal static IpcMessage Reply(IpcMessage type) => (IpcMessage)((ushort)type | 0x8000);

    internal static async Task<IpcPacket> Read(Stream stream, CancellationToken token)
    {
        byte[] head = new byte[PrefixSize];
        await stream.ReadExactlyAsync(head.AsMemory(0, HeaderSize), token).ConfigureAwait(false);
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8)) != HeaderSize || total is < PrefixSize or > Limit)
            throw new IpcFailure(IpcFailure.Incompatible);
        await stream.ReadExactlyAsync(head.AsMemory(HeaderSize), token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(32)) != 0) throw new IpcFailure(IpcFailure.Incompatible);
        byte[] body = new byte[total - PrefixSize];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return new((IpcMessage)BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)),
            BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16)), BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(24)), body);
    }

    internal static byte[] Encode(IpcMessage type, ulong request, ulong session, Action<BinaryWriter>? payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write((ushort)HeaderSize);
        writer.Write((ushort)type);
        writer.Write(0);
        writer.Write(request);
        writer.Write(session);
        writer.Write(0u);
        writer.Write(0u);
        payload?.Invoke(writer);
        if (stream.Length > Limit) throw new IpcFailure(IpcFailure.Incompatible);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), bytes.Length);
        return bytes;
    }

    internal static void Text(this BinaryWriter w, string? text)
    {
        if (text == null) { w.Write(-1); return; }
        int size = Utf8.GetByteCount(text);
        if (size > 4096) throw new IpcFailure(IpcFailure.Incompatible);
        w.Write(size);
        w.Write(Utf8.GetBytes(text));
    }
}

internal sealed class IpcReader(byte[] bytes) : BinaryReader(new MemoryStream(bytes, false), ConsoleIpcWire.Utf8)
{
    internal int Count(int max)
    {
        int n = ReadInt32();
        return n >= 0 && n <= max ? n : throw new IpcFailure(IpcFailure.Incompatible);
    }
    internal bool Flag() => ReadByte() switch { 0 => false, 1 => true, _ => throw new IpcFailure(IpcFailure.Incompatible) };
    internal byte[] Bytes(int n) => n >= 0 && n <= BaseStream.Length - BaseStream.Position ? ReadBytes(n) : throw new IpcFailure(IpcFailure.Incompatible);
    internal Guid Id() => new(Bytes(16));
    internal string? Text()
    {
        int n = ReadInt32();
        return n == -1 ? null : n is >= 0 and <= 4096 ? ConsoleIpcWire.Utf8.GetString(Bytes(n)) : throw new IpcFailure(IpcFailure.Incompatible);
    }
    internal string String() => Text() ?? throw new IpcFailure(IpcFailure.Incompatible);
    internal string? Diagnostic()
    {
        string? value = Text();
        return value is { Length: > 0 and <= 32 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? value : null;
    }
    internal string? SafeReason()
    {
        string? value = Text()?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 512) return null;
        foreach (char c in value)
        {
            if (char.IsControl(c) || c is '\u200E' or '\u200F' || c is >= '\u202A' and <= '\u202E' || c is >= '\u2066' and <= '\u2069')
                return null;
        }
        return value.Replace('；', '，').Replace(';', ',').Replace('·', '/');
    }
}
