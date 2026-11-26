using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class WriteBoundaryTests
{
    [TestMethod]
    public async Task PendingAndCancelledPowerRequestsRemainUnknown()
    {
        var pending = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown,
            (await VmPowerService.WaitForTurnOffRequestAsync(pending.Task, TimeSpan.Zero)).ErrorKind);
        pending.SetResult(ApiResponse.Ok());
        foreach (var kind in new[] { ApiErrorKind.Cancelled, ApiErrorKind.OutcomeUnknown })
            Assert.AreEqual(ApiErrorKind.OutcomeUnknown, (await VmPowerService.WaitForTurnOffRequestAsync(
                Task.FromResult(ApiResponse.Fail("waiting", kind: kind)), TimeSpan.FromSeconds(1))).ErrorKind);
    }

    [TestMethod]
    public void SnapshotRequiresOneSettledCreatedObject()
    {
        Assert.IsTrue(VmCheckpointService.ConfirmSnapshotReceipt(new(ApiResponse.Ok(), ["snapshot"], false)).Success);
        foreach (var receipt in new WmiCreationResult[] { new(ApiResponse.Ok(), [], false),
                     new(ApiResponse.Ok(), ["a", "b"], false), new(ApiResponse.Ok(), ["snapshot"], true) })
            Assert.AreEqual(ApiErrorKind.OutcomeUnknown, VmCheckpointService.ConfirmSnapshotReceipt(receipt).ErrorKind);
    }

    [TestMethod]
    public void InvalidDriverMapsCannotBeMergedAsEmptyMaps()
    {
        foreach (string text in new[] { "", "{", "null", "[]", "{\"a\":\"1\",\"A\":\"2\"}", "{\"a\":null}" })
        {
            try
            {
                VmMetadataService.ParseGpuDriversMap(new Dictionary<string, string> { [VmMetadataService.GpuDriversKey] = text });
                Assert.Fail("Unexpected acceptance: " + text);
            }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { }
        }
    }

    [TestMethod]
    public async Task NetworkAndGpuMetadataUseTheVmWriteLockBeforeReading()
    {
        Guid id = Guid.NewGuid();
        int reads = 0;
        using var hook = WmiApi.UseQuerySource((_, _) =>
        {
            HostResourceLock.AssertHeld(VmSettingsWriteLock.Resource(id));
            Interlocked.Increment(ref reads);
            return new(new(ApiErrorKind.AccessDenied, 5, ApiErrorSource.Wmi, "probe"));
        });
        var adapter = new VmNetworkAdapter { Id = $"Microsoft:{id:D}\\{Guid.NewGuid():D}" };
        Assert.AreEqual(ApiErrorKind.AccessDenied, (await VmNetworkService.ApplySecuritySettingsAsync(id, adapter,
            [nameof(adapter.DhcpGuardEnabled)])).ErrorKind);
        var combined = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(id, adapter,
            [nameof(adapter.DhcpGuardEnabled), nameof(adapter.SriovEnabled)]);
        Assert.AreEqual(ApiErrorKind.AccessDenied, combined.Failure?.Kind);
        Assert.AreEqual(ApiErrorKind.AccessDenied, (await VmMetadataService.UpsertGpuDriverVersionsAsync(id, [("a", "1")])).ErrorKind);
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public void CopyRemainsInTheSelectedRootAndRejectsAmbiguousVolumePaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV-Volume-" + Guid.NewGuid().ToString("N"));
        string guest = Directory.CreateDirectory(Path.Combine(root, "guest")).FullName;
        try
        {
            string source = Path.Combine(root, "source");
            File.WriteAllText(source, "payload");
            using var volume = new GuestVolumeFiles(guest);
            string target = Path.Combine(guest, "nested", "target");
            volume.CopyFile(source, target, readOnly: false);
            Assert.AreEqual("payload", File.ReadAllText(target));
            Assert.ThrowsExactly<GuestVolumeBoundaryException>(() => volume.CopyFile(source, Path.Combine(guest, "..", "outside"), false));
            Assert.IsFalse(File.Exists(Path.Combine(root, "outside")));
            string id = @"\\?\Volume{" + Guid.NewGuid().ToString("D") + @"}\";
            Assert.AreEqual(id, GuestVolumeFiles.PartitionVolume([@"Z:\", id], 'Z'));
            Assert.ThrowsExactly<GuestVolumeBoundaryException>(() => GuestVolumeFiles.PartitionVolume([@"Y:\", id], 'Z'));
            Assert.ThrowsExactly<GuestVolumeBoundaryException>(() => GuestVolumeFiles.PartitionVolume([@"Z:\", id, id], 'Z'));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task GpuRemovalUsesFreshInventoryAndPreservesBothStepResults()
    {
        var remaining = new List<(string Id, string InstancePath)> { ("new", "gpu") };
        int cleared = 0;
        Task<ApiResponse> Clear(string key)
        {
            Assert.AreEqual("GPU", key);
            cleared++;
            return Task.FromResult(WmiJobWaiter.Unknown("metadata pending"));
        }
        Task<ApiResponse> Finish() => VmGpuService.FinishRemovalAsync("old", "gpu", () => Task.FromResult((true, remaining)), Clear);
        Assert.IsTrue((await Finish()).Success);
        remaining.Clear();
        remaining.Add(("new", ""));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, (await Finish()).ErrorKind);
        Assert.AreEqual(0, cleared);
        remaining.Clear();
        var result = await Finish();
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
        Assert.AreEqual(OperationStepState.Succeeded, result.Steps[0].State);
        Assert.AreEqual(OperationStepState.OutcomeUnknown, result.Steps[1].State);
        Assert.AreEqual(1, cleared);
    }

    [TestMethod]
    public void JunctionIsRejectedByGuestWritesAndByIsoSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV-Junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string link = Path.Combine(root, "junction");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "regular"));
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { Arguments = "/d /c mklink /j junction regular", WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false };
            using var command = System.Diagnostics.Process.Start(start)!;
            command.WaitForExit();
            Assert.AreEqual(0, command.ExitCode);
            using var guest = new GuestVolumeFiles(root);
            var files = new GpuDeploymentFileOperations(guest, _ => { });
            Assert.ThrowsExactly<GuestVolumeBoundaryException>(() => files.PrepareDirectory(link));
            Assert.ThrowsExactly<GuestVolumeBoundaryException>(() => files.ReplaceWithLink(Path.Combine(link, "new"), "guest", false));
            Assert.ThrowsExactly<GuestVolumeBoundaryException>(() => guest.CopyFile("unused", Path.Combine(link, "file"), false));
            Assert.ThrowsExactly<IOException>(() => IsoBuilderService.BuildUdfIso(link, Path.Combine(root, "out.iso"), "TEST"));
            Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(root, "regular")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }
    [TestMethod]
    public void GuardianIdentityBindsBothCertificatesFromTheSameMetadataElement()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        byte[] Cert(string subject)
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(subject, key,
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return cert.RawData;
        }
        var signing = Cert("CN=Signing");
        var encryption = Cert("CN=Encryption");
        string xml = $"""
            <Metadata xmlns="http://schemas.microsoft.com/kps/2014/07"><GuardianInformation>
            <SigningCertificate>{Convert.ToBase64String(signing)}</SigningCertificate>
            <EncryptionCertificate>{Convert.ToBase64String(encryption)}</EncryptionCertificate>
            </GuardianInformation></Metadata>
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var pair = HgsGuardians.ReadGuardianIdentity(stream);
        Assert.IsTrue(HgsGuardians.MatchesPair(pair, signing, encryption));
        Assert.IsFalse(HgsGuardians.MatchesPair(pair, signing, signing));
    }
}
