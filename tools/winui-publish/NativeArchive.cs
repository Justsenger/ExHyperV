using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

internal static class NativeArchive
{
    internal static void Create(string directory, string archive, string rid)
    {
        var expectedMachine = rid switch
        {
            "win-arm64" => Machine.Arm64,
            "win-x64" => Machine.Amd64,
            _ => throw new ArgumentException("Unsupported desktop architecture.", nameof(rid))
        };
        foreach (var name in new[] { "ExHyperV.exe", "resources.pri", "Microsoft.WindowsAppRuntime.Bootstrap.dll" })
            if (!File.Exists(Path.Combine(directory, name))) throw new FileNotFoundException(name);
        using var contents = new MemoryStream();
        using var writer = new BinaryWriter(contents, Encoding.Unicode, leaveOpen: true);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        writer.Write(files.Length);
        int codeOffset = 0, codeLength = 0;
        foreach (var path in files)
        {
            string relative = Path.GetRelativePath(directory, path);
            byte[] name = Encoding.Unicode.GetBytes(relative);
            writer.Write(name.Length);
            writer.Write(name);
            using var file = File.OpenRead(path);
            writer.Write(checked((int)file.Length));
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
                if (pe.PEHeaders.CorHeader != null || pe.PEHeaders.CoffHeader.Machine != expectedMachine)
                    throw new InvalidDataException($"Unexpected managed or wrong-architecture image: {relative}");
                if (relative == "ExHyperV.exe")
                {
                    var code = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
                    codeOffset = checked((int)contents.Position + code.PointerToRawData);
                    codeLength = code.SizeOfRawData;
                }
                file.Position = 0;
            }
            file.CopyTo(contents);
        }
        byte[] data = contents.ToArray();
        var digest = SHA256.HashData(data);
        // Encode branch destinations as section offsets. Calls to the same method then
        // compress together. The launcher restores the exact bytes before checking the hash.
        EncodeBranches(data.AsSpan(codeOffset, codeLength), expectedMachine);
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, compressed, out int length, quality: 11, window: 24))
            throw new InvalidDataException("Portable payload compression failed.");
        using var output = new BinaryWriter(File.Create(archive));
        output.Write(data.Length);
        output.Write(codeOffset);
        output.Write(codeLength);
        output.Write(digest);
        output.Write(compressed, 0, length);
        Console.WriteLine($"{rid}: {files.Length} files, {data.Length:N0} bytes, compressed {length:N0} bytes");
    }

    private static void EncodeBranches(Span<byte> text, Machine machine)
    {
        if (machine == Machine.Arm64)
        {
            for (int i = 0; i + 4 <= text.Length; i += 4)
            {
                uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(text[i..]);
                if (instruction >> 26 == 0x25)
                    BinaryPrimitives.WriteUInt32LittleEndian(text[i..], 0x94000000u | ((instruction + (uint)(i / 4)) & 0x03ffffffu));
            }
            return;
        }
        for (int i = 0; i + 5 <= text.Length; i++)
        {
            if (text[i] is not (0xe8 or 0xe9)) continue;
            if (text[i + 4] is 0 or 255)
            {
                int relative = BinaryPrimitives.ReadInt32LittleEndian(text[(i + 1)..]);
                int address = unchecked((relative + i + 5) << 7) >> 7;
                BinaryPrimitives.WriteInt32LittleEndian(text[(i + 1)..], address);
            }
            // Skip the complete operand even when it is not encoded, so overlapping
            // opcode-like data cannot change the candidate boundaries during decoding.
            i += 4;
        }
    }
}
