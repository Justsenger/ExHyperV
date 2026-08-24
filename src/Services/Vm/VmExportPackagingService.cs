using System.Buffers;
using System.IO;
using System.IO.Compression;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public enum VmExportPackageMode
{
    Store,
    Compress
}

public sealed record VmExportPackageResult(
    string ArchivePath,
    bool SourceDirectoryRemoved,
    string? CleanupError);

public static class VmExportPackagingService
{
    public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(
        string sourceDirectory,
        string destinationArchivePath,
        VmExportPackageMode mode,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            string temporaryArchivePath = destinationArchivePath + ".partial";

            try
            {
                if (!Directory.Exists(sourceDirectory))
                    return ApiResponse<VmExportPackageResult>.Fail(
                        Properties.Resources.VmExport_ExportDirectoryMissing);

                if (File.Exists(destinationArchivePath) || Directory.Exists(destinationArchivePath))
                    return ApiResponse<VmExportPackageResult>.Fail(
                        Properties.Resources.VmExport_PackageExists);

                if (File.Exists(temporaryArchivePath))
                    File.Delete(temporaryArchivePath);

                string[] files = Directory.GetFiles(
                    sourceDirectory,
                    "*",
                    SearchOption.AllDirectories);
                long totalBytes = files.Sum(path => new FileInfo(path).Length);
                long completedBytes = 0;
                string sourceParent = Directory.GetParent(sourceDirectory)?.FullName
                    ?? sourceDirectory;
                CompressionLevel compressionLevel = mode == VmExportPackageMode.Store
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.SmallestSize;

                using (var archiveStream = new FileStream(
                           temporaryArchivePath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           1024 * 1024,
                           FileOptions.SequentialScan))
                using (var archive = new ZipArchive(
                           archiveStream,
                           ZipArchiveMode.Create,
                           leaveOpen: false))
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    try
                    {
                        foreach (string filePath in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string entryName = Path.GetRelativePath(sourceParent, filePath)
                                .Replace(Path.DirectorySeparatorChar, '/');
                            ZipArchiveEntry entry = archive.CreateEntry(entryName, compressionLevel);
                            entry.LastWriteTime = File.GetLastWriteTime(filePath);

                            using Stream entryStream = entry.Open();
                            using var sourceStream = new FileStream(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                1024 * 1024,
                                FileOptions.SequentialScan);

                            int bytesRead;
                            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                entryStream.Write(buffer, 0, bytesRead);
                                completedBytes += bytesRead;
                                int percentage = totalBytes == 0
                                    ? 100
                                    : (int)Math.Min(100, completedBytes * 100L / totalBytes);
                                progress?.Report(percentage);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                File.Move(temporaryArchivePath, destinationArchivePath);
                var cleanup = TryDeleteDirectory(sourceDirectory);
                progress?.Report(100);
                return ApiResponse<VmExportPackageResult>.Ok(new VmExportPackageResult(
                    destinationArchivePath,
                    cleanup.Removed,
                    cleanup.Error));
            }
            catch (OperationCanceledException ex)
            {
                TryDelete(temporaryArchivePath);
                return ApiResponse<VmExportPackageResult>.Fail(
                    ex.Message, -1, ApiErrorSource.None, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(temporaryArchivePath);
                return ApiResponse<VmExportPackageResult>.Fail(
                    ex.Message, 5, ApiErrorSource.Win32, ex);
            }
            catch (Exception ex)
            {
                TryDelete(temporaryArchivePath);
                return ApiResponse<VmExportPackageResult>.Fail(
                    ex.Message, -1, ApiErrorSource.None, ex);
            }
        }, cancellationToken);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original failure. A .partial suffix makes any residue unambiguous.
        }
    }

    private static (bool Removed, string? Error) TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
