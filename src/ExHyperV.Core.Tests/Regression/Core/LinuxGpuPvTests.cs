using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class LinuxGpuPvTests
{
    private static byte[] MakeElf64(ushort type, ushort machine, byte[]? extra = null)
    {
        byte[] data = new byte[Math.Max(64, 64 + (extra?.Length ?? 0))];
        data[0] = 0x7F;
        data[1] = (byte)'E';
        data[2] = (byte)'L';
        data[3] = (byte)'F';
        data[4] = 2; // ELFCLASS64
        data[5] = 1; // ELFDATA2LSB
        data[16] = (byte)(type & 0xFF);
        data[17] = (byte)(type >> 8);
        data[18] = (byte)(machine & 0xFF);
        data[19] = (byte)(machine >> 8);
        if (extra is { Length: > 0 })
            Buffer.BlockCopy(extra, 0, data, 64, extra.Length);
        return data;
    }

    private static string BuildBundleZip(
        string outputZipPath,
        string arch = "x86_64",
        string version = "v0.1.1",
        bool corruptProbeChecksum = false,
        ushort? overrideLibMachine = null,
        string? extraForbiddenEntry = null,
        string? extraTraversalEntry = null)
    {
        ushort machine = overrideLibMachine ?? (arch == "aarch64" ? (ushort)183 : (ushort)62);
        byte[] dxcore = MakeElf64(3, machine, "dxcore"u8.ToArray());
        byte[] d3d12 = MakeElf64(3, machine, "d3d12"u8.ToArray());
        byte[] d3d12core = MakeElf64(3, machine, "d3d12core"u8.ToArray());
        byte[] probe = MakeElf64(2, machine, "probe"u8.ToArray());
        byte[] ko = MakeElf64(1, machine, "dxgkrnl"u8.ToArray());

        string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

        var kernels = new[]
        {
            new
            {
                release = "6.8.0-45-generic",
                arch,
                name = "dxgkrnl",
                vermagic = "6.8.0-45-generic SMP preempt mod_unload ",
                sha256 = Hash(ko),
                srcversion = "ABC123",
                targets = new[] { "ubuntu-24.04" }
            }
        };

        var manifest = new
        {
            schemaVersion = 1,
            arch,
            version,
            libraries = new Dictionary<string, string>
            {
                ["libdxcore.so"] = Hash(dxcore),
                ["libd3d12.so"] = Hash(d3d12),
                ["libd3d12core.so"] = Hash(d3d12core)
            },
            probeSha256 = corruptProbeChecksum ? new string('0', 64) : Hash(probe),
            kernels
        };

        using (var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Create))
        {
            void WriteBytes(string name, byte[] bytes)
            {
                var e = archive.CreateEntry(name);
                using var s = e.Open();
                s.Write(bytes, 0, bytes.Length);
            }

            WriteBytes("guest/setup", "#!/bin/sh\necho ok\n"u8.ToArray());
            WriteBytes("kernel/install-module.sh", "#!/bin/sh\n"u8.ToArray());
            WriteBytes("lib/libdxcore.so", dxcore);
            WriteBytes("lib/libd3d12.so", d3d12);
            WriteBytes("lib/libd3d12core.so", d3d12core);
            WriteBytes("guest/d3d12-compute", probe);
            WriteBytes($"prebuilt/{arch}/6.8.0-45-generic/dxgkrnl.ko", ko);
            WriteBytes("prebuilt/index.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(kernels)));
            WriteBytes("manifest.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));

            if (extraForbiddenEntry != null)
                WriteBytes(extraForbiddenEntry, "x"u8.ToArray());
            if (extraTraversalEntry != null)
                WriteBytes(extraTraversalEntry, "x"u8.ToArray());
        }

        return outputZipPath;
    }

    [TestMethod]
    public async Task BundleValidator_AcceptsValidArchitectureAndRejectsCorruptOrMismatchedArchives()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ExHyperV-LinuxBundleTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string expectedArch = LinuxGpuPvBundleService.GetTargetArchitecture();
            string wrongArch = expectedArch == "x86_64" ? "aarch64" : "x86_64";

            string validZip = BuildBundleZip(Path.Combine(tempDir, "valid.zip"), arch: expectedArch, version: "v0.1.1");
            var info = await LinuxGpuPvBundleService.InspectAsync(validZip, expectedArch);
            Assert.AreEqual("v0.1.1", info.Version);
            Assert.AreEqual(expectedArch, info.Architecture);
            Assert.AreEqual(1, info.KernelCount);

            string wrongArchZip = BuildBundleZip(Path.Combine(tempDir, "wrong-arch.zip"), arch: wrongArch);
            await Assert.ThrowsExactlyAsync<LinuxBundleException>(() =>
                LinuxGpuPvBundleService.InspectAsync(wrongArchZip, expectedArch));

            string badShaZip = BuildBundleZip(Path.Combine(tempDir, "bad-sha.zip"), arch: expectedArch, corruptProbeChecksum: true);
            await Assert.ThrowsExactlyAsync<LinuxBundleException>(() =>
                LinuxGpuPvBundleService.InspectAsync(badShaZip, expectedArch));

            string badElfZip = BuildBundleZip(Path.Combine(tempDir, "bad-elf.zip"), arch: expectedArch, overrideLibMachine: 3);
            await Assert.ThrowsExactlyAsync<LinuxBundleException>(() =>
                LinuxGpuPvBundleService.InspectAsync(badElfZip, expectedArch));

            string forbiddenZip = BuildBundleZip(Path.Combine(tempDir, "forbidden.zip"), arch: expectedArch, extraForbiddenEntry: "drivers/nv/test.so");
            await Assert.ThrowsExactlyAsync<LinuxBundleException>(() =>
                LinuxGpuPvBundleService.InspectAsync(forbiddenZip, expectedArch));

            string traversalZip = BuildBundleZip(Path.Combine(tempDir, "traversal.zip"), arch: expectedArch, extraTraversalEntry: "../evil");
            await Assert.ThrowsExactlyAsync<LinuxBundleException>(() =>
                LinuxGpuPvBundleService.InspectAsync(traversalZip, expectedArch));

            string badExt = Path.Combine(tempDir, "notzip.txt");
            File.Copy(validZip, badExt);
            var publicResp = await VmGpuService.InspectLinuxGpuPvBundleAsync(badExt);
            Assert.IsFalse(publicResp.Success);
            Assert.AreEqual(ApiErrorKind.InvalidArgument, publicResp.ErrorKind);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task OnlineDownloader_VerifiesSha256SumsAndReusesMatchingCache()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ExHyperV-LinuxDownloadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string expectedArch = LinuxGpuPvBundleService.GetTargetArchitecture();
            string assetName = $"linuxpv-bundle-{expectedArch}-v0.1.1.zip";
            string sourceZip = BuildBundleZip(Path.Combine(tempDir, assetName), arch: expectedArch, version: "v0.1.1");
            byte[] zipBytes = await File.ReadAllBytesAsync(sourceZip);
            string zipSha = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            string sumsBody = $"{zipSha}  {assetName}\n";

            string releaseJson = JsonSerializer.Serialize(new
            {
                tag_name = "v0.1.1",
                prerelease = false,
                assets = new object[]
                {
                    new { name = assetName, size = zipBytes.Length, browser_download_url = "https://example.invalid/bundle.zip" },
                    new { name = "SHA256SUMS", size = sumsBody.Length, browser_download_url = "https://example.invalid/SHA256SUMS" }
                }
            });

            int downloadRequests = 0;
            using var handler = new StubHttpHandler(url =>
            {
                if (url.EndsWith("/latest", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, Encoding.UTF8.GetBytes(releaseJson));
                if (url.EndsWith("/SHA256SUMS", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, Encoding.UTF8.GetBytes(sumsBody));
                downloadRequests++;
                return (HttpStatusCode.OK, zipBytes);
            });
            using var client = new HttpClient(handler);
            string cacheDir = Path.Combine(tempDir, "cache");

            var progressValues = new List<int>();
            string downloaded = await LinuxGpuPvBundleService.DownloadLatestCoreAsync(
                client,
                "https://example.invalid/releases/latest",
                cacheDir,
                expectedArch,
                (pct, _) => progressValues.Add(pct));

            Assert.IsTrue(File.Exists(downloaded));
            Assert.AreEqual(1, downloadRequests);
            Assert.Contains(100, progressValues);

            string cachedHit = await LinuxGpuPvBundleService.DownloadLatestCoreAsync(
                client,
                "https://example.invalid/releases/latest",
                cacheDir,
                expectedArch);
            Assert.AreEqual(downloaded, cachedHit);
            Assert.AreEqual(1, downloadRequests, "Cache hit matching SHA256SUMS should skip re-downloading");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void WslInfFileMap_ResolvesLxssCopySectionsByArchitecture()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ExHyperV-LinuxInfTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string infPath = Path.Combine(tempDir, "testgpu.inf");
            File.WriteAllText(infPath, """
                [Version]
                Signature="$WINDOWS NT$"

                [DestinationDirs]
                Shared_Lxss = 11, "lxss\lib"

                [DestinationDirs.NTamd64]
                X64_Lxss = 11, "lxss\lib"

                [DestinationDirs.NTarm64]
                Arm64_Lxss = 11, "lxss\lib"

                [Shared_Lxss]
                libcommon.so

                [X64_Lxss]
                libcuda.so.1, libcuda_x64.so

                [Arm64_Lxss]
                libcuda.so.1, libcuda_arm64.so
                """);

            var x64Map = LinuxGpuPvRuntimeService.ReadWslFileMap(new FileInfo(infPath), "x86_64");
            Assert.AreEqual("libcommon.so", x64Map["libcommon.so"]);
            Assert.AreEqual("libcuda_x64.so", x64Map["libcuda.so.1"]);

            var armMap = LinuxGpuPvRuntimeService.ReadWslFileMap(new FileInfo(infPath), "aarch64");
            Assert.AreEqual("libcommon.so", armMap["libcommon.so"]);
            Assert.AreEqual("libcuda_arm64.so", armMap["libcuda.so.1"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void IsoBuilder_ForceUdfOnly_ProducesPureUdfWithoutIso9660Descriptor()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ExHyperV-LinuxIsoTests-" + Guid.NewGuid().ToString("N"));
        string stageDir = Path.Combine(tempDir, "stage");
        string isoPath = Path.Combine(tempDir, "gpupv.iso");
        Directory.CreateDirectory(Path.Combine(stageDir, "guest"));
        try
        {
            File.WriteAllText(Path.Combine(stageDir, "setup"), "#!/bin/sh\nexec sh \"$(dirname \"$0\")/guest/setup\" \"$@\"\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stageDir, "guest", "setup"), "#!/bin/sh\necho ok\n", new UTF8Encoding(false));

            IsoBuilderService.BuildUdfIso(
                [new IsoDirectorySource(stageDir, string.Empty)],
                isoPath,
                LinuxGpuPvBundleService.IsoVolumeLabel,
                null,
                forceUdfOnly: true);

            Assert.IsTrue(File.Exists(isoPath));
            using var stream = File.OpenRead(isoPath);
            byte[] vrs = new byte[0x3000];
            stream.Seek(0x8000, SeekOrigin.Begin);
            int read = stream.Read(vrs, 0, vrs.Length);
            Assert.IsGreaterThan(0x800, read);
            string ascii = Encoding.ASCII.GetString(vrs, 0, read);
            Assert.Contains("BEA01", ascii, "Pure UDF ISO must contain VRS BEA01 descriptor");
            Assert.Contains("NSR0", ascii, "Pure UDF ISO must contain UDF NSR02/NSR03 descriptor");
            Assert.IsFalse(ascii.StartsWith("\u0001CD001", StringComparison.Ordinal), "Pure UDF ISO must not start sector 16 with ISO9660 CD001 PVD");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubHttpHandler(Func<string, (HttpStatusCode Status, byte[] Body)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = responder(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(response);
        }
    }
}
