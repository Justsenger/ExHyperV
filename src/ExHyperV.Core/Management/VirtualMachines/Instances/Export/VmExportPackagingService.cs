using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>ZIP 包的存储或压缩方式。</summary>
public enum VmExportPackageMode
{
    /// <summary>不压缩文件内容，优先降低打包 CPU 开销。</summary>
    Store,
    /// <summary>使用 ZIP 压缩存储文件内容。</summary>
    Compress
}

/// <summary>已完成归档的路径及独立的源目录清理结果。</summary>
/// <param name="ArchivePath">归档成功后的 ZIP 文件路径。</param>
/// <param name="SourceDirectoryRemoved">归档完成后是否成功移除了整个源目录。</param>
/// <param name="CleanupError">归档成功后清理源目录的错误；无清理错误时为 null。</param>
public sealed record VmExportPackageResult(
    string ArchivePath,
    bool SourceDirectoryRemoved,
    string? CleanupError)
{
    /// <summary>本次采用的源目录处理方式；Keep 且 SourceDirectoryRemoved=false 表示按要求保留，不是清理失败。</summary>
    public VmExportSourceHandling SourceHandling { get; init; } = VmExportSourceHandling.DeleteAfterValidation;
    /// <summary>源目录清理失败的结构化信息；归档仍有效。</summary>
    public ApiFailure? CleanupFailure { get; init; }
}

/// <summary>导出包正在创建或校验的阶段。</summary>
public enum VmExportPackageStage
{
    /// <summary>正在将源文件写入 ZIP。</summary>
    CreatingArchive,
    /// <summary>正在校验归档条目及内容。</summary>
    ValidatingArchive
}

/// <summary>当前打包阶段及该阶段的整数百分比。</summary>
/// <param name="Stage">报告进度或结果时所处的操作阶段。</param>
/// <param name="Percentage">当前 CreatingArchive 或 ValidatingArchive 阶段的百分比，不是两个阶段合并的总进度。</param>
public readonly record struct VmExportPackageProgress(
    VmExportPackageStage Stage,
    int Percentage);

/// <summary>将虚拟机导出目录打包为经校验的 ZIP，支持压缩、AES-256 和源目录保留选择。</summary>
public static class VmExportPackagingService
{
    private sealed record PackagedFileInfo(
        string EntryName,
        long Length,
        byte[] Sha256);

    /// <summary>将已有导出目录写成 ZIP；归档校验成功后清理源目录。</summary>
    /// <param name="sourceDirectory">仅供打包使用、成功后允许整体删除的导出暂存目录；不要传入需保留的工作目录。</param>
    /// <param name="destinationArchivePath">最终 ZIP 文件路径；必须在源目录之外，祖先目录不能包含重解析点，且不能覆盖已有归档。</param>
    /// <param name="mode">Store 不压缩，Compress 使用压缩；两种模式均会校验归档后清理源目录。</param>
    /// <param name="progress">可选进度接收器；null 时不通知。回调线程由具体 IProgress 实现决定。</param>
    /// <param name="cancellationToken">协作取消令牌；默认不请求取消。取消不撤销已完成的主机或文件修改。</param>
    /// <returns>归档成功时返回实际路径及独立的源目录清理结果；SourceDirectoryRemoved=false 不影响归档成功。调度前和执行中的取消均返回 ErrorKind=Cancelled；最终归档提交后视为完成，不因后来的取消撤销归档。</returns>
    /// <remarks>保留自动补充当前用户修改权限：仅向逐个核实身份的对象追加非继承权限，不递归传播到未知对象。源文件和目录持有句柄直到校验及清理结束，期间阻止写入和替换；重解析点和多硬链接文件会被拒绝。最终归档写入前使用 .partial 文件；已有同名暂存文件会报告冲突并保留。源目录必须是允许整体删除的导出暂存目录。取消或失败仅清理本次创建的临时归档，保留源目录；临时归档清理失败时，Exception 中的 VmExportPackageException.RetainedPaths 返回保留路径；已追加的 ACL 不撤销。归档校验逐项比较长度和 SHA-256。清理前重查清单，新增对象时保留源目录；清理只删除已归档的原对象，不递归删除后来添加的内容。</remarks>
    public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(
        string sourceDirectory,
        string destinationArchivePath,
        VmExportPackageMode mode,
        IProgress<VmExportPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Enum.IsDefined(mode)
            ? CreatePackageAsync(sourceDirectory, destinationArchivePath, new VmExportPackageOptions
            {
                CompressionLevel = mode == VmExportPackageMode.Store ? VmExportCompressionLevel.Store : VmExportCompressionLevel.SmallestSize,
                SourceHandling = VmExportSourceHandling.DeleteAfterValidation
            }, progress, cancellationToken)
            : Task.FromResult(ApiResponse<VmExportPackageResult>.Fail("归档模式无效。", kind: ApiErrorKind.InvalidArgument));

    /// <summary>按指定压缩级别、加密方式和源目录处理方式创建 ZIP。</summary>
    /// <param name="sourceDirectory">完整导出目录；选择 DeleteAfterValidation 时必须是允许成功后删除的暂存目录。</param>
    /// <param name="destinationArchivePath">最终 ZIP 路径，位于源目录之外；已有目标和同名 .partial 文件均不覆盖。</param>
    /// <param name="options">打包选项；默认选项为均衡压缩、不加密、保留源目录。参数错误在权限补充与归档写入前返回。</param>
    /// <param name="progress">创建和校验两阶段的进度；null 不通知。</param>
    /// <param name="cancellationToken">取消打包或校验；最终归档提交后不再撤销成功结果。</param>
    /// <returns>归档路径和独立的源目录处理结果；保留源目录或清理失败不会把已经校验成功的归档改为失败。</returns>
    /// <remarks>使用标准 ZIP/ZIP64；普通 ZIP 保留 .NET 压缩路径，加密 ZIP 使用 WinZip AES-256，文件名和大小不隐藏。归档始终逐项核对长度和 SHA-256，通过后才发布最终文件，再按 SourceHandling 处理源目录。取消、密码或完整性校验失败保留源目录。两种源目录模式都沿用仅向核实身份的对象补充非继承权限的规则，已增加的权限不撤销。失败的临时归档清理信息见 VmExportPackageException.RetainedPaths。</remarks>
    public static Task<ApiResponse<VmExportPackageResult>> CreatePackageAsync(
        string sourceDirectory,
        string destinationArchivePath,
        VmExportPackageOptions options,
        IProgress<VmExportPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            string temporaryArchivePath = destinationArchivePath + ".partial";
            ExportArchiveLease? output = null;
            VmExportPackageException FailureException(Exception exception)
            {
                if (output is null || output.Committed) return new(exception.Message, exception, []);
                string? cleanupError = output.TryCleanup();
                return new(exception.Message, exception, cleanupError == null ? [] : [temporaryArchivePath], cleanupError);
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(options);
                options.Validate();
                sourceDirectory = Path.GetFullPath(sourceDirectory);
                destinationArchivePath = Path.GetFullPath(destinationArchivePath);
                temporaryArchivePath = destinationArchivePath + ".partial";
                string relativeDestination = Path.GetRelativePath(sourceDirectory, destinationArchivePath);
                if (relativeDestination == "." || (!Path.IsPathRooted(relativeDestination)
                    && relativeDestination != ".." && !relativeDestination.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    return ApiResponse<VmExportPackageResult>.Fail("归档必须位于源目录之外，避免归档被自身打包或随源目录删除。", kind: ApiErrorKind.InvalidArgument);
                if (!Directory.Exists(sourceDirectory))
                    return ApiResponse<VmExportPackageResult>.Fail(
                        Properties.Resources.VmExport_ExportDirectoryMissing);

                if (File.Exists(destinationArchivePath) || Directory.Exists(destinationArchivePath))
                    return ApiResponse<VmExportPackageResult>.Fail(
                        string.Format(
                            Properties.Resources.VmExport_PackageExists,
                            Path.GetFileName(destinationArchivePath)));

                if (File.Exists(temporaryArchivePath) || Directory.Exists(temporaryArchivePath))
                    return ApiResponse<VmExportPackageResult>.Fail("同名临时归档已存在，未覆盖：" + temporaryArchivePath,
                        kind: ApiErrorKind.Conflict);

                using var source = ExportSourceLease.Open(sourceDirectory, destinationArchivePath, cancellationToken);
                var files = source.Files;
                long totalBytes = files.Sum(file => file.Stream.Length);
                long completedBytes = 0;
                int lastArchiveProgress = -1;
                var packagedFiles = new List<PackagedFileInfo>(files.Count);
                ReportStageProgress(
                    progress,
                    VmExportPackageStage.CreatingArchive,
                    0,
                    ref lastArchiveProgress);

                output = new ExportArchiveLease(temporaryArchivePath);
                using (var archive = new CoreZipWriter(output.Stream, options))
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    try
                    {
                        foreach (var sourceFile in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string filePath = sourceFile.Path;
                            string entryName = Path.GetRelativePath(sourceDirectory, filePath)
                                .Replace(Path.DirectorySeparatorChar, '/');
                            Stream sourceStream = sourceFile.Stream;
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                            long actualLength = 0;
                            archive.WriteEntry(entryName, File.GetLastWriteTime(filePath), sourceStream.Length, entryStream =>
                            {
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
                            });

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
                    output.Stream,
                    options.Password,
                    packagedFiles,
                    progress,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                output.Commit(destinationArchivePath);
                if (options.SourceHandling == VmExportSourceHandling.Keep)
                    return ApiResponse<VmExportPackageResult>.Ok(new VmExportPackageResult(
                        destinationArchivePath, false, null) { SourceHandling = VmExportSourceHandling.Keep });
                var cleanup = source.DeleteVerifiedSource();
                return ApiResponse<VmExportPackageResult>.Ok(new VmExportPackageResult(
                    destinationArchivePath,
                    cleanup.Removed,
                    cleanup.Error) { CleanupFailure = cleanup.Exception is null ? null : ApiFailure.From(cleanup.Exception) with { Kind = ApiErrorKind.CleanupRequired } });
            }
            catch (OperationCanceledException ex)
            {
                var failure = FailureException(ex);
                return ApiResponse<VmExportPackageResult>.Fail(
                    failure.Message, -1, ApiErrorSource.None, failure, ApiErrorKind.Cancelled);
            }
            catch (UnauthorizedAccessException ex)
            {
                var failure = FailureException(ex);
                return ApiResponse<VmExportPackageResult>.Fail(
                    failure.Message, 5, ApiErrorSource.Win32, failure, ApiErrorKind.AccessDenied);
            }
            catch (Exception ex)
            {
                var failure = FailureException(ex);
                return ApiResponse<VmExportPackageResult>.Fail(
                    failure.Message, failure.Failure.Code, failure.Failure.Source, failure, failure.Failure.Kind);
            }
            finally { output?.Dispose(); }
        });

    private static void ValidateArchive(
        FileStream archiveStream,
        string? password,
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

            archiveStream.Flush(flushToDisk: true);
            archiveStream.Position = 0;
            using var archive = new CoreZipArchive(archiveStream, password, leaveOpen: true);
            if (archive.Entries.Count != expectedByName.Count)
                throw new InvalidDataException();

            var validatedNames = new HashSet<string>(StringComparer.Ordinal);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                foreach (var entry in archive.Entries)
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

}
