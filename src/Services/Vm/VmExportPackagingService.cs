using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
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

public enum VmExportPackageStage
{
    CreatingArchive,
    ValidatingArchive
}

public readonly record struct VmExportPackageProgress(
    VmExportPackageStage Stage,
    int Percentage);

public static class VmExportPackagingService
{
    private sealed record PackagedFileInfo(
        string EntryName,
        long Length,
        byte[] Sha256);

    public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(
        string sourceDirectory,
        string destinationArchivePath,
        VmExportPackageMode mode,
        IProgress<VmExportPackageProgress>? progress = null,
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
                        string.Format(
                            Properties.Resources.VmExport_PackageExists,
                            Path.GetFileName(destinationArchivePath)));

                if (File.Exists(temporaryArchivePath))
                    File.Delete(temporaryArchivePath);

                VmExportAccessService.EnsureCurrentUserCanModifyTree(sourceDirectory);

                string[] files = Directory.GetFiles(
                        sourceDirectory,
                        "*",
                        SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                long totalBytes = files.Sum(path => new FileInfo(path).Length);
                long completedBytes = 0;
                int lastArchiveProgress = -1;
                var packagedFiles = new List<PackagedFileInfo>(files.Length);
                CompressionLevel compressionLevel = mode == VmExportPackageMode.Store
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.SmallestSize;
                ReportStageProgress(
                    progress,
                    VmExportPackageStage.CreatingArchive,
                    0,
                    ref lastArchiveProgress);

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

                            string entryName = Path.GetRelativePath(sourceDirectory, filePath)
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
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                            long actualLength = 0;
                            int bytesRead;
                            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                entryStream.Write(buffer, 0, bytesRead);
                                hash.AppendData(buffer, 0, bytesRead);
                                actualLength += bytesRead;
                                completedBytes += bytesRead;
                                ReportStageProgress(
                                    progress,
                                    VmExportPackageStage.CreatingArchive,
                                    CalculatePercentage(completedBytes, totalBytes),
                                    ref lastArchiveProgress);
                            }

                            packagedFiles.Add(new PackagedFileInfo(
                                entryName,
                                actualLength,
                                hash.GetHashAndReset()));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                ReportStageProgress(
                    progress,
                    VmExportPackageStage.CreatingArchive,
                    100,
                    ref lastArchiveProgress);
                ValidateArchive(
                    temporaryArchivePath,
                    packagedFiles,
                    progress,
                    cancellationToken);
                File.Move(temporaryArchivePath, destinationArchivePath);
                var cleanup = TryDeleteDirectory(sourceDirectory);
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

    private static void ValidateArchive(
        string archivePath,
        IReadOnlyCollection<PackagedFileInfo> expectedFiles,
        IProgress<VmExportPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var expectedByName = expectedFiles.ToDictionary(
                file => file.EntryName,
                StringComparer.Ordinal);
            long totalBytes = expectedFiles.Sum(file => file.Length);
            long validatedBytes = 0;
            int lastValidationProgress = -1;
            ReportStageProgress(
                progress,
                VmExportPackageStage.ValidatingArchive,
                0,
                ref lastValidationProgress);

            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count != expectedByName.Count)
                throw new InvalidDataException();

            var validatedNames = new HashSet<string>(StringComparer.Ordinal);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!expectedByName.TryGetValue(entry.FullName, out PackagedFileInfo? expected)
                        || !validatedNames.Add(entry.FullName)
                        || entry.Length != expected.Length)
                    {
                        throw new InvalidDataException();
                    }

                    using Stream stream = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long actualLength = 0;
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        hash.AppendData(buffer, 0, bytesRead);
                        actualLength += bytesRead;
                        validatedBytes += bytesRead;
                        ReportStageProgress(
                            progress,
                            VmExportPackageStage.ValidatingArchive,
                            CalculatePercentage(validatedBytes, totalBytes),
                            ref lastValidationProgress);
                    }

                    byte[] actualHash = hash.GetHashAndReset();
                    if (actualLength != expected.Length
                        || !CryptographicOperations.FixedTimeEquals(actualHash, expected.Sha256))
                    {
                        throw new InvalidDataException();
                    }
                }

                if (validatedNames.Count != expectedByName.Count)
                    throw new InvalidDataException();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            ReportStageProgress(
                progress,
                VmExportPackageStage.ValidatingArchive,
                100,
                ref lastValidationProgress);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                Properties.Resources.VmExport_PackageValidationFailed,
                ex);
        }
    }

    private static int CalculatePercentage(long completedBytes, long totalBytes)
    {
        if (totalBytes <= 0)
            return 100;

        return (int)Math.Min(100, completedBytes * 100d / totalBytes);
    }

    private static void ReportStageProgress(
        IProgress<VmExportPackageProgress>? progress,
        VmExportPackageStage stage,
        int percentage,
        ref int lastPercentage)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        if (percentage == lastPercentage)
            return;

        lastPercentage = percentage;
        progress?.Report(new VmExportPackageProgress(stage, percentage));
    }

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
