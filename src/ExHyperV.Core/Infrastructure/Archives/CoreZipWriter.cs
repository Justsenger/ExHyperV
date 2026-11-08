using System.IO.Compression;
using ExHyperV.Services;
using ICSharpCode.SharpZipLib.Zip;

namespace ExHyperV.Tools;

internal sealed class CoreZipWriter : IDisposable
{
    private readonly ZipArchive? plain;
    private readonly ZipOutputStream? encrypted;
    private readonly VmExportCompressionLevel level;
    private bool faulted;
    internal CoreZipWriter(Stream output, VmExportPackageOptions options)
    {
        level = options.CompressionLevel;
        if (options.Encryption == VmExportEncryption.None)
            plain = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        else
        {
            encrypted = new ZipOutputStream(output)
            {
                IsStreamOwner = false,
                Password = options.Password,
                UseZip64 = UseZip64.Dynamic
            };
            encrypted.SetLevel(level switch
            {
                VmExportCompressionLevel.Store => 0,
                VmExportCompressionLevel.Fastest => 1,
                VmExportCompressionLevel.Optimal => 6,
                _ => 9
            });
        }
    }

    internal void WriteEntry(string name, DateTime modified, long length, Action<Stream> write)
    {
        if (plain != null)
        {
            var entry = plain.CreateEntry(name, level switch
            {
                VmExportCompressionLevel.Store => CompressionLevel.NoCompression,
                VmExportCompressionLevel.Fastest => CompressionLevel.Fastest,
                VmExportCompressionLevel.Optimal => CompressionLevel.Optimal,
                _ => CompressionLevel.SmallestSize
            });
            entry.LastWriteTime = modified;
            using var stream = entry.Open();
            write(stream);
        }
        else
        {
            try
            {
                encrypted!.PutNextEntry(new ZipEntry(name)
                {
                    AESKeySize = 256,
                    IsUnicodeText = true,
                    DateTime = modified,
                    Size = length,
                    CompressionMethod = level == VmExportCompressionLevel.Store ? CompressionMethod.Stored : CompressionMethod.Deflated
                });
                write(encrypted);
                encrypted.CloseEntry();
            }
            catch { faulted = true; throw; }
        }
    }

    public void Dispose()
    {
        plain?.Dispose();
        try { encrypted?.Dispose(); }
        // Finishing a cancelled, partially written entry can itself report a size
        // mismatch. The caller must receive the original cancellation/write error.
        catch when (faulted) { }
        finally { if (encrypted != null) encrypted.Password = null; }
    }
}
