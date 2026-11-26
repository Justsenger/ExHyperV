using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using ExHyperV.Infrastructure.Interop;
using ExHyperV.Services;
using Microsoft.AspNetCore.Http;

namespace ExHyperV.Server.Hosting;

internal interface IConsoleSessionFactory
{
    Task<ConsoleVmPolicy> Inspect(Guid vm, CancellationToken token);
    IConsoleConnection Create(ConsoleConnectionOptions options, string backend);
}

internal sealed record ConsoleVmPolicy(ConsoleRights Basic, ConsoleRights Enhanced, bool ExtensionAllowed);

internal sealed class CoreConsoleSessionFactory : IConsoleSessionFactory
{
    public async Task<ConsoleVmPolicy> Inspect(Guid vm, CancellationToken token)
    {
        await using var query = new VmQueryService();
        VmQueryField[] fields = [VmIdentityFields.Generation, VmSecurityFields.Shielded, VmConsoleFields.BasicEnabled,
            VmConsoleFields.EnhancedAvailable, VmConsoleFields.EnhancedClipboard, VmConsoleFields.EnhancedAudioOutput, VmConsoleFields.EnhancedAudioInput];
        var response = await query.QueryAsync(new(fields, [vm]), token);
        var row = response.HasData ? response.Data.VirtualMachines.SingleOrDefault(r => Guid.TryParse(r.Key, out var id) && id == vm) : null;
        if (row == null) return new(0, 0, false);
        bool Enabled(VmQueryField<bool> field) => row.Get(field) is { HasValue: true, Value: true };
        var generation = row.Get(VmIdentityFields.Generation);
        bool first = generation is { HasValue: true, Value: 1 };
        if (!first && (generation is not { HasValue: true, Value: 2 }
            || row.Get(VmSecurityFields.Shielded) is not { HasValue: true, Value: false }))
            return new(0, 0, false);
        var basic = first || Enabled(VmConsoleFields.BasicEnabled) ? ConsoleRights.Watch | ConsoleRights.Control : 0;
        ConsoleRights enhanced = 0;
        if (!first && basic != 0 && Enabled(VmConsoleFields.EnhancedAvailable))
        {
            enhanced = ConsoleRights.Watch | ConsoleRights.Control | ConsoleRights.DisplayMode;
            if (Enabled(VmConsoleFields.EnhancedAudioOutput)) enhanced |= ConsoleRights.AudioOutput;
            if (Enabled(VmConsoleFields.EnhancedAudioInput)) enhanced |= ConsoleRights.AudioInput;
            if (Enabled(VmConsoleFields.EnhancedClipboard)) enhanced |= ConsoleRights.Clipboard;
        }
        return new(basic, enhanced, true);
    }

    public IConsoleConnection Create(ConsoleConnectionOptions options, string backend) => ConsoleService.CreateConnection(options, backend);
}

internal sealed class ConsoleSlots
{
    internal readonly SemaphoreSlim Available = new(16, 16);
}

internal sealed record ConsoleRequest(ConsoleConnectionOptions Options, string Backend, string Encoding)
{
    internal static ConsoleRequest Parse(string id, IQueryCollection query, bool external = false)
    {
        if (!Guid.TryParse(id, out var vmId) || vmId == Guid.Empty) throw new ArgumentException("需要有效的虚拟机 GUID。");
        string Value(string name, string fallback) => query.TryGetValue(name, out var values)
            ? values.Count == 1 ? values[0]! : throw new ArgumentException($"参数 {name} 重复。") : fallback;
        int Number(string name, int fallback) => int.Parse(Value(name, fallback.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.None, CultureInfo.InvariantCulture);
        string backend = Value("backend", ConsoleService.RdpBasicBackendId);
        string encoding = Value("frameEncoding", "png");
        int width = Number("width", 1024), height = Number("height", 768);
        int scale = Number("desktopScale", 100), timeout = Number("connectTimeoutMs", 2000);
        bool Flag(string name, bool fallback) => Value(name, fallback ? "true" : "false") switch
        {
            "true" or "True" or "1" => true,
            "false" or "False" or "0" => false,
            _ => throw new ArgumentException($"{name} 参数无效。")
        };
        bool hostCursor = Flag("hostCursor", false);
        bool clipboard = Flag("clipboard", true);
        bool audioOutput = Flag("audioOutput", true);
        bool audioInput = Flag("audioInput", true);
        bool discrete = external && !ConsoleDirectory.Builtin(backend);
        if (discrete && (query.ContainsKey("width") || query.ContainsKey("height") || query.ContainsKey("desktopScale")))
            throw new ArgumentException("扩展使用已枚举的显示模式。");
        if (!external && backend is not (ConsoleService.RdpBasicBackendId or ConsoleService.RdpEnhancedBackendId)
            || encoding is not ("png" or "bgra" or "deflate" or "h264") || !ConsolePayload.IsDesktopSize(width, height)
            || scale is < 100 or > 500 || timeout is < 1 or > 120000)
            throw new ArgumentException("控制台后端、编码、桌面尺寸、缩放或超时参数无效。");
        return new(new() { VmId = vmId, Width = width, Height = height, DesktopScale = (uint)scale,
            ConnectTimeout = TimeSpan.FromMilliseconds(timeout), HostCursor = hostCursor,
            RedirectClipboard = clipboard, RedirectAudioOutput = audioOutput, RedirectAudioInput = audioInput }, backend, encoding);
    }
}

internal static class ConsoleWire
{
    internal const int VideoHeader = 34, AudioHeader = 18, H264Header = 34;

    internal static object Cursor(ConsoleCursor? cursor) => cursor is null
        ? new { type = "cursor", visible = false }
        : new
        {
            type = "cursor",
            visible = true,
            cursor.Width,
            cursor.Height,
            cursor.HotX,
            cursor.HotY,
            png = Convert.ToBase64String(WindowsImageCodec.PngFromBgra32(cursor.Pixels, cursor.Width, cursor.Height, cursor.Width * 4))
        };

    internal static ConsolePacket Video(ConsoleFrame frame, string encoding, long sequence, int width, int height, bool forceFull = false, ConsoleFrameRegion? overrideRegion = null)
    {
        bool full = forceFull || (overrideRegion is null && (frame.BaseSequence != sequence || width != frame.Width || height != frame.Height
            || frame.HasFullDamage || frame.Damage.IsEmpty));
        var rect = overrideRegion ?? (full ? new ConsoleFrameRegion(0, 0, frame.Width, frame.Height) : frame.Damage);
        int stride = checked(rect.Width * 4);
        int rawBytes = checked(stride * rect.Height);

        var packet = new ConsolePacket(encoding == "bgra" ? VideoHeader + rawBytes : Math.Min(65536, VideoHeader + rawBytes));
        try
        {
            WriteVideoHeader(packet.Append(VideoHeader), full, encoding switch { "bgra" => 0, "png" => 1, "deflate" => 2,
                _ => throw new ArgumentException("未知画面格式。") }, frame, rect);
            if (encoding == "png") ConsolePng.Write(packet, frame, rect);
            else
            {
                using var deflate = encoding == "deflate" ? new DeflateStream(packet, CompressionLevel.Fastest, true) : null;
                Stream target = (Stream?)deflate ?? packet;
                for (int row = 0; row < rect.Height; row++)
                    target.Write(frame.Pixels.Slice(checked((rect.Y + row) * frame.Stride + rect.X * 4), stride));
            }
            return packet;
        }
        catch { packet.Dispose(); throw; }
    }

    internal static ConsolePacket Regions(ConsoleFrame frame, bool full)
    {
        IReadOnlyList<ConsoleFrameRegion> dirty = full ? [new(0, 0, frame.Width, frame.Height)] : frame.DirtyRegions;
        IReadOnlyList<ConsoleFrameMove> moves = full ? [] : frame.MoveRegions;
        if (!full && dirty.Count + (long)moves.Count > 4096) return Regions(frame, true);
        int header = checked(40 + dirty.Count * 8 + moves.Count * 12);
        long length = header + dirty.Sum(r => (long)r.Width * r.Height * 4);
        if (!full && length > 64 * 1024 * 1024) return Regions(frame, true);
        int total = checked((int)length);
        var packet = new ConsolePacket(total);
        try
        {
            var h = packet.Append(40);
            Prefix(h, 4, full ? (byte)1 : (byte)0, 0, 2);
            BinaryPrimitives.WriteInt32LittleEndian(h[6..], total);
            BinaryPrimitives.WriteInt64LittleEndian(h[10..], frame.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(h[18..], frame.BaseSequence);
            U16(h[26..], frame.Width); U16(h[28..], frame.Height);
            U16(h[30..], dirty.Count); U16(h[32..], moves.Count);
            BinaryPrimitives.WriteInt32LittleEndian(h[34..], header);
            U16(h[38..], 0);
            foreach (var r in dirty) Rect(packet.Append(8), r);
            foreach (var m in moves)
            {
                var dest = packet.Append(12);
                U16(dest, m.SourceX); U16(dest[2..], m.SourceY); Rect(dest[4..], m.Destination);
            }
            foreach (var r in dirty)
                for (int y = 0; y < r.Height; y++)
                    packet.Write(frame.Pixels.Slice((r.Y + y) * frame.Stride + r.X * 4, r.Width * 4));
            return packet;
        }
        catch { packet.Dispose(); throw; }
    }

    private static void U16(Span<byte> bytes, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
    private static void Rect(Span<byte> bytes, ConsoleFrameRegion r)
    { U16(bytes, r.X); U16(bytes[2..], r.Y); U16(bytes[4..], r.Width); U16(bytes[6..], r.Height); }

    internal static ConsolePacket H264(ReadOnlySpan<byte> annexB, long sequence, long timestampUs, int width, int height, bool isKeyFrame, bool isHardware, uint durationUs = 0)
    {
        var owner = new ConsolePacket(H264Header + annexB.Length);
        var packet = owner.Append(H264Header + annexB.Length);
        byte flags = (byte)((isKeyFrame ? 1 : 0) | (isHardware ? 2 : 0));
        Prefix(packet, 3, flags, 0, 0);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(6), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(14), timestampUs);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(22), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(24), checked((ushort)height));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(26), durationUs);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(30), 0);
        annexB.CopyTo(packet.Slice(H264Header));
        return owner;
    }

    private static void WriteVideoHeader(Span<byte> packet, bool full, byte format, ConsoleFrame frame, ConsoleFrameRegion rect)
    {
        Prefix(packet, 1, (byte)(full ? 1 : 0), format, 0);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(6), frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(14), frame.BaseSequence);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(22), checked((ushort)frame.Width));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(24), checked((ushort)frame.Height));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(26), checked((ushort)rect.X));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(28), checked((ushort)rect.Y));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(30), checked((ushort)rect.Width));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(32), checked((ushort)rect.Height));
    }

    private static void Prefix(Span<byte> bytes, byte type, byte flags, byte format, byte channels)
    {
        bytes[0] = 0x45; bytes[1] = 0x58; bytes[2] = type;
        bytes[3] = flags; bytes[4] = format; bytes[5] = channels;
    }

    private static byte SampleKind(ConsoleAudioFormat format) =>
        format.SampleFormat == ConsoleAudioSampleFormat.Signed16LittleEndian ? (byte)1 : (byte)2;

    internal static ConsolePacket Audio(ConsoleAudioPacket audio)
    {
        var format = audio.Format;
        if (!ConsolePayload.TryGetAudioFrameAlignment(format, out _) || audio.Samples.Length > ConsolePayload.MaxAudioPacketBytes)
            throw new ArgumentException("输出音频格式或大小无效。");
        var owner = new ConsolePacket(AudioHeader + audio.Samples.Length);
        var packet = owner.Append(AudioHeader + audio.Samples.Length);
        Prefix(packet, 2, 0, SampleKind(format), (byte)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(packet.Slice(6), format.SampleRate);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(10), audio.Timestamp.Ticks);
        audio.Samples.CopyTo(packet.Slice(AudioHeader));
        return owner;
    }

    internal static void AudioInput(IConsoleAudioInput? input, ReadOnlySpan<byte> packet)
    {
        if (input is not { IsAvailable: true, Format: { } format } || !ConsolePayload.TryGetAudioFrameAlignment(format, out int align)
            || packet.Length <= AudioHeader || packet.Length - AudioHeader > ConsolePayload.MaxAudioPacketBytes
            || !packet[..4].SequenceEqual("EX\u0003\0"u8) || packet[4] != SampleKind(format) || packet[5] != format.Channels
            || BinaryPrimitives.ReadInt32LittleEndian(packet[6..]) != format.SampleRate || BinaryPrimitives.ReadInt64LittleEndian(packet[10..]) < 0
            || (packet.Length - AudioHeader) % align != 0 || !input.SendAudio(format, packet[AudioHeader..]))
            throw new ArgumentException("音频输入不可用或 PCM 数据不匹配。");
    }
}
