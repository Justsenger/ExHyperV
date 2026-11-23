using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// Future opt-in guest preparation must use journaled work images even for VHDX inputs.
// The current VM-only service supplies no stages and never opens a guest volume.
internal interface IUpgradeGuestStages
{
    Task PrepareAsync(UpgradeJournal journal, CancellationToken token);
    Task ValidateAsync(UpgradeJournal journal, CancellationToken token);
}

internal sealed partial class WindowsUpgradeHost(IUpgradeGuestStages? guest = null) : IGenerationUpgradeHost, IDisposable
{
    private readonly List<SafeFileHandle> reusedImageLocks = [];

    public void Dispose()
    {
        foreach (var handle in reusedImageLocks) handle.Dispose();
        reusedImageLocks.Clear();
    }

    internal static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static IEnumerable<UpgradeDrive> Disks(UpgradeSource source) => source.Drives.Where(d => !d.Dvd);
    private static T Read<T>(ApiResponse<T> response)
    {
        if (response.HasData) return response.Data!;
        OperationOutcomeUnknownException.ThrowIfUnknown(response);
        throw new IOException(response.Error);
    }
    internal static void Require(ApiResponse r)
    {
        if (r.ErrorKind == ApiErrorKind.OutcomeUnknown) throw new UpgradeUncertainException(r.Error);
        if (!r.Success) throw new IOException(r.Error);
    }

    public async Task ExecuteAsync(UpgradeStep step, UpgradeJournal j, CancellationToken token)
    {
        switch (step)
        {
            case UpgradeStep.Preflight:
                ValidateOptions(j.Options);
                await RequireSourceGeneration(j.OldId);
                await Shutdown(j, token);
                j.Source = await Capture(j);
                j.Extras = await Extras(j.OldId);
                j.Conversions = PlanConversions(j.Source.Drives, j.TransactionId, j.Options);
                j.FinalDisk = PlannedSlots(j, staged: false).FirstOrDefault(d => !d.Dvd)?.Path;
                break;
            case UpgradeStep.PrepareStorage:
                await PrepareStorage(j, token);
                break;
            case UpgradeStep.PrepareGuest:
                if (guest != null) await guest.PrepareAsync(j, token);
                break;
            case UpgradeStep.ValidateGuest:
                if (guest != null) await guest.ValidateAsync(j, token);
                break;
            case UpgradeStep.ExportRecovery:
                await ExportRecovery(j);
                break;
            case UpgradeStep.CreateCandidate:
                await CreateCandidate(j);
                Require(await VmMetadataService.SetAsync(j.Candidate, j.OwnershipKey, j.OldId.ToString("D")));
                await UpgradeHardware.ConfigureAsync(j.Candidate, j.Source!.Hardware);
                await ApplyExtras(j);
                break;
            case UpgradeStep.RecheckSource:
                await VerifySource(j);
                break;
            case UpgradeStep.DestroySource:
                await VerifySource(j);
                await DestroySource(j);
                break;
            case UpgradeStep.RestoreDevices:
                foreach (var drive in Disks(j.Source!)) VerifyOriginal(drive);
                await UpgradeHardware.FinalizeAsync(j.Candidate, j.Source!.Name, j.Source.Hardware, PlannedSlots(j, staged: true));
                foreach (var pair in j.Source!.Metadata)
                    Require(await VmMetadataService.SetAsync(j.Candidate, pair.Key, pair.Value));
                await RestoreGroups(j.Candidate, j.Source.Groups);
                break;
            case UpgradeStep.VerifyStaged:
                if (j.Conversions.Length > 0) await VerifyFinal(j, staged: true);
                break;
            case UpgradeStep.PublishStorage:
                var finalSlots = PlannedSlots(j, staged: false);
                foreach (var conversion in j.Conversions)
                {
                    token.ThrowIfCancellationRequested();
                    await UpgradeHardware.DetachMediaAsync(j.Candidate, conversion.Work);
                    await PublishDiskAsync(conversion, Unreferenced);
                    using var pin = OwnedFileOperations.PinImage(conversion.Destination, conversion.Identity!);
                    var originalAccess = new FileInfo(conversion.Disk.Path!).GetAccessControl(AccessControlSections.Access);
                    var finalAccess = new FileSecurity();
                    finalAccess.SetSecurityDescriptorBinaryForm(originalAccess.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    new FileInfo(conversion.Destination).SetAccessControl(finalAccess);
                    await UpgradeHardware.AttachMediaAsync(j.Candidate, finalSlots
                        .Single(d => Paths.Equals(d.Path, conversion.Destination)));
                }
                if (j.Conversions.Length > 0) await UpgradeHardware.RestoreBootOrderAsync(j.Candidate, j.Source!.Hardware, finalSlots);
                break;
            case UpgradeStep.VerifyFinal:
                await VerifyFinal(j);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    internal static UpgradeConversion[] PlanConversions(UpgradeDrive[] drives, Guid transactionId, VmGenerationUpgradeOptions options)
    {
        ValidateDrives(drives);
        ValidateOptions(options);
        var conversions = new List<UpgradeConversion>();
        var destinations = new HashSet<string>(drives.Where(d => d.Path != null).Select(d => d.Path!), Paths);
        foreach (var drive in drives.Where(d => !d.Dvd && Paths.Equals(Path.GetExtension(d.Path), ".vhd")))
        {
            string path = drive.Path!;
            string directory = Path.Combine(Path.GetDirectoryName(path)!, $".exhyperv-{transactionId:N}-{conversions.Count}");
            var conversion = new UpgradeConversion(drive, Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".vhdx"));
            if (!options.ConvertVhdToVhdx) throw new InvalidDataException("VHD conversion is disabled: " + path);
            if (!destinations.Add(conversion.Destination) || Path.Exists(conversion.Destination) || Path.Exists(directory))
                throw new IOException("VHDX destination or workspace already exists: " + conversion.Destination);
            foreach (var pair in TrackingFiles(path)) conversion.TrackingFiles.Add(pair.Key, pair.Value);
            conversions.Add(conversion);
        }
        return conversions.ToArray();
    }

    private static void ValidateOptions(VmGenerationUpgradeOptions options)
    {
        if (options.SecureBootTemplateId is Guid template && (!options.EnableSecureBoot || template == Guid.Empty))
            throw new InvalidDataException("A Secure Boot template requires enabled Secure Boot and a nonempty GUID.");
    }

    private static async Task PrepareStorage(UpgradeJournal j, CancellationToken token)
    {
        using var metadata = new VirtualDiskMetadataReader();
        foreach (var conversion in j.Conversions)
        {
            token.ThrowIfCancellationRequested();
            conversion.DirectoryIntent = true;
            j.Save();
            try { UpgradeJournal.PrivateDirectory(conversion.DirectoryPath, mustBeNew: true); }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 80 or 183)
            {
                conversion.DirectoryIntent = false;
                throw;
            }
            using (var directory = HostDiskResource.PinDirectory(conversion.DirectoryPath))
                conversion.DirectoryIdentity = HostDiskResource.Image(directory);
            conversion.DirectoryOwned = true;
            j.Save();
            using var guard = new FileStream(conversion.Disk.Path!, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyOriginal(conversion.Disk);
            var source = metadata.ReadSettings(conversion.Disk.Path!);
            await UpgradeHardware.ConvertVhdAsync(conversion.Disk.Path!, conversion.Work, source.Type, token);
            conversion.Identity = HostDiskResource.Image(conversion.Work);
            j.Save();
            if (metadata.ReadSettings(conversion.Work) != source)
                throw new IOException("Converted disk capacity or allocation type changed: " + conversion.Work);
        }
    }

    private static void ValidateDrives(UpgradeDrive[] drives)
    {
        foreach (var drive in drives)
        {
            if (drive.Controller is not ("IDE" or "SCSI") || drive.Number < 0 || drive.Location < 0 ||
                (drive.Controller == "IDE" && (drive.Number > 1 || drive.Location > 1)) ||
                (drive.Controller == "SCSI" && (drive.Number > 3 || drive.Location > 63)) ||
                drive.Shared || (!drive.Dvd && (string.IsNullOrWhiteSpace(drive.Path) ||
                    !new[] { ".vhd", ".vhdx" }.Contains(Path.GetExtension(drive.Path), Paths))))
                throw new InvalidDataException("Unsupported storage attachment: " + drive.Path);
        }
        if (drives.GroupBy(d => (d.Controller, d.Number, d.Location)).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate storage slot.");
        if (drives.Length > 256) throw new InvalidDataException("Too many SCSI devices.");
    }

    private static UpgradeDrive[] PlannedSlots(UpgradeJournal j, bool staged)
    {
        var paths = j.Conversions.ToDictionary(c => c.Disk.Path!, c => staged ? c.Work : c.Destination, Paths);
        return Slots(j.Source!.Drives, j.Source.Hardware.GetProperty("BootOrder").Deserialize(CoreJson.Default.UInt16Array)!, paths);
    }

    internal static UpgradeDrive[] Slots(UpgradeDrive[] drives, ushort[] biosOrder, IReadOnlyDictionary<string, string> paths)
    {
        ValidateDrives(drives);
        var ordered = drives.OrderBy(d => d.Controller, StringComparer.Ordinal).ThenBy(d => d.Number).ThenBy(d => d.Location).ToArray();
        var boot = ordered.Where(d => !d.Dvd).OrderBy(d => UpgradeHardware.BootRank(biosOrder, d.Controller == "IDE" ? 2 : 4)).FirstOrDefault();
        var used = new HashSet<int>();
        var result = new List<UpgradeDrive>();
        void Map(UpgradeDrive drive, int slot)
        {
            used.Add(slot);
            result.Add(drive with { Controller = "SCSI", Number = slot / 64, Location = slot % 64,
                BiosDevice = drive.Dvd ? 1 : drive.Controller == "IDE" ? 2 : 4,
                Path = !drive.Dvd && paths.TryGetValue(drive.Path!, out var path) ? path : drive.Path });
        }

        if (boot != null) Map(boot, 0);
        var deferred = new List<UpgradeDrive>();
        foreach (var drive in ordered.Where(d => d != boot))
        {
            int slot = drive.Number * 64 + drive.Location;
            if (drive.Controller == "SCSI" && !used.Contains(slot)) Map(drive, slot);
            else deferred.Add(drive);
        }
        foreach (var drive in deferred) Map(drive, Enumerable.Range(0, 256).First(slot => !used.Contains(slot)));
        return result.ToArray();
    }

    internal static void ValidateCheckpoints(IEnumerable<CheckpointNode> nodes)
    {
        if (nodes.Any(n => !n.IsLogicalNode || n.IsWormhole)) throw new InvalidDataException("Checkpoint present.");
    }

    internal static void ValidateDiskInventory(UpgradeSource source, VmDeleteService.PurgePreview plan)
    {
        if (plan.DiskDependencyIssues.Count != 0 || plan.RetainedDiskFiles.Count != 0 ||
            !plan.DiskFiles.ToHashSet(Paths).SetEquals(Disks(source).Select(d => d.Path!)))
            throw new IOException("A disk is shared or its dependency graph is incomplete. " + string.Join(" ", plan.DiskDependencyIssues));
    }

    private static async Task<UpgradeSource> Capture(UpgradeJournal j)
    {
        var checkpoints = await VmCheckpointService.ReadCheckpointStateAsync(j.OldId);
        ValidateCheckpoints(checkpoints);
        var source = await UpgradeHardware.CaptureAsync(j.OldId);
        ValidateDrives(source.Drives);
        VmPurgeFileSafety.EnsureNoReparse(source.Root);
        var plan = Read(await VmDeleteService.PreviewPurgeAsync(j.OldId));
        ValidateDiskInventory(source, plan);
        var seen = new HashSet<string>();
        using var metadata = new VirtualDiskMetadataReader();
        var drives = new List<UpgradeDrive>();
        foreach (var d in source.Drives)
        {
            if (d.Dvd)
            {
                if (!string.IsNullOrEmpty(d.Path) && (!Path.IsPathFullyQualified(d.Path) || !File.Exists(d.Path)))
                    throw new FileNotFoundException("Optical media is inaccessible.", d.Path);
                drives.Add(d);
                continue;
            }
            string path = d.Path!;
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || metadata.ReadParentPath(path) != null)
                throw new IOException("Expected a standalone local disk: " + path);
            VmPurgeFileSafety.EnsureNoReparse(path);
            var image = Read(await HostDiskService.ReadImageAsync(path));
            if (image.Attached) throw new IOException("Image is mounted: " + path);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            OwnedFileOperations.RequireSingleLink(exclusive.SafeFileHandle);
            string identity = HostDiskResource.Image(exclusive.SafeFileHandle);
            if (!seen.Add(identity)) throw new IOException("Disk appears in multiple slots: " + path);
            drives.Add(d with { Identity = identity, Length = exclusive.Length, WriteTime = File.GetLastWriteTimeUtc(path).Ticks });
        }
        if (j.Step == UpgradeStep.Preflight)
            j.OriginalConfiguration = plan.ConfigFiles.Select(path => new VmPurgeDiskFile(path, HostDiskResource.Image(path))).ToArray();
        Guid[] groups = await Groups(j.OldId);
        var items = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Read(await VmMetadataService.ReadAsync(j.OldId))) items.Add(pair.Key, pair.Value);
        return source with { Drives = drives.ToArray(), Groups = groups, Metadata = items };
    }

    private async Task DestroySource(UpgradeJournal j)
    {
        var guards = new List<FileStream>();
        try
        {
            // Deny opens for write/delete through DestroySystem. A competing start cannot
            // write to a disk after the final identity/content-stamp comparison.
            foreach (var disk in Disks(j.Source!))
            {
                guards.Add(new FileStream(disk.Path!, FileMode.Open, FileAccess.Read, FileShare.Read));
                VerifyOriginal(disk);
                if (Paths.Equals(Path.GetExtension(disk.Path), ".vhdx"))
                    reusedImageLocks.Add(OwnedFileOperations.PinImage(disk.Path!, disk.Identity!));
            }
            if (j.Conversions.Any(c => !TrackingFiles(c.Disk.Path!).SequenceEqual(c.TrackingFiles) || Path.Exists(c.Destination)))
                throw new IOException("Changed-block tracking files changed.");
            j.DestroyAuthorized = true;
            j.Save();
            await Destroy(j.OldId, j.Source!.Name);
            j.SourceRemoved = true;
        }
        finally { foreach (var guard in guards) guard.Dispose(); }
    }

    private static Dictionary<string, string> TrackingFiles(string disk)
    {
        var files = new Dictionary<string, string>();
        foreach (string suffix in new[] { ".rct", ".mrt" })
            if (Identity(disk + suffix) is { } id) files.Add(suffix, id);
        return files;
    }

    private static async Task VerifySource(UpgradeJournal j)
    {
        var current = await Capture(j);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(j.Source!, CoreJson.Default.UpgradeSource), JsonSerializer.SerializeToElement(current, CoreJson.Default.UpgradeSource)) ||
            JsonSerializer.Serialize(j.Extras!, CoreJson.Default.UpgradeExtras) != JsonSerializer.Serialize(await Extras(j.OldId), CoreJson.Default.UpgradeExtras))
            throw new IOException("Original VM or disks changed during upgrade.");
    }

    private static async Task<UpgradeExtras> Extras(Guid id)
    {
        bool console = Read(await VmConsoleSupportService.IsConsoleSupportEnabledAsync(id));
        return new(console, console ? Read(await VmDisplaySettingsService.GetResolutionAsync(id)) : null,
            Read(await VmPowerService.GetBatteryStateAsync(id)).Enabled,
            Read(await VmSerialPortService.GetSerialPortsAsync(id)).OrderBy(p => p.PortNumber)
                .Select(p => new UpgradeSerial(p.PortNumber, p.PipePath, p.DebuggerMode)).ToArray());
    }

    private static async Task ApplyExtras(UpgradeJournal j)
    {
        var extras = j.Extras!;
        Guid id = j.Candidate;
        if (extras.Console != Read(await VmConsoleSupportService.IsConsoleSupportEnabledAsync(id)))
            Require(await VmConsoleSupportService.SetConsoleSupportAsync(id, extras.Console));
        if (extras.Display is { } display && display != Read(await VmDisplaySettingsService.GetResolutionAsync(id)))
            Require(await VmDisplaySettingsService.SetResolutionAsync(id, display.ResolutionType, display.Width, display.Height));
        if (extras.Battery != Read(await VmPowerService.GetBatteryStateAsync(id)).Enabled)
            Require(await VmPowerService.SetBatteryEnabledAsync(id, extras.Battery));
        foreach (var p in extras.Serial)
        {
            if (!string.IsNullOrEmpty(p.Pipe)) Require(await VmSerialPortService.SetPipePathAsync(id, p.Port, p.Pipe));
            if (p.Debug) Require(await VmSerialPortService.SetDebuggerModeAsync(id, p.Port, true));
        }
    }

    private static async Task<Guid[]> Groups(Guid id) =>
        (await VmGroupService.ReadAsync()).Groups.Where(g => g.VmIds.Contains(id)).Select(g => g.Id).Order().ToArray();

    private static async Task RestoreGroups(Guid id, Guid[] groups)
    {
        foreach (Guid group in groups.Except(await Groups(id)))
            await VmGroupService.MoveAsync([new VmGroupMove(id, false, null)], group);
        if (!Enumerable.SequenceEqual(groups, await Groups(id))) throw new IOException("Group membership changed.");
    }

    private static async Task VerifyFinal(UpgradeJournal j, bool staged = false)
    {
        if (await State(j.OldId) != null) throw new IOException("Original VM is registered.");
        var slots = PlannedSlots(j, staged);
        foreach (var disk in slots.Where(d => !d.Dvd))
        {
            var conversion = j.Conversions.SingleOrDefault(c => c.Disk.Identity == disk.Identity);
            if (conversion == null) VerifyOriginal(disk);
            else if (Identity(disk.Path!) != conversion.Identity || conversion.Identity == null)
                throw new IOException("Converted disk identity changed: " + disk.Path);
            if (!staged) j.DiskIdentities[disk.Path!] = conversion?.Identity ?? disk.Identity!;
        }
        await UpgradeHardware.VerifyAsync(j.Candidate, j.Source!, slots, j.Options);
        string config = Read(await WmiApi.QueryFirstAsync(
            $"SELECT ConfigurationFile, ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(j.Candidate)}",
            row => VmConfigurationPaths.Resolve((string)row["ConfigurationFile"], (string)row["ConfigurationDataRoot"])));
        if (!Path.GetFullPath(config).StartsWith(Path.TrimEndingDirectorySeparator(j.Source!.Root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !File.Exists(config))
            throw new IOException("Configuration file is outside the original VM root or missing: " + config);
        j.ConfigurationPath = config;
        if (!staged) j.DiskPaths = slots.Where(d => !d.Dvd).Select(d => d.Path!).ToArray();
        var items = Read(await VmMetadataService.ReadAsync(j.Candidate));
        if (!items.TryGetValue(j.OwnershipKey, out var owner) || owner != j.OldId.ToString("D") ||
            !Enumerable.SequenceEqual(j.Source!.Groups, await Groups(j.Candidate)) ||
            !j.Source.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(items.Where(p => p.Key != j.OwnershipKey).OrderBy(p => p.Key, StringComparer.Ordinal)) ||
            JsonSerializer.Serialize(j.Extras!, CoreJson.Default.UpgradeExtras) != JsonSerializer.Serialize(await Extras(j.Candidate), CoreJson.Default.UpgradeExtras))
            throw new IOException("Final machine settings failed readback.");
    }

    private static async Task ExportRecovery(UpgradeJournal j)
    {
        string root = Path.Combine(j.Artifacts, "Recovery");
        UpgradeJournal.PrivateDirectory(root, mustBeNew: true);
        using (var pin = HostDiskResource.PinDirectory(root)) j.RecoveryDirectories[root] = HostDiskResource.Image(pin);
        j.Save();
        var r = await VmExportService.ExportAsync(j.OldId, root, false, [], VmExportCheckpointMode.None, null, false);
        if (r.OperationStateUnknown) throw new UpgradeUncertainException(r.Error ?? root);
        if (!r.Success || r.OutputPath == null) throw new IOException(r.Error);
        j.RecoveryConfiguration = Directory.EnumerateFiles(r.OutputPath, j.OldId.ToString("D") + ".vmcx", SearchOption.AllDirectories)
            .Single(VmImportService.IsMainConfigurationPath);
        CaptureRecoveryTree(root, j);
        j.Save();
        await using var preview = await OpenRecovery(j);
    }

    private static Task<VmImportSession> OpenRecovery(UpgradeJournal j) =>
        VmImportService.OpenUpgradeRecoveryAsync(j.RecoveryConfiguration!, j.OldId, Disks(j.Source!).Select(d => d.Path!));

    private static async Task CreateCandidate(UpgradeJournal j)
    {
        var s = j.Source!;
        var settings = Read(await WmiApi.WithFirstAsync(VmCreateService.DefaultSystemSettingsWql, v =>
        {
            v["ElementName"] = "ExHyperV-" + j.TransactionId.ToString("N");
            v["VirtualSystemSubType"] = "Microsoft:Hyper-V:SubType:2";
            v["Notes"] = new[] { "ExHyperV.GenerationUpgrade/" + j.TransactionId.ToString("D") };
            v["Version"] = s.Version;
            v["ConfigurationDataRoot"] = s.Root;
            v["SnapshotDataRoot"] = s.Root;
            v["SwapFileDataRoot"] = s.Root;
            v["AutomaticStartupAction"] = (ushort)2;
            v["AutomaticSnapshotsEnabled"] = false;
            v["SecureBootEnabled"] = j.Options.EnableSecureBoot;
            if (j.Options.EnableSecureBoot) v["SecureBootTemplateId"] = UpgradeHardware.SecureBootTemplate(j.Options).ToString("D");
            return Task.FromResult(v.GetText(TextFormat.CimDtd20));
        }));
        var created = await VmCreateService.DefineAtExactLocationAsync(settings);
        string? path = VmCreateService.GetConfirmedCreatedVmPath(created);
        if (path == null)
        {
            if (created.OutcomeUnknown || created.Response.Success || created.CreatedPaths.Length != 0)
                throw new UpgradeUncertainException(created.Response.Error);
            throw new IOException(created.Response.Error);
        }
        try
        {
            using var vm = new ManagementObject(path);
            vm.Get();
            Guid id = Guid.Parse((string)vm["Name"]);
            if (id == Guid.Empty || id == j.OldId) throw new InvalidDataException(path);
            j.NewId = id;
            j.CandidateCreated = true;
            j.Save();
        }
        catch (Exception e) { throw new UpgradeUncertainException(e.Message); }
        Require(created.Response);
    }

    private static async Task RequireSourceGeneration(Guid id)
    {
        var generation = Read(await WmiApi.QueryFirstAsync(
            $"SELECT VirtualSystemSubType FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier='{id:D}' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'",
            obj => obj["VirtualSystemSubType"] as string));
        if (generation != "Microsoft:Hyper-V:SubType:1") throw new InvalidDataException("Expected generation 1.");
    }

    private static async Task<int?> State(Guid id)
    {
        var r = await WmiApi.QueryFirstAsync($"SELECT EnabledState FROM Msvm_ComputerSystem WHERE Name='{id:D}'",
            row => Convert.ToInt32(row["EnabledState"] ?? throw new InvalidDataException("EnabledState")));
        if (!r.Success) throw new UpgradeUncertainException(r.Error);
        return r.HasData ? r.Data : null;
    }

    private static async Task Shutdown(UpgradeJournal j, CancellationToken token)
    {
        var timeout = TimeSpan.FromMinutes(2);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        await WaitForShutdownAsync(() => State(j.OldId), async () =>
        {
            j.ShutdownRequested = true;
            j.Save();
            var response = await WmiApi.InvokeAsync($"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName='{j.OldId:D}'",
                "InitiateShutdown", args => { args["Force"] = false; args["Reason"] = "ExHyperV generation upgrade"; }, WmiScope.HyperV, cancellationToken: deadline.Token);
            Require(response);
        }, deadline.Token, timeout);
        j.ShutdownRequested = false;
        j.Save();
    }

    internal static async Task WaitForShutdownAsync(Func<Task<int?>> read, Func<Task> request, CancellationToken token, TimeSpan timeout)
    {
        var wait = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        var state = await read().WaitAsync(timeout, token);
        if (state == 3) return;
        if (state != 2) throw new IOException("VM is neither running nor off: " + state);
        token.ThrowIfCancellationRequested();
        if (wait.Elapsed >= timeout) throw new TimeoutException("Shutdown deadline expired before submission.");
        Task? pending = null;
        try
        {
            pending = request();
            TimeSpan requestBudget = timeout - wait.Elapsed;
            await pending.WaitAsync(requestBudget > TimeSpan.Zero ? requestBudget : TimeSpan.Zero, token);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            _ = pending?.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new UpgradeUncertainException("Shutdown submission is unconfirmed: " + error.Message);
        }
        try
        {
            while (wait.Elapsed < timeout)
            {
                var remaining = timeout - wait.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                if (await read().WaitAsync(remaining, token) == 3) return;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, remaining.TotalMilliseconds)), token);
            }
        }
        catch (Exception error) { throw new UpgradeUncertainException("Shutdown state is unconfirmed: " + error.Message); }
        throw new UpgradeUncertainException("Guest shutdown was requested but not confirmed.");
    }

    private static async Task Destroy(Guid id, string name)
    {
        if (await State(id) != 3) throw new IOException("VM must be Off before unregistering.");
        var r = await VmDeleteService.DestroyAsync(name, id);
        if (r.RegistrationState == VmRegistrationState.Unknown) throw new UpgradeUncertainException(r.Error ?? name);
        if (!r.Success || r.RegistrationState != VmRegistrationState.Unregistered) throw new IOException(r.Error);
    }
}
