using System.Buffers.Binary;
using System.Text;

namespace ExHyperV.Services;

internal static class ConsolePayload
{
    internal const int MaxFrameBytes = 64 * 1024 * 1024 - 64;
    internal const int MaxClipboardBytes = 32 * 1024 * 1024;
    internal const int MaxFormatBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool IsDesktopSize(int width, int height) => width is >= 200 and <= 8192
        && height is >= 200 and <= 8192 && (long)width * height * 4 <= MaxFrameBytes;

    internal static bool IsFrame(byte[] data, uint width, uint height) => width is > 0 and <= 8192
        && height is > 0 and <= 8192 && (ulong)width * height * 4 <= MaxFrameBytes
        && (ulong)data.Length == (ulong)width * height * 4;

    internal static bool TryWriteClipboard(ConsoleClipboardData clipboard, out byte[] payload)
    {
        payload = [];
        if (clipboard?.Formats is not { Count: <= 3 } formats) return false;
        var entries = new List<(ConsoleClipboardFormat Format, byte[] Name)>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long total = 4;
        foreach (var format in formats)
        {
            if (format is null || !AllowedFormat(format.Id, format.Name, out string identity)
                || !identities.Add(identity) || format.Data.Length > MaxFormatBytes) return false;
            if (format.Id == 13 && (format.Data.Length < 2 || format.Data.Length % 2 != 0
                || format.Data.Span[^1] != 0 || format.Data.Span[^2] != 0)) return false;
            byte[] name = Utf8.GetBytes(format.Name ?? "");
            total += 12L + name.Length + format.Data.Length;
            if (total > MaxClipboardBytes) return false;
            entries.Add((format, name));
        }
        payload = new byte[(int)total];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)entries.Count);
        int offset = 4;
        foreach (var (format, name) in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), format.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 4), (uint)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 8), (uint)format.Data.Length);
            name.CopyTo(payload, offset + 12);
            format.Data.Span.CopyTo(payload.AsSpan(offset + 12 + name.Length));
            offset += 12 + name.Length + format.Data.Length;
        }
        return true;
    }

    internal static bool TryReadClipboard(byte[] payload, out ConsoleClipboardData clipboard)
    {
        clipboard = new(Array.Empty<ConsoleClipboardFormat>());
        if (payload.Length is < 4 or > MaxClipboardBytes) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (count > 3) return false;
        var formats = new List<ConsoleClipboardFormat>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            if (payload.Length - offset < 12) return false;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 4));
            uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 8));
            if (nameLength > 64 || dataLength > MaxFormatBytes || 12UL + nameLength + dataLength > (ulong)(payload.Length - offset)) return false;
            string name;
            try { name = Utf8.GetString(payload.AsSpan(offset + 12, (int)nameLength)); }
            catch (DecoderFallbackException) { return false; }
            if (!AllowedFormat(id, name, out string identity) || !identities.Add(identity)) return false;
            var data = payload.AsMemory(offset + 12 + (int)nameLength, (int)dataLength);
            if (id == 13 && (data.Length < 2 || data.Length % 2 != 0 || data.Span[^1] != 0 || data.Span[^2] != 0)) return false;
            formats.Add(new(id, string.IsNullOrEmpty(name) ? null : name, data));
            offset += 12 + (int)nameLength + (int)dataLength;
        }
        if (offset != payload.Length) return false;
        clipboard = new(formats.AsReadOnly());
        return true;
    }

    private static bool AllowedFormat(uint id, string? name, out string identity)
    {
        identity = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (id is 8 or 13) return string.IsNullOrEmpty(name);
        identity = "HTML Format";
        return id is >= 0xc000 and <= 0xffff && name == identity;
    }
}
