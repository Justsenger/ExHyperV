using ExHyperV.Tools;
using System.Runtime.InteropServices;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class GenerationUpgradeTests
{
    private readonly Guid id = Guid.NewGuid();
    private readonly string root = Path.Combine(Path.GetTempPath(), "ExHyperV-Upgrade-Test-" + Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Prepare() => Directory.CreateDirectory(root);

    [TestCleanup]
    public void Release() => Directory.Delete(root, true);

    [TestMethod]
    public void HardwareManifestSerializesBoxedCimValuesWithGeneratedMetadata()
    {
        var fields = new Dictionary<string, object?>
        {
            ["BootOrder"] = new ushort[] { 3, 0 },
            ["Memory"] = new Dictionary<string, object?> { ["Limit"] = ulong.MaxValue },
            ["Network"] = new List<Dictionary<string, object?>>
            {
                new() { ["Bytes"] = new byte[] { 255 }, ["Values"] = new object[] { (byte)255, (sbyte)-1 } }
            }
        };

        var result = JsonSerializer.SerializeToElement(fields, CoreJson.Default.FieldMap);
        Assert.AreEqual((ushort)3, result.GetProperty("BootOrder")[0].GetUInt16());
        Assert.AreEqual(ulong.MaxValue, result.GetProperty("Memory").GetProperty("Limit").GetUInt64());
        var adapter = result.GetProperty("Network")[0];
        CollectionAssert.AreEqual(new byte[] { 255 }, adapter.GetProperty("Bytes").GetBytesFromBase64());
        Assert.AreEqual((byte)255, adapter.GetProperty("Values")[0].GetByte());
        Assert.AreEqual((sbyte)-1, adapter.GetProperty("Values")[1].GetSByte());
    }

    [TestMethod]
    public async Task PublicEntryRejectsInvalidIdentityAndArmHostBeforeOpeningJournal()
    {
        var result = await VmGenerationUpgradeService.UpgradeToGeneration2Async(Guid.Empty);
        Assert.AreEqual(VmGenerationUpgradeStatus.Rejected, result.Status);
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            result = await VmGenerationUpgradeService.UpgradeToGeneration2Async(id);
            Assert.AreEqual(id, result.OldVmId);
            Assert.AreEqual(VmGenerationUpgradeStatus.Rejected, result.Status);
            Assert.IsEmpty(result.RetainedPaths);
            Assert.IsNull(result.NewVmId);
        }
    }

    private UpgradeDrive Drive(string file, string controller = "IDE", int number = 0, int location = 0, bool dvd = false) =>
        new(controller, number, location, dvd, Path.Combine(root, file), false);

    [TestMethod]
    public void PlansEachVhdRejectsConflictsAndLeavesVhdxInPlace()
    {
        var ide = Drive("boot.VHD");
        var scsi = Drive("data.vhd", "SCSI", 1, 42);
        var existing = Drive("keep.vhdx", "SCSI", 0, 0);
        var optical = Drive("setup.iso", "IDE", 1, 0, true);
        UpgradeDrive[] drives = [existing, scsi, optical, ide];
        var plan = WindowsUpgradeHost.PlanConversions(drives, id, new());
        CollectionAssert.AreEquivalent(new[] { scsi, ide }, plan.Select(c => c.Disk).ToArray());
        var destinations = plan.ToDictionary(c => c.Disk.Path!, c => c.Destination);
        var mapped = WindowsUpgradeHost.Slots(drives, [1, 2, 3], destinations);
        Assert.AreEqual((0, 0, Path.ChangeExtension(ide.Path, ".vhdx")), (mapped[0].Number, mapped[0].Location, mapped[0].Path));
        Assert.IsTrue(mapped.Any(d => d.Number == 1 && d.Location == 42 && d.Path == Path.ChangeExtension(scsi.Path, ".vhdx")));
        Assert.IsTrue(mapped.Any(d => d.Path == existing.Path));
        Assert.IsTrue(mapped.All(d => d.Controller == "SCSI"));
        Assert.AreEqual(4, mapped.Select(d => (d.Number, d.Location)).Distinct().Count());
        foreach (var invalid in new[] { ide with { Shared = true }, ide with { Path = null }, ide with { Path = "child.avhdx" } })
            Assert.ThrowsExactly<InvalidDataException>(() => WindowsUpgradeHost.PlanConversions([invalid], id, new()));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsUpgradeHost.PlanConversions(drives, id, new() { ConvertVhdToVhdx = false }));
        File.WriteAllText(destinations[scsi.Path!], "already owned");
        Assert.ThrowsExactly<IOException>(() => WindowsUpgradeHost.PlanConversions(drives, id, new()));
        Assert.AreEqual("already owned", File.ReadAllText(destinations[scsi.Path!]));
    }

    [TestMethod]
    public void MapsFirmwareOrderForEmptyOpticalScsiAndMixedMachines()
    {
        var paths = new Dictionary<string, string>();
        Assert.IsEmpty(WindowsUpgradeHost.Slots([], [], paths));
        Assert.IsEmpty(UpgradeHardware.MapBootOrder([], [], []));
        var dvd = Drive("os.iso", "IDE", 1, 0, true);
        var optical = WindowsUpgradeHost.Slots([dvd], [1, 2], paths).Single();
        Assert.AreEqual((true, 0, 0, dvd.Path), (optical.Dvd, optical.Number, optical.Location, optical.Path));
        var disk = Drive("data.vhdx", "SCSI", 1, 22);
        Assert.AreEqual(disk.Path, WindowsUpgradeHost.Slots([disk], [2], paths).Single().Path);
        var slots = WindowsUpgradeHost.Slots([dvd, disk], [1, 2, 3], paths);
        (string, string)[] sources = [("pxe", "MAC(123)"), ("disk", "Scsi(0,0)"), ("dvd", "Scsi(0,1)")];
        CollectionAssert.AreEqual(new[] { "dvd", "disk", "pxe" }, UpgradeHardware.MapBootOrder([1, 2, 3], slots, sources));
        CollectionAssert.AreEqual(new[] { "pxe", "dvd", "disk" }, UpgradeHardware.MapBootOrder([3, 1, 2], slots, sources));
        CollectionAssert.AreEqual(new[] { "legacy", "other" }, UpgradeHardware.MapBootOrder([3], [],
            [("other", "MAC(001122334455,0)"), ("legacy", "MAC(abcdef001122,0)")], ["ABCDEF001122"]));
        CollectionAssert.AreEqual(new[] { "disk", "pxe", "dvd" }, UpgradeHardware.MapBootOrder([2, 3, 1], slots, sources));
    }

    [TestMethod]
    public void SecureBootIsOptInWithAnOptionalTemplate()
    {
        var defaults = new VmGenerationUpgradeOptions();
        Assert.IsFalse(defaults.EnableSecureBoot);
        Assert.IsTrue(defaults.ConvertVhdToVhdx);
        Assert.AreEqual(new Guid("1734c6e8-3154-4dda-ba5f-a874cc483422"), UpgradeHardware.SecureBootTemplate(new() { EnableSecureBoot = true }));
        var options = new VmGenerationUpgradeOptions { EnableSecureBoot = true, SecureBootTemplateId = id };
        Assert.IsEmpty(WindowsUpgradeHost.PlanConversions([], id, options));
        Assert.AreEqual(id, UpgradeHardware.SecureBootTemplate(options));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsUpgradeHost.PlanConversions([], id, new() { SecureBootTemplateId = id }));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ReconcilesPartialPublicationWithoutTouchingOriginals(int published)
    {
        var drives = new[] { Drive("boot.vhd"), Drive("data.vhd", "SCSI", 0, 4), Drive("keep.vhdx", "SCSI", 1, 3) };
        for (int i = 0; i < drives.Length; i++)
        {
            string path = drives[i].Path!;
            File.WriteAllText(path, "original" + i);
            drives[i] = drives[i] with { Identity = HostDiskResource.Image(path), Length = new FileInfo(path).Length,
                WriteTime = File.GetLastWriteTimeUtc(path).Ticks };
        }
        var j = new UpgradeJournal { OldId = id, Conversions = WindowsUpgradeHost.PlanConversions(drives, id, new()) };
        static Task Check(string path, string expected) { Assert.AreEqual(expected, HostDiskResource.Image(path)); return Task.CompletedTask; }
        foreach (var c in j.Conversions)
        {
            Directory.CreateDirectory(c.DirectoryPath);
            File.WriteAllText(c.Work, "converted");
            c.Identity = HostDiskResource.Image(c.Work);
        }
        foreach (var c in j.Conversions.Take(published)) await WindowsUpgradeHost.PublishDiskAsync(c, Check);
        if (published == 1) File.WriteAllText(j.Conversions[1].Destination, "foreign");
        await WindowsUpgradeHost.UndoDiskMovesAsync(j, Check);
        await WindowsUpgradeHost.UndoDiskMovesAsync(j, Check);
        foreach (var c in j.Conversions) Assert.AreEqual(c.Identity, HostDiskResource.Image(c.Work));
        if (published == 1) Assert.AreEqual("foreign", File.ReadAllText(j.Conversions[1].Destination));
        foreach (var drive in drives) Assert.AreEqual(drive.Identity, HostDiskResource.Image(drive.Path!));
    }

    [TestMethod]
    public async Task FaultAtEveryStepKeepsOriginalOrRollsBackBeforeCleanup()
    {
        foreach (var step in Enum.GetValues<UpgradeStep>().Where(s => s <= UpgradeStep.VerifyFinal))
        {
            var backend = new Host { Fault = step };
            Guid original = Guid.NewGuid();
            var result = await GenerationUpgradeTransaction.RunAsync(original, root, backend, default);
            Assert.AreEqual(step >= UpgradeStep.CreateCandidate ? VmGenerationUpgradeStatus.RolledBack :
                VmGenerationUpgradeStatus.Rejected, result.Status, step.ToString());
            Assert.AreEqual(step.ToString(), result.FailedStage);
            Assert.IsTrue(backend.OriginalPresent && !backend.CandidatePresent);
            Assert.AreEqual(0, backend.Cleanups);
            Assert.IsTrue(result.RetainedPaths.Any(Directory.Exists));
        }
    }

    [TestMethod]
    public async Task UnknownNativeMutationReconcilesBeforeCompensation()
    {
        var host = new Host { Fault = UpgradeStep.DestroySource, Unknown = true };
        var first = await Run(host);
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, first.Status);
        Assert.IsFalse(host.OriginalPresent);
        Assert.AreEqual(0, host.Rollbacks);
        host.FailReconcile = true;
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, (await Run(host)).Status);
        Assert.AreEqual(0, host.Rollbacks);
        host.FailReconcile = false;
        Assert.AreEqual(VmGenerationUpgradeStatus.RolledBack, (await Run(host)).Status);
        Assert.AreEqual(2, host.Reconciles);
        Assert.IsTrue(host.OriginalPresent && !host.CandidatePresent);
    }

    [TestMethod]
    public async Task CoreUnknownFromGroupWriteKeepsStageWithoutRollbackOrCleanup()
    {
        var host = new Host { Fault = UpgradeStep.RestoreDevices, CoreUnknown = true };
        var result = await Run(host);
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, result.Status);
        var journal = UpgradeJournal.Read(Path.Combine(root, id.ToString("D")), id);
        Assert.AreEqual(UpgradeStep.RestoreDevices, journal.Step);
        Assert.IsTrue(journal.InFlight);
        Assert.AreEqual(0, host.Rollbacks);
        Assert.AreEqual(0, host.Cleanups);
    }

    [TestMethod]
    public async Task UnknownCleanupRequiresSuccessfulReconciliationBeforeAnotherWrite()
    {
        var host = new Host { CleanupUnknown = true };
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, (await Run(host)).Status);
        host.FailReconcile = true;
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, (await Run(host)).Status);
        Assert.AreEqual(1, host.Cleanups);
        Assert.AreEqual(0, host.Rollbacks);
    }

    [TestMethod]
    public async Task ShutdownCancellationAfterSubmissionIsUnknown()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var wait = WindowsUpgradeHost.WaitForShutdownAsync(() => Task.FromResult<int?>(2),
            () => { accepted.SetResult(); return pending.Task; }, cancel.Token, TimeSpan.FromMinutes(1));
        await accepted.Task;
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<UpgradeUncertainException>(() => wait.WaitAsync(TimeSpan.FromSeconds(3)));
        pending.SetResult();
        int calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsUpgradeHost.WaitForShutdownAsync(
            () => Task.FromResult<int?>(2), () => { calls++; return Task.CompletedTask; }, cancel.Token, TimeSpan.FromSeconds(1)));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task WorkspaceIntentWithoutIdentityRetainsDirectoryAndWorkDisk()
    {
        var drive = Drive("source.vhd");
        var conversion = WindowsUpgradeHost.PlanConversions([drive], id, new()).Single();
        Directory.CreateDirectory(conversion.DirectoryPath);
        File.WriteAllText(conversion.Work, "retain");
        conversion.DirectoryIntent = true;
        var journal = new UpgradeJournal { OldId = id, Conversions = [conversion] };
        await Assert.ThrowsExactlyAsync<IOException>(() => WindowsUpgradeHost.RemoveWork(journal));
        Assert.AreEqual("retain", File.ReadAllText(conversion.Work));
        conversion.DirectoryOwned = true;
        using (var directory = HostDiskResource.PinDirectory(conversion.DirectoryPath))
            conversion.DirectoryIdentity = HostDiskResource.Image(directory);
        await Assert.ThrowsExactlyAsync<IOException>(() => WindowsUpgradeHost.RemoveWork(journal));
        Assert.IsTrue(File.Exists(conversion.Work));
    }

    [TestMethod]
    public void RecoveryCleanupPreservesFilesOutsideTheReceipt()
    {
        var journal = new UpgradeJournal { OldId = id, DirectoryPath = root };
        Directory.CreateDirectory(journal.Artifacts);
        using (var pin = HostDiskResource.PinDirectory(journal.Artifacts))
            journal.ArtifactIdentity = HostDiskResource.Image(pin);
        string known = Path.Combine(journal.Artifacts, "known.vmcx");
        string foreign = Path.Combine(journal.Artifacts, "retain.txt");
        File.WriteAllText(known, "owned");
        File.WriteAllText(foreign, "foreign");
        journal.RecoveryFiles[known] = HostDiskResource.Image(known);
        Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() => WindowsUpgradeHost.RemoveRecoveryTree(journal));
        Assert.IsFalse(File.Exists(known));
        Assert.AreEqual("foreign", File.ReadAllText(foreign));
        File.Delete(foreign);
        WindowsUpgradeHost.RemoveRecoveryTree(journal);
        Assert.IsFalse(Directory.Exists(journal.Artifacts));
    }

    [TestMethod]
    public async Task JournalPersistsOnlyThisTransactionsOutstandingNativeReceipts()
    {
        var j = new UpgradeJournal { OldId = id, DirectoryPath = root, TracksOperations = true };
        using (j.CaptureOperations())
        {
            var operation = WmiOperationCapture.Start("Modify", "target", WmiScope.HyperV, WmiContext.Local)!;
            operation.Job = "job-1";
            operation.Complete(WmiJobWaiter.Unknown("pending"));
            var pending = UpgradeJournal.Read(root, id).PendingOperations.Single().Value;
            Assert.AreEqual("job-1", pending.Job);
            operation.Complete(ApiResponse.Ok());
        }
        var loaded = UpgradeJournal.Read(root, id);
        Assert.IsEmpty(loaded.PendingOperations);
        await UpgradeHardware.ReconcileOperationsAsync(loaded);
    }

    [TestMethod]
    public async Task DurableCommitOnlyRetriesCleanupAndCompletedReceiptIsIdempotent()
    {
        var backend = new Host { FailCleanup = true };
        var pending = await Run(backend);
        Assert.AreEqual(VmGenerationUpgradeStatus.CommittedCleanupPending, pending.Status);
        Assert.IsTrue(UpgradeJournal.Read(Path.Combine(root, id.ToString("D")), id).Committed);
        backend.FailCleanup = false;
        var done = await Run(backend);
        Assert.AreEqual(VmGenerationUpgradeStatus.Succeeded, done.Status);
        Assert.AreEqual(pending.NewVmId, done.NewVmId);
        Assert.IsEmpty(done.RetainedPaths);
        Assert.AreEqual(done.NewVmId, (await Run(backend)).NewVmId);
        Assert.AreEqual(2, backend.Cleanups);
        Assert.AreEqual(0, backend.Rollbacks);
        Assert.AreEqual(1, backend.Executed.Count(s => s == UpgradeStep.DestroySource));
    }

    [TestMethod]
    public async Task LockedOrCorruptJournalNeverStartsAnotherUpgrade()
    {
        string dir = Directory.CreateDirectory(Path.Combine(root, id.ToString("D"))).FullName;
        var host = new Host();
        using (File.Create(Path.Combine(dir, "lock")))
            Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, (await Run(host)).Status);
        File.WriteAllText(Path.Combine(dir, "transaction.json"), "{");
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, (await Run(host)).Status);
        Assert.IsEmpty(host.Executed);
    }

    [TestMethod]
    public async Task ShutdownRequiresObservedOffAndRejectsSavedOrSuspendedStates()
    {
        int requests = 0;
        Task Stop() { requests++; return Task.CompletedTask; }
        var sequence = new Queue<int?>([2, 3]);
        await WindowsUpgradeHost.WaitForShutdownAsync(() => Task.FromResult(sequence.Dequeue()), Stop, default, TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, requests);
        foreach (int? state in new int?[] { null, 6, 9, 32768, 32769, 32779 })
            await Assert.ThrowsExactlyAsync<IOException>(() => WindowsUpgradeHost.WaitForShutdownAsync(
                () => Task.FromResult(state), Stop, default, TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, requests);
        await WindowsUpgradeHost.WaitForShutdownAsync(() => Task.FromResult<int?>(3), Stop, default, TimeSpan.Zero);
        Assert.AreEqual(1, requests);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => WindowsUpgradeHost.WaitForShutdownAsync(
            () => Task.FromResult<int?>(2), Stop, default, TimeSpan.Zero));
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public void CheckpointAndUncertainDependencyEvidenceBlockCutover()
    {
        WindowsUpgradeHost.ValidateCheckpoints([new() {NodeType=CheckpointNodeType.Current}]);
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsUpgradeHost.ValidateCheckpoints([new() {NodeType=CheckpointNodeType.Snapshot}]));
        Assert.ThrowsExactly<InvalidDataException>(() => WindowsUpgradeHost.ValidateCheckpoints([new() {NodeType=CheckpointNodeType.Current, IsWormhole=true}]));
        var source = new UpgradeSource("test", root, "10.0", [new("IDE",0,0,false,"disk.vhdx",false)],
            JsonSerializer.SerializeToElement(new {}), [], new());
        var plan = new VmDeleteService.PurgePreview(root,false,[],["disk.vhdx"]);
        WindowsUpgradeHost.ValidateDiskInventory(source, plan);
        foreach (var invalid in new[] { plan with { DiskDependencyIssues = ["unknown parent"] },
            plan with { RetainedDiskFiles = ["disk.vhdx"] }, plan with { DiskFiles = [] } })
            Assert.ThrowsExactly<IOException>(() => WindowsUpgradeHost.ValidateDiskInventory(source, invalid));
    }

    [TestMethod]
    public async Task CleanupRetriesOnlyRemainingOriginalVhds()
    {
        var disks = new[] { Drive("a.vhd"), Drive("b.vhd", "SCSI", 0, 1) };
        for (int index = 0; index < disks.Length; index++)
        {
            string path = disks[index].Path!;
            File.WriteAllText(path, "old");
            disks[index] = disks[index] with { Identity = HostDiskResource.Image(path), Length = 3,
                WriteTime = File.GetLastWriteTimeUtc(path).Ticks };
        }
        var conversions = WindowsUpgradeHost.PlanConversions(disks, id, new());
        foreach (var c in conversions) File.WriteAllText(c.Destination, "final");
        await Assert.ThrowsExactlyAsync<IOException>(() => WindowsUpgradeHost.RemoveOriginalsAsync(conversions,
            (path, _) => path == disks[1].Path ? throw new IOException("locked") : Task.CompletedTask));
        Assert.IsFalse(File.Exists(disks[0].Path));
        Assert.IsTrue(File.Exists(disks[1].Path));
        await WindowsUpgradeHost.RemoveOriginalsAsync(conversions, (_, _) => Task.CompletedTask);
        foreach (var c in conversions)
        {
            Assert.IsFalse(File.Exists(c.Disk.Path));
            Assert.AreEqual("final", File.ReadAllText(c.Destination));
        }
    }

    [TestMethod]
    public void PreviousCompletedReceiptCanBeReadAndActiveLegacyRecoveryIsPreserved()
    {
        var path = Path.Combine(root, "transaction.json");
        string original = Path.Combine(root, "system.vhd");
        var receipt = new { Version = 1, OldId = id, TransactionId = Guid.NewGuid(), NewId = Guid.NewGuid(),
            CandidateCreated = true, SourceRemoved = true, Committed = true, Step = "Complete", Original = original,
            WorkIdentity = "system-file-id", DiskPaths = new[] { Path.ChangeExtension(original, ".vhdx") } };
        File.WriteAllText(path, JsonSerializer.Serialize(receipt));
        var j = UpgradeJournal.Read(root, id);
        Assert.AreEqual(receipt.WorkIdentity, j.DiskIdentities[receipt.DiskPaths[0]]);
        string active = JsonSerializer.Serialize(receipt with { Step = "TrialBoot", Committed = false });
        File.WriteAllText(path, active);
        Assert.ThrowsExactly<InvalidDataException>(() => UpgradeJournal.Read(root, id));
        Assert.AreEqual(active, File.ReadAllText(path));
    }

    [TestMethod]
    public async Task DefaultGuestStagesNeedNeitherVmNorDisk()
    {
        using var host = new WindowsUpgradeHost();
        var journal = new UpgradeJournal { OldId = id };
        await host.ExecuteAsync(UpgradeStep.PrepareGuest, journal, default);
        await host.ExecuteAsync(UpgradeStep.ValidateGuest, journal, default);
    }

    [TestMethod]
    public async Task InterruptedRollbackResumesWithJournaledOptions()
    {
        var host = new Host { Fault = UpgradeStep.PublishStorage, FailRollback = true };
        var first = await GenerationUpgradeTransaction.RunAsync(id, root, host, default,
            new() { EnableSecureBoot = true, SecureBootTemplateId = id });
        Assert.AreEqual(VmGenerationUpgradeStatus.OutcomeUnknown, first.Status);
        var journal = UpgradeJournal.Read(Path.Combine(root, id.ToString("D")), id);
        Assert.IsTrue(journal.Options.EnableSecureBoot);
        Assert.AreEqual(id, journal.Options.SecureBootTemplateId);
        host.FailRollback = false;
        Assert.AreEqual(VmGenerationUpgradeStatus.RolledBack, (await Run(host)).Status);
        Assert.IsTrue(host.OriginalPresent && !host.CandidatePresent);
    }

    [TestMethod]
    public async Task CancellationBeforeFirstMutationReturnsStructuredRejection()
    {
        var host = new Host();
        var result = await GenerationUpgradeTransaction.RunAsync(id, root, host, new CancellationToken(true));
        Assert.AreEqual(VmGenerationUpgradeStatus.Rejected, result.Status);
        Assert.IsEmpty(host.Executed);
        Assert.IsTrue(host.OriginalPresent);
    }

    private Task<VmGenerationUpgradeResult> Run(Host host) => GenerationUpgradeTransaction.RunAsync(id, root, host, default);

    private sealed class Host : IGenerationUpgradeHost
    {
        internal UpgradeStep? Fault;
        internal bool CoreUnknown, CleanupUnknown, Unknown, FailReconcile, FailCleanup, FailRollback, CandidatePresent;
        internal bool OriginalPresent = true;
        internal int Rollbacks, Cleanups, Reconciles;
        internal List<UpgradeStep> Executed { get; } = [];

        public Task ExecuteAsync(UpgradeStep step, UpgradeJournal j, CancellationToken token)
        {
            var durable = UpgradeJournal.Read(j.DirectoryPath, j.OldId);
            Assert.IsTrue(durable.InFlight && durable.Step == step);
            Executed.Add(step);
            if (step == UpgradeStep.CreateCandidate) { j.NewId = Guid.NewGuid(); CandidatePresent = j.CandidateCreated = true; }
            if (step == UpgradeStep.DestroySource) { OriginalPresent = false; j.SourceRemoved = true; }
            if (Fault == step && CoreUnknown) throw new OperationOutcomeUnknownException(WmiJobWaiter.Unknown("group pending"));
            if (Fault == step) throw Unknown ? new UpgradeUncertainException("job") : new IOException("fail");
            return Task.CompletedTask;
        }
        public Task ReconcileAsync(UpgradeJournal j)
        {
            Reconciles++;
            if (FailReconcile) throw new IOException("unconfirmed");
            return Task.CompletedTask;
        }
        public Task RollbackAsync(UpgradeJournal j)
        {
            Assert.IsFalse(j.Committed);
            if (FailRollback) throw new IOException("interrupted");
            OriginalPresent = true;
            CandidatePresent = false;
            Rollbacks++;
            return Task.CompletedTask;
        }
        public Task VerifyReceiptAsync(UpgradeJournal j)
        {
            Assert.IsFalse(OriginalPresent);
            Assert.IsTrue(CandidatePresent);
            return Task.CompletedTask;
        }
        public Task CleanupAsync(UpgradeJournal j)
        {
            Assert.IsTrue(UpgradeJournal.Read(j.DirectoryPath, j.OldId).Committed);
            Cleanups++;
            if (CleanupUnknown) throw new OperationOutcomeUnknownException(WmiJobWaiter.Unknown("cleanup pending"));
            if (FailCleanup) throw new IOException("busy");
            return Task.CompletedTask;
        }
    }
}
