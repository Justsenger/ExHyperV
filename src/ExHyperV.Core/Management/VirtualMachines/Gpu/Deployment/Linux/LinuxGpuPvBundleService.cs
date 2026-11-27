using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using R = ExHyperV.Properties.Resources;

namespace ExHyperV.Services;

internal static class LinuxGpuPvBundleService
{
    internal const string GuestSetupCommand = "sudo mount -L GPUPV /mnt && sudo sh /mnt/setup";
    internal const string IsoVolumeLabel = "GPUPV";
    private const string DefaultLatestReleaseApiUrl = "https://api.github.com/repos/Justsenger/hyperv-linux-gpupv/releases/latest";
    private const string Sha256SumsAssetName = "SHA256SUMS";
    private static readonly string[] RequiredLibraries = ["libdxcore.so", "libd3d12.so", "libd3d12core.so"];
    private static readonly Regex LowerSha256Regex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex VersionRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex ReleaseRegex = new("^[A-Za-z0-9][A-Za-z0-9.+_-]*$", RegexOptions.Compiled);
    private static readonly Regex SafeAssetNameRegex = new(@"^linuxpv-bundle-[A-Za-z0-9._-]+\.zip$", RegexOptions.Compiled);
    private static readonly Regex Sha256LineRegex = new(@"^([0-9a-fA-F]{64})\s+\*?(.+)$", RegexOptions.Compiled);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    internal static string GetTargetArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        var other => throw new NotSupportedException(string.Format(R.Error_Gpu_LinuxArchUnsupported, other))
    };

    internal static ushort GetExpectedElfMachine(string arch) => arch switch
    {
        "x86_64" => 62,
        "aarch64" => 183,
        _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, null)
    };

    internal static bool IsValidElf64Header(ReadOnlySpan<byte> header, ushort expectedMachine, ReadOnlySpan<ushort> allowedTypes)
    {
        if (header.Length < 64) return false;
        // 0x7F 'E' 'L' 'F', ELFCLASS64 (2), ELFDATA2LSB (1)
        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F'
            || header[4] != 2 || header[5] != 1)
            return false;

        ushort eType = (ushort)(header[16] | (header[17] << 8));
        ushort eMachine = (ushort)(header[18] | (header[19] << 8));
        if (eMachine != expectedMachine) return false;

        foreach (ushort allowed in allowedTypes)
        {
            if (eType == allowed) return true;
        }
        return false;
    }

    internal static void AssertValidElf64File(string filePath, ushort expectedMachine, ReadOnlySpan<ushort> allowedTypes, string label)
    {
        Span<byte> header = stackalloc byte[64];
        using var stream = File.OpenRead(filePath);
        int totalRead = 0;
        while (totalRead < 64)
        {
            int n = stream.Read(header[totalRead..]);
            if (n <= 0) break;
            totalRead += n;
        }
        if (totalRead < 64 || !IsValidElf64Header(header, expectedMachine, allowedTypes))
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleBadElf, label));
    }

    internal static Task<LinuxGpuPvBundleInfo> InspectAsync(
        string zipPath,
        string? expectedArch = null,
        Action<int>? progressPercent = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ValidateAndInspectCore(zipPath, expectedArch ?? GetTargetArchitecture(), progressPercent, cancellationToken), cancellationToken);

    internal static Task<LinuxGpuPvBundleInfo> ExtractAndVerifyAsync(
        string zipPath,
        string destinationDirectory,
        string? expectedArch = null,
        Action<int>? progressPercent = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            string arch = expectedArch ?? GetTargetArchitecture();
            var info = ValidateAndInspectCore(zipPath, arch, pct => progressPercent?.Invoke(pct / 2), cancellationToken);
            ExtractSafeCore(zipPath, destinationDirectory, info, arch, pct => progressPercent?.Invoke(50 + pct / 2), cancellationToken);
            return info;
        }, cancellationToken);

    internal static Task<string> DownloadLatestAsync(
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
        => DownloadLatestCoreAsync(SharedHttpClient, DefaultLatestReleaseApiUrl, GetDefaultCacheDirectory(), GetTargetArchitecture(), progress, cancellationToken);

    internal static Task<string> DownloadLatestCoreAsync(
        HttpClient httpClient,
        string releaseApiUrl,
        string cacheDirectory,
        string expectedArch,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadInternalAsync(httpClient, releaseApiUrl, cacheDirectory, expectedArch, progress, cancellationToken);

    private static async Task<string> DownloadInternalAsync(
        HttpClient httpClient,
        string releaseApiUrl,
        string cacheDirectory,
        string expectedArch,
        Action<int, string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke(0, R.Msg_Gpu_FetchingBundleInfo);
        GitHubReleaseDto release;
        try
        {
            using var req = CreateGitHubRequest(releaseApiUrl);
            using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var jsonStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            release = await JsonSerializer.DeserializeAsync(jsonStream, CoreJson.Default.GitHubReleaseDto, cancellationToken).ConfigureAwait(false)
                ?? throw new LinuxBundleException(R.Error_Gpu_LinuxBundleOnlineEmpty);
        }
        catch (OperationCanceledException) { throw; }
        catch (LinuxBundleException) { throw; }
        catch (Exception ex)
        {
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleOnlineFetchFailed, ex.Message), ex);
        }

        string archPrefix = $"linuxpv-bundle-{expectedArch}-";
        var bundleAsset = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith(archPrefix, StringComparison.Ordinal)
            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && SafeAssetNameRegex.IsMatch(a.Name));
        if (bundleAsset == null || string.IsNullOrWhiteSpace(bundleAsset.BrowserDownloadUrl))
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleArchAssetMissing, expectedArch));

        var sumsAsset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, Sha256SumsAssetName, StringComparison.Ordinal));
        if (sumsAsset == null || string.IsNullOrWhiteSpace(sumsAsset.BrowserDownloadUrl))
            throw new LinuxBundleException(R.Error_Gpu_LinuxBundleSumsMissing);

        string expectedZipHash;
        try
        {
            using var req = CreateGitHubRequest(sumsAsset.BrowserDownloadUrl);
            using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string sumsText = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            expectedZipHash = ParseSha256Sums(sumsText, bundleAsset.Name)
                ?? throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleSumEntryMissing, bundleAsset.Name));
        }
        catch (OperationCanceledException) { throw; }
        catch (LinuxBundleException) { throw; }
        catch (Exception ex)
        {
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleOnlineFetchFailed, ex.Message), ex);
        }

        Directory.CreateDirectory(cacheDirectory);
        string cachedPath = Path.Combine(cacheDirectory, bundleAsset.Name);
        if (File.Exists(cachedPath))
        {
            try
            {
                string existingHash = ComputeFileSha256(cachedPath);
                if (string.Equals(existingHash, expectedZipHash, StringComparison.OrdinalIgnoreCase))
                {
                    ValidateAndInspectCore(cachedPath, expectedArch, null, cancellationToken);
                    progress?.Invoke(100, string.Format(R.Msg_Gpu_DownloadingBundlePct, 100));
                    return cachedPath;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // 缓存文件损坏或失效时忽略缓存并重新下载
            }
        }

        string partPath = Path.Combine(cacheDirectory, $"{bundleAsset.Name}.{Guid.NewGuid():N}.part");
        try
        {
            using var req = CreateGitHubRequest(bundleAsset.BrowserDownloadUrl);
            using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? bundleAsset.Size;
            long receivedBytes = 0;
            int lastPct = -1;

            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    receivedBytes += read;
                    int pct = totalBytes > 0
                        ? (int)Math.Clamp(receivedBytes * 100L / totalBytes, 0, 99)
                        : 0;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progress?.Invoke(pct, string.Format(R.Msg_Gpu_DownloadingBundlePct, pct));
                    }
                }
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            string actualHash = ComputeFileSha256(partPath);
            if (!string.Equals(actualHash, expectedZipHash, StringComparison.OrdinalIgnoreCase))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleChecksumMismatch, bundleAsset.Name));

            ValidateAndInspectCore(partPath, expectedArch, null, requireZipExtension: false, cancellationToken);
            File.Move(partPath, cachedPath, overwrite: true);
            progress?.Invoke(100, string.Format(R.Msg_Gpu_DownloadingBundlePct, 100));
            return cachedPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (LinuxBundleException) { throw; }
        catch (Exception ex)
        {
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleDownloadFailed, ex.Message), ex);
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    private static LinuxGpuPvBundleInfo ValidateAndInspectCore(
        string zipPath,
        string expectedArch,
        Action<int>? progressPercent,
        CancellationToken cancellationToken)
        => ValidateAndInspectCore(zipPath, expectedArch, progressPercent, requireZipExtension: true, cancellationToken);

    private static LinuxGpuPvBundleInfo ValidateAndInspectCore(
        string zipPath,
        string expectedArch,
        Action<int>? progressPercent,
        bool requireZipExtension,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new LinuxBundleException(R.Error_Gpu_LinuxBundleNotFound);

        var fileInfo = new FileInfo(zipPath);
        if (requireZipExtension && !string.Equals(fileInfo.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
            throw new LinuxBundleException(R.Error_Gpu_LinuxBundleNotZip);

        ushort expectedMachine = GetExpectedElfMachine(expectedArch);

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(fileInfo.FullName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleInvalidZip, ex.Message), ex);
        }

        using (archive)
        {
            var entryMap = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            bool hasKernelFiles = false;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullName = entry.FullName;
                if (!IsSafeArchiveEntryPath(fullName))
                    throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleUnsafePath, fullName));

                // 宿主合成时拥有 drivers/ 与 device.json 所有权，不允许预打包覆盖。
                if (fullName.StartsWith("drivers/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fullName, "device.json", StringComparison.OrdinalIgnoreCase))
                    throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleUnsafePath, fullName));

                if (fullName.EndsWith('/')) continue;
                if (!entryMap.TryAdd(fullName, entry))
                    throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleDuplicateEntry, fullName));

                if (fullName.StartsWith("kernel/", StringComparison.Ordinal))
                    hasKernelFiles = true;
            }

            if (!entryMap.ContainsKey("guest/setup") || !hasKernelFiles)
                throw new LinuxBundleException(R.Error_Gpu_LinuxBundleMissingCoreFiles);

            if (!entryMap.TryGetValue("manifest.json", out var manifestEntry))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleMissingMember, "manifest.json"));

            LinuxBundleManifest manifest = ReadJsonEntry(manifestEntry, CoreJson.Default.LinuxBundleManifest, "manifest.json");
            if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Version) || !VersionRegex.IsMatch(manifest.Version))
                throw new LinuxBundleException(R.Error_Gpu_LinuxBundleBadManifest);

            if (!string.Equals(manifest.Arch, expectedArch, StringComparison.Ordinal))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleArchMismatch, expectedArch, manifest.Arch));

            if (!LowerSha256Regex.IsMatch(manifest.ProbeSha256 ?? string.Empty))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleBadChecksumSpec, "guest/d3d12-compute"));

            foreach (string libName in RequiredLibraries)
            {
                if (!manifest.Libraries.TryGetValue(libName, out string? hash) || !LowerSha256Regex.IsMatch(hash ?? string.Empty))
                    throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleMissingMember, $"lib/{libName}"));
            }

            if (!entryMap.TryGetValue("prebuilt/index.json", out var indexEntry))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleMissingMember, "prebuilt/index.json"));

            List<LinuxBundleKernelEntry> indexEntries = ReadJsonEntry(indexEntry, CoreJson.Default.ListLinuxBundleKernelEntry, "prebuilt/index.json");
            if (indexEntries.Count == 0 || !KernelListsEqual(indexEntries, manifest.Kernels))
                throw new LinuxBundleException(R.Error_Gpu_LinuxBundleBadKernelIndex);

            var verifications = new List<(string MemberName, string ExpectedHash, ushort[] AllowedElfTypes)>(RequiredLibraries.Length + 1 + indexEntries.Count);
            foreach (string libName in RequiredLibraries)
                verifications.Add(($"lib/{libName}", manifest.Libraries[libName], [3]));

            verifications.Add(("guest/d3d12-compute", manifest.ProbeSha256, [2, 3]));

            foreach (var kernel in indexEntries)
            {
                if (!ReleaseRegex.IsMatch(kernel.Release ?? string.Empty)
                    || !string.Equals(kernel.Arch, expectedArch, StringComparison.Ordinal)
                    || !string.Equals(kernel.Name, "dxgkrnl", StringComparison.Ordinal)
                    || !(kernel.Vermagic ?? string.Empty).StartsWith(kernel.Release + " ", StringComparison.Ordinal)
                    || !LowerSha256Regex.IsMatch(kernel.Sha256 ?? string.Empty))
                    throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleBadKernelMeta, kernel.Release));

                verifications.Add(($"prebuilt/{expectedArch}/{kernel.Release}/dxgkrnl.ko", kernel.Sha256, [1]));
            }

            for (int i = 0; i < verifications.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (memberName, expectedHash, allowedTypes) = verifications[i];
                if (!entryMap.TryGetValue(memberName, out var memberEntry))
                    throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleMissingMember, memberName));

                VerifyZipMemberElfAndSha256(memberEntry, memberName, expectedHash, expectedMachine, allowedTypes, cancellationToken);
                progressPercent?.Invoke((i + 1) * 100 / verifications.Count);
            }

            return new LinuxGpuPvBundleInfo(
                fileInfo.FullName,
                fileInfo.Name,
                manifest.Version,
                manifest.Arch,
                indexEntries.Count,
                fileInfo.Length);
        }
    }

    private static void ExtractSafeCore(
        string zipPath,
        string destinationDirectory,
        LinuxGpuPvBundleInfo inspected,
        string expectedArch,
        Action<int>? progressPercent,
        CancellationToken cancellationToken)
    {
        string destRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destRoot);

        using var archive = ZipFile.OpenRead(zipPath);
        int total = Math.Max(1, archive.Entries.Count);
        int count = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (entry.FullName.EndsWith('/')) continue;

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string targetPath = Path.GetFullPath(Path.Combine(destRoot, relative));
            if (!targetPath.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleUnsafePath, entry.FullName));

            string? parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

            using (var src = entry.Open())
            using (var dst = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }
            progressPercent?.Invoke(count * 100 / total);
        }

        // 解压后再核对核心文件存在及 ELF 架构。
        ushort expectedMachine = GetExpectedElfMachine(expectedArch);
        foreach (string libName in RequiredLibraries)
            AssertValidElf64File(Path.Combine(destRoot, "lib", libName), expectedMachine, [3], $"lib/{libName}");
        AssertValidElf64File(Path.Combine(destRoot, "guest", "d3d12-compute"), expectedMachine, [2, 3], "guest/d3d12-compute");
    }

    private static void VerifyZipMemberElfAndSha256(
        ZipArchiveEntry entry,
        string memberName,
        string expectedSha256,
        ushort expectedMachine,
        ReadOnlySpan<ushort> allowedTypes,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = entry.Open();
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            byte[] header = new byte[64];
            int headerBytes = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (headerBytes < 64)
                {
                    int take = Math.Min(64 - headerBytes, read);
                    Buffer.BlockCopy(buffer, 0, header, headerBytes, take);
                    headerBytes += take;
                }
                hasher.AppendData(buffer, 0, read);
            }

            if (headerBytes < 64 || !IsValidElf64Header(header, expectedMachine, allowedTypes))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleBadElf, memberName));

            string actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
                throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleChecksumMismatch, memberName));
        }
        catch (LinuxBundleException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleInvalidZip, ex.Message), ex);
        }
    }

    private static T ReadJsonEntry<T>(ZipArchiveEntry entry, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string memberName)
    {
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize(stream, typeInfo)
                ?? throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleBadJson, memberName));
        }
        catch (LinuxBundleException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new LinuxBundleException(string.Format(R.Error_Gpu_LinuxBundleBadJson, memberName), ex);
        }
    }

    private static bool KernelListsEqual(IReadOnlyList<LinuxBundleKernelEntry> a, IReadOnlyList<LinuxBundleKernelEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var left = a[i];
            var right = b[i];
            if (!string.Equals(left.Release, right.Release, StringComparison.Ordinal)
                || !string.Equals(left.Arch, right.Arch, StringComparison.Ordinal)
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(left.Vermagic, right.Vermagic, StringComparison.Ordinal)
                || !string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
                || !string.Equals(left.SrcVersion, right.SrcVersion, StringComparison.Ordinal))
                return false;

            var leftTargets = left.Targets ?? [];
            var rightTargets = right.Targets ?? [];
            if (!leftTargets.SequenceEqual(rightTargets, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static bool IsSafeArchiveEntryPath(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || fullName.StartsWith('/') || fullName.Contains('\\') || fullName.Contains(':') || fullName.Contains('\0'))
            return false;
        foreach (string part in fullName.Split('/'))
        {
            if (part is "." or "..") return false;
        }
        return true;
    }

    internal static string? ParseSha256Sums(string text, string fileName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            var match = Sha256LineRegex.Match(line);
            if (!match.Success) continue;
            string listedName = match.Groups[2].Value.Trim();
            if (string.Equals(listedName, fileName, StringComparison.Ordinal))
                return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string GetDefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExHyperV",
        "linux-gpupv-bundles");

    private static HttpRequestMessage CreateGitHubRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("ExHyperV/2.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return req;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(15) };
    }
}
