using System.Text.Json;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed partial class WindowsUpgradeHost
{
    private static string? Identity(string path)
    {
        var id = VmPurgeDiskProtection.IdentifyFile(path);
        if (!id.Success) throw new IOException(id.Error);
        return id.Data;
    }

    private static void VerifyOriginal(UpgradeDrive disk)
    {
        string path = disk.Path!;
        if (Identity(path) != disk.Identity || new FileInfo(path).Length != disk.Length ||
            File.GetLastWriteTimeUtc(path).Ticks != disk.WriteTime) throw new IOException("Original disk changed: " + path);
    }

    private static async Task Unreferenced(string path, string expected)
    {
        var registered = Read(await VmDeleteService.ReadRegisteredDiskPathsAsync());
        var plan = await VmPurgeDiskProtection.EvaluateAsync([path], registered, HostVirtualDiskMetadata.ReadParentPathAsync,
            VmPurgeDiskProtection.IdentifyFile);
        if (plan.Issues.Count != 0 || plan.Deletable.Count != 1 || plan.Deletable[0].Identity != expected)
            throw new IOException("Cannot prove exclusive disk ownership: " + path);
        var image = Read(await HostDiskService.ReadImageAsync(path));
        if (image.Attached) throw new IOException("Image is attached: " + path);
    }

    internal static async Task PublishDiskAsync(UpgradeConversion conversion, Func<string, string, Task> verifyExclusive)
    {
        string identity = conversion.Identity ?? throw new InvalidDataException("Missing converted disk identity.");
        VerifyOriginal(conversion.Disk);
        await verifyExclusive(conversion.Work, identity);
        OwnedFileOperations.MoveOrDelete(conversion.Work, identity, conversion.Destination);
    }

    public async Task ReconcileAsync(UpgradeJournal j)
    {
        await UpgradeHardware.ReconcileOperationsAsync(j);
        if (j.Committed)
        {
            await VerifyReceiptAsync(j);
            return;
        }
        if (j.ShutdownRequested && await State(j.OldId) != 3)
            throw new UpgradeUncertainException("Guest shutdown has no confirmed final state.");
        if (j.InFlight && j.Step == UpgradeStep.CreateCandidate && !j.NewId.HasValue)
        {
            var registrations = Read(await WmiApi.QueryAsync("SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType='Microsoft:Hyper-V:System:Realized'",
                row => new { Id = row["VirtualSystemIdentifier"] as string, Root = row["ConfigurationDataRoot"] as string,
                    Name = row["ElementName"] as string, Notes = row["Notes"] as string[], Type = row["VirtualSystemSubType"] as string }));
            var matches = registrations.Where(r => r.Name == "ExHyperV-" + j.TransactionId.ToString("N")).ToArray();
            if (matches.Length != 0)
            {
                var r = matches.Single();
                if (!Paths.Equals(r.Root, j.Source!.Root) || r.Type != "Microsoft:Hyper-V:SubType:2" ||
                    r.Notes?.SingleOrDefault() != "ExHyperV.GenerationUpgrade/" + j.TransactionId.ToString("D") ||
                    !Guid.TryParse(r.Id, out Guid id) || id == j.OldId || id == Guid.Empty)
                    throw new UpgradeUncertainException("Unconfirmed DefineSystem identity.");
                j.NewId = id;
                j.CandidateCreated = true;
            }
        }
        if (j.Source != null && await State(j.OldId) == null)
        {
            if (!j.DestroyAuthorized)
                throw new UpgradeUncertainException("Original registration disappeared before cutover.");
            j.SourceRemoved = true;
        }
        if (j.Step == UpgradeStep.VerifyFinal && j.SourceRemoved)
        {
            // This boundary is read-only. Repeat the entire final readback and persist the
            // commitment before cleanup, even if the previous process died after readback.
            try
            {
                await VerifyFinal(j);
                j.Committed = true;
                j.InFlight = false;
            }
            catch (IOException error) when (error is not UpgradeUncertainException)
            {
                j.FailureStage ??= j.Step.ToString();
                j.Error = error.Message;
            }
        }
        j.Save();
    }

    public async Task RollbackAsync(UpgradeJournal j)
    {
        Dispose();
        if (j.Source == null) return;
        if (j.NewId.HasValue && await State(j.Candidate) is { } candidateState)
        {
            if (!j.CandidateCreated) throw new UpgradeUncertainException("Candidate ownership unknown.");
            var items = Read(await VmMetadataService.ReadAsync(j.Candidate));
            if (!items.TryGetValue(j.OwnershipKey, out string? owner) || owner != j.OldId.ToString("D"))
                await UpgradeHardware.CheckCreationMarkerAsync(j.Candidate, j.Source.Root, j.TransactionId.ToString("D"),
                    "ExHyperV-" + j.TransactionId.ToString("N"));
            if (candidateState != 3) throw new UpgradeUncertainException("Replacement VM is no longer off.");
            await Destroy(j.Candidate, "ExHyperV-" + j.TransactionId);
        }
        if (j.SourceRemoved || await State(j.OldId) == null)
        {
            if (!j.DestroyAuthorized || j.RecoveryConfiguration == null) throw new UpgradeUncertainException("Original registration is missing.");
            await UndoDiskMovesAsync(j, Unreferenced);
            foreach (var d in Disks(j.Source)) VerifyOriginal(d);
            if (await State(j.OldId) == null)
            {
                await using var session = await OpenRecovery(j);
                var imported = await VmImportService.RegisterUpgradeRecoveryAsync(session, j.Source);
                if (!imported.Success || !imported.IsRealized || imported.OperationStateUnknown || imported.VmId != j.OldId)
                    throw new UpgradeUncertainException(imported.Error ?? "Original registration could not be verified.");
            }
            await RestoreGroups(j.OldId, j.Source.Groups);
            await VerifySource(j);
        }
        else
        {
            await RequireSourceGeneration(j.OldId);
            if (j.CandidateCreated) await VerifySource(j);
            foreach (var d in Disks(j.Source))
                if (Identity(d.Path!) != d.Identity) throw new IOException("Original disk identity changed.");
        }
        await RemoveWork(j);
    }

    internal static async Task UndoDiskMovesAsync(UpgradeJournal j, Func<string, string, Task> verifyExclusive)
    {
        foreach (var conversion in j.Conversions)
        {
            if (conversion.Identity is { } expected && Identity(conversion.Destination) == expected)
            {
                await verifyExclusive(conversion.Destination, expected);
                OwnedFileOperations.MoveOrDelete(conversion.Destination, expected, conversion.Work);
            }
            VerifyOriginal(conversion.Disk);
        }
    }

    public async Task VerifyReceiptAsync(UpgradeJournal j)
    {
        if (await State(j.OldId) != null) throw new UpgradeUncertainException("Original VM is registered.");
        if (j.Version == 2 && !j.DiskPaths.ToHashSet(Paths).SetEquals(j.DiskIdentities.Keys))
            throw new UpgradeUncertainException("Committed disk inventory is incomplete.");
        foreach (var pair in j.DiskIdentities)
            if (Identity(pair.Key) != pair.Value)
                throw new UpgradeUncertainException("Committed disk identity changed: " + pair.Key);
        await UpgradeHardware.ReceiptAsync(j.Candidate, j.Source!.Name, j.Source.Root, j.DiskPaths);
    }

    public async Task CleanupAsync(UpgradeJournal j)
    {
        if (!j.Committed) throw new InvalidOperationException("Missing durable commitment.");
        try { await VerifyReceiptAsync(j); }
        catch (Exception e) { throw new UpgradeReadbackException(e.Message); }
        foreach (var config in j.OriginalConfiguration)
            if (Identity(config.Path) != null) OwnedFileOperations.MoveOrDelete(config.Path, config.Identity, null);
        await RemoveOriginalsAsync(j.Conversions, Unreferenced);
        await RemoveWork(j);
        var markers = await VmMetadataService.ReadAsync(j.Candidate);
        if (!markers.HasData) throw new UpgradeReadbackException(markers.Error);
        if (markers.Data.TryGetValue(j.OwnershipKey, out string? owner))
        {
            if (owner != j.OldId.ToString("D")) throw new IOException("Upgrade ownership marker changed.");
            j.RemovingOwnershipMarker = true;
            j.Save();
            Require(await VmMetadataService.RemoveAsync(j.Candidate, j.OwnershipKey));
        }
        j.RemovingOwnershipMarker = false;
        j.Save();
        RemoveRecoveryTree(j);
    }

    internal static async Task RemoveOriginalsAsync(UpgradeConversion[] conversions, Func<string, string, Task> verifyExclusive)
    {
        foreach (var conversion in conversions)
        {
            string old = conversion.Disk.Path!;
            if (Identity(old) != null)
            {
                VerifyOriginal(conversion.Disk);
                await verifyExclusive(old, conversion.Disk.Identity!);
                OwnedFileOperations.MoveOrDelete(old, conversion.Disk.Identity!, null);
            }
            foreach (var pair in conversion.TrackingFiles)
                if (Identity(old + pair.Key) != null) OwnedFileOperations.MoveOrDelete(old + pair.Key, pair.Value, null);
        }
    }

    internal static async Task RemoveWork(UpgradeJournal j)
    {
        foreach (var conversion in j.Conversions)
        {
            if (!conversion.DirectoryIntent || !DirectoryPresent(conversion.DirectoryPath)) continue;
            if (!conversion.DirectoryOwned || conversion.DirectoryIdentity == null)
                throw new IOException("Missing workspace creation receipt: " + conversion.DirectoryPath);
            using var directory = HostDiskResource.PinDirectory(conversion.DirectoryPath);
            if (HostDiskResource.Image(directory) != conversion.DirectoryIdentity)
                throw new IOException("Work directory replaced: " + conversion.DirectoryPath);
            if (Identity(conversion.Work) is { } identity)
            {
                if (conversion.Identity == null || identity != conversion.Identity)
                    throw new IOException("Work disk has no matching output receipt: " + conversion.Work);
                await Unreferenced(conversion.Work, identity);
                OwnedFileOperations.MoveOrDelete(conversion.Work, identity, null);
            }
            OwnedFileOperations.DeleteEmptyDirectory(directory);
        }
    }

    private static bool DirectoryPresent(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException("Expected a directory: " + path);
            return true;
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
    }

    // The export was directed to a new private directory. Record its output identities
    // immediately after a successful receipt, and never widen this inventory on retry.
    private static void CaptureRecoveryTree(string root, UpgradeJournal journal)
    {
        string config = journal.RecoveryConfiguration ?? throw new InvalidDataException("Missing exported configuration.");
        string vmDirectory = Path.GetDirectoryName(config)!;
        string export = Path.GetDirectoryName(vmDirectory)!;
        if (!Path.GetFullPath(config).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(Path.GetFileNameWithoutExtension(config), out var id) || id != journal.OldId)
            throw new IOException("Recovery export is outside the owned workspace or has a different identity.");
        for (string? dir = vmDirectory; dir != null && !Paths.Equals(dir, root); dir = Path.GetDirectoryName(dir))
            RememberDirectory(dir);
        foreach (string name in new[] { "Snapshots", "Virtual Hard Disks" })
        {
            string dir = Path.Combine(export, name);
            if (DirectoryPresent(dir)) RememberDirectory(dir);
        }
        foreach (string extension in new[] { ".vmcx", ".vmrs", ".vmgs" })
        {
            string file = Path.ChangeExtension(config, extension);
            if (Identity(file) is { } identity)
            {
                VmPurgeFileSafety.EnsureNoReparse(file);
                journal.RecoveryFiles[file] = identity;
            }
        }

        void RememberDirectory(string directory)
        {
            using var pin = HostDiskResource.PinDirectory(directory);
            journal.RecoveryDirectories[directory] = HostDiskResource.Image(pin);
        }
    }

    internal static void RemoveRecoveryTree(UpgradeJournal journal)
    {
        if (!DirectoryPresent(journal.Artifacts)) return;
        using var root = HostDiskResource.PinDirectory(journal.Artifacts);
        if (journal.ArtifactIdentity == null || HostDiskResource.Image(root) != journal.ArtifactIdentity)
            throw new IOException("Missing recovery workspace identity: " + journal.Artifacts);
        foreach (var (path, identity) in journal.RecoveryFiles)
        {
            if (!Inside(path)) throw new InvalidDataException(path);
            if (Identity(path) != null) OwnedFileOperations.MoveOrDelete(path, identity, null);
        }
        foreach (var (path, identity) in journal.RecoveryDirectories.OrderByDescending(p => p.Key.Length))
        {
            if (!Inside(path)) throw new InvalidDataException(path);
            if (!DirectoryPresent(path)) continue;
            using var directory = HostDiskResource.PinDirectory(path);
            if (HostDiskResource.Image(directory) != identity) throw new IOException("Recovery directory replaced: " + path);
            OwnedFileOperations.DeleteEmptyDirectory(directory);
        }
        OwnedFileOperations.DeleteEmptyDirectory(root);

        bool Inside(string path) => Path.GetFullPath(path).StartsWith(journal.Artifacts + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
