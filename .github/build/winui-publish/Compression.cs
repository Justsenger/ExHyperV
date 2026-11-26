using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;

internal static class Compression
{
    internal static void Resource(string kind, string input, string destination)
    {
        if (kind == "json")
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(input));
            // The resource is JSON, never HTML. Encode Chinese text directly as UTF-8.
            Write(JsonSerializer.SerializeToUtf8Bytes(json.RootElement,
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), destination);
            return;
        }
        if (kind != "zip") throw new ArgumentException("Unknown resource format.", nameof(kind));
        // Rebuild an uncompressed ZIP so the Brotli window can span native DLLs.
        using var original = ZipFile.OpenRead(input);
        using var stored = new MemoryStream();
        using (var archive = new ZipArchive(stored, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var entry in original.Entries.OrderBy(e => NativeFamily(e.Name)).ThenByDescending(e => e.Length)
            .ThenBy(e => e.FullName, StringComparer.Ordinal))
        {
            var copy = archive.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
            copy.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var source = entry.Open();
            using var target = copy.Open();
            source.CopyTo(target);
        }
        Write(stored.GetBuffer().AsSpan(0, checked((int)stored.Length)), destination);
    }

    private static int NativeFamily(string name) => name switch
    {
        _ when name.StartsWith("libcrypto", StringComparison.Ordinal) => 1,
        _ when name.StartsWith("libssl", StringComparison.Ordinal) => 2,
        _ when name.StartsWith("libwinpr", StringComparison.Ordinal) => 3,
        _ when name.StartsWith("libfreerdp", StringComparison.Ordinal) => 4,
        _ => 0
    };

    internal static void Write(ReadOnlySpan<byte> data, string destination)
    {
        var encoded = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, encoded, out var length, quality: 11, window: 24))
            throw new InvalidDataException("Brotli compression failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = File.Create(temp)) output.Write(encoded.AsSpan(0, length));
            File.Move(temp, destination, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
