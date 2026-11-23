using System;
using System.IO;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    internal sealed record IsoDirectorySource(string SourceDirectory, string TargetSubDirectory = "");

    /// <summary>使用 Windows IMAPI 将目录构建为包含 ISO 9660 与 UDF 文件系统的光盘映像。</summary>
    public static class IsoBuilderService
    {
        /// <summary>删除卷标中不支持的字符并限制长度，为空时使用默认卷标。</summary>
        /// <returns>可传给本服务映像构建器的卷标。</returns>
        /// <param name="name">候选卷标文本。</param>
        internal static string SanitizeVolumeLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "NewISO";
            char[] buf = name.ToCharArray();
            for (int i = 0; i < buf.Length; i++)
                if (!(char.IsLetterOrDigit(buf[i]) || buf[i] == '_' || buf[i] == '-')) buf[i] = '_';
            string s = new string(buf);
            while (s.Contains("__")) s = s.Replace("__", "_");
            s = s.Trim('_');
            if (s.Length > 32) s = s.Substring(0, 32);
            return s.Length == 0 ? "NewISO" : s;
        }

        private const long Iso9660VolumeSafetyLimitBytes = 4L * 1024 * 1024 * 1024;
        private const int MaxStreamReadBufferBytes = 1024 * 1024;

        internal static bool ShouldUseUdfOnly(long totalBytes, long maxFileBytes)
            => totalBytes >= Iso9660VolumeSafetyLimitBytes || maxFileBytes >= Iso9660VolumeSafetyLimitBytes;

        /// <summary>将源目录内容写成 ISO 9660/UDF 映像，当前线程等待写入完成。</summary>
        /// <remarks>输出只构建文件，不挂载映像。已有目标文件会被覆盖并截断至新映像长度；调用方负责选择输出路径。数据流提前结束时抛出异常，写入失败保留已有目标文件。源目录内容放在映像根目录下。</remarks>
        /// <param name="sourceDirectory">包含要写入光盘内容的宿主目录。</param>
        /// <param name="targetIsoPath">目标 ISO 文件路径。</param>
        /// <param name="volumeLabel">光盘卷标。Core 保留字母、数字、下划线和连字符，其他字符替换为下划线，最多 32 个字符；空值使用 NewISO。</param>
        public static void BuildUdfIso(string sourceDirectory, string targetIsoPath, string volumeLabel)
            => BuildUdfIso([new IsoDirectorySource(sourceDirectory, string.Empty)], targetIsoPath, volumeLabel, null, CancellationToken.None);

        internal static void BuildUdfIso(
            IReadOnlyList<IsoDirectorySource> sources,
            string targetIsoPath,
            string volumeLabel,
            Action<int>? progressPercent = null,
            CancellationToken cancellationToken = default)
        {
            if (sources == null || sources.Count == 0)
                throw new ArgumentException("至少需要一个 ISO 源目录。", nameof(sources));

            string? fallbackStaging = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                (long totalBytes, long maxFileBytes) = MeasureSources(sources, cancellationToken);

                using var apartment = new NativeComApartment(sta: false);
                using var image = ImapiImage.Create(SanitizeVolumeLabel(volumeLabel), ShouldUseUdfOnly(totalBytes, maxFileBytes));
                using var root = image.GetInterface(7); // IFileSystemImage::get_Root

                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(source.SourceDirectory) || !Directory.Exists(source.SourceDirectory))
                        throw new DirectoryNotFoundException($"找不到 ISO 源目录：{source.SourceDirectory}");

                    var target = root;
                    try
                    {
                        foreach (var segment in (source.TargetSubDirectory ?? string.Empty).Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var child = ImapiImage.OpenOrCreateDirectory(target, segment);
                            if (target != root) target.Dispose();
                            target = child;
                        }
                        AddDirectorySafe(target, new DirectoryInfo(source.SourceDirectory), ref fallbackStaging, cancellationToken);
                    }
                    finally { if (target != root) target.Dispose(); }
                }

                cancellationToken.ThrowIfCancellationRequested();
                string? targetDirPath = Path.GetDirectoryName(Path.GetFullPath(targetIsoPath));
                if (!string.IsNullOrWhiteSpace(targetDirPath)) Directory.CreateDirectory(targetDirPath);

                using var result = image.GetInterface(39); // IFileSystemImage::CreateResultImage
                using var stream = result.GetInterface(7); // IFileSystemImageResult::get_ImageStream
                int blockSize = result.GetInt32(10);
                int totalBlocks = result.GetInt32(9);
                WriteImageStream(stream.Read, targetIsoPath,
                    blockSize, totalBlocks, progressPercent, cancellationToken);
            }
            finally
            {
                if (fallbackStaging != null && Directory.Exists(fallbackStaging))
                {
                    try { Directory.Delete(fallbackStaging, recursive: true); }
                    catch { }
                }
            }
        }

        private static void AddDirectorySafe(
            NativeComInterface targetDir,
            DirectoryInfo sourceDir,
            ref string? fallbackStaging,
            CancellationToken cancellationToken)
        {
            VmPurgeFileSafety.EnsureNoReparse(sourceDir.FullName);
            if (!ContainsReparsePoint(sourceDir, cancellationToken))
            {
                ImapiImage.AddTree(targetDir, sourceDir.FullName);
                return;
            }

            // 仅对包含连接点/重解析点的目录做逐层跳过，避免对整个大目录做二次落盘复制
            FileInfo[] files = sourceDir.GetFiles();

            if (files.Length > 0)
            {
                fallbackStaging ??= Path.Combine(Path.GetTempPath(), $"ExHyperV-IsoStage-{Guid.NewGuid():N}");
                string leafDir = Path.Combine(fallbackStaging, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(leafDir);
                foreach (FileInfo file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    File.Copy(file.FullName, Path.Combine(leafDir, file.Name));
                }
                ImapiImage.AddTree(targetDir, leafDir);
            }

            DirectoryInfo[] subDirs = sourceDir.GetDirectories();

            foreach (DirectoryInfo sub in subDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                using var child = ImapiImage.OpenOrCreateDirectory(targetDir, sub.Name);
                AddDirectorySafe(child, sub, ref fallbackStaging, cancellationToken);
            }
        }

        private static (long TotalBytes, long MaxFileBytes) MeasureSources(
            IReadOnlyList<IsoDirectorySource> sources,
            CancellationToken cancellationToken)
        {
            long totalBytes = 0;
            long maxFileBytes = 0;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(source.SourceDirectory) || !Directory.Exists(source.SourceDirectory))
                    throw new DirectoryNotFoundException($"找不到 ISO 源目录：{source.SourceDirectory}");

                var stack = new Stack<DirectoryInfo>();
                VmPurgeFileSafety.EnsureNoReparse(source.SourceDirectory);
                stack.Push(new DirectoryInfo(source.SourceDirectory));
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DirectoryInfo dir = stack.Pop();
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (FileInfo file in dir.GetFiles())
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        long length = file.Length;
                        totalBytes += length;
                        if (length > maxFileBytes) maxFileBytes = length;
                    }
                    foreach (DirectoryInfo child in dir.GetDirectories())
                        stack.Push(child);
                }
            }
            return (totalBytes, maxFileBytes);
        }

        private static bool ContainsReparsePoint(DirectoryInfo root, CancellationToken cancellationToken)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryInfo dir = stack.Pop();
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                foreach (FileInfo file in dir.GetFiles())
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                foreach (DirectoryInfo child in dir.GetDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                    stack.Push(child);
                }
            }
            return false;
        }

        internal static void WriteImageStream(
            Func<byte[], int, int> read,
            string path,
            int blockSize,
            int totalBlocks,
            Action<int>? progressPercent,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
            ArgumentOutOfRangeException.ThrowIfNegative(totalBlocks);
            long total = (long)blockSize * totalBlocks;
            long remaining = total;
            long written = 0;
            int lastPercent = -1;

            void Report(int percent)
            {
                if (progressPercent == null || percent == lastPercent) return;
                lastPercent = percent;
                try { progressPercent(percent); }
                catch { }
            }

            int chunkSize = blockSize >= MaxStreamReadBufferBytes
                ? blockSize
                : (MaxStreamReadBufferBytes / blockSize) * blockSize;

            Report(0);
            string destination = Path.GetFullPath(path);
            string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".exhyperv-{Guid.NewGuid():N}.iso.tmp");
            string? temporaryIdentity = null;
            try
            {
                using (var fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryIdentity = HostDiskResource.Image(fs.SafeFileHandle);
                    byte[] buffer = new byte[chunkSize];
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int requested = (int)Math.Min(buffer.Length, remaining);
                        int bytesRead = read(buffer, requested);
                        if (bytesRead == 0) throw new EndOfStreamException($"光盘映像数据流提前结束，尚缺 {remaining} 字节。");
                        if (bytesRead < 0 || bytesRead > requested) throw new InvalidDataException("光盘映像数据流返回了无效的读取长度。");
                        fs.Write(buffer, 0, bytesRead);
                        remaining -= bytesRead;
                        written += bytesRead;
                        if (total > 0) Report((int)Math.Clamp(written * 100L / total, 0, 99));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                OwnedFileOperations.MoveOrDelete(temporary, temporaryIdentity, destination, replaceExisting: true);
                temporaryIdentity = null;
            }
            finally { if (temporaryIdentity != null) OwnedFileOperations.MoveOrDelete(temporary, temporaryIdentity, null); }
            Report(100);
        }
    }
}
