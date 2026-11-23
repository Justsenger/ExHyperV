using System.Buffers.Binary;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ExHyperV.Services.VmStorageService;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class VmStorageMaintenanceTests
{
    private const long GiB = 1L << 30;
    private const string Leaf = @"C:\vm\leaf.vhdx", Mid = @"C:\vm\mid.vhdx", Root = @"C:\vm\root.vhdx";
    private static VmStorageAttachment Disk(string type = "Dynamic", string ext = ".vhdx", string controller = "SCSI") =>
        new() { DriveType = "HardDisk", DiskType = "Virtual", PathOrDiskNumber = Path.ChangeExtension(Leaf, ext),
            VhdType = type, VhdFormat = ext, VirtualCapacityBytes = 20 * GiB, ControllerType = controller, SelectionToken = "drive" };

    [TestMethod]
    [DataRow(VmState.Saved)]
    [DataRow(VmState.Suspended)]
    [DataRow(VmState.FastSaved)]
    [DataRow(VmState.Starting)]
    [DataRow(VmState.Unknown)]
    public void StorageSavedAndTransitionalPowerForbidAllEdits(VmState state)
    {
        var drive = Disk();
        var access = EvaluateAccess(drive, state, VmDiskAccessKind.Normal);
        Assert.IsFalse(access.CanExpand || access.CanShrink || access.CanCompact || access.CanEditMedia || access.CanEditSlot || access.CanDeleteFile);
        drive.DriveType = "DvdDrive";
        Assert.IsFalse(EvaluateAccess(drive, state, VmDiskAccessKind.Missing).CanEditMedia);
        Assert.IsFalse(GetMediaModificationCondition(drive, state).CanModify);
    }

    [TestMethod]
    [DataRow("Dynamic", ".vhdx", "SCSI", true)]
    [DataRow("Fixed", ".vhdx", "SCSI", true)]
    [DataRow("Differencing", ".vhdx", "SCSI", false)]
    [DataRow("Fixed", ".vhd", "SCSI", false)]
    [DataRow("Dynamic", ".vhdx", "IDE", false)]
    public void StorageOnlineOnlyExpandsEligibleDisk(string type, string ext, string ctrl, bool canExpand)
    {
        var d = Disk(type, ext, ctrl);
        var a = EvaluateAccess(d, VmState.Running, VmDiskAccessKind.Normal);
        Assert.AreEqual(VmDiskAccessKind.VmRunning, a.Kind);
        Assert.AreEqual(canExpand, a.CanExpand);
        Assert.IsFalse(a.CanEditSlot || a.CanShrink || a.CanDeleteFile || a.CanChangeType || a.CanEditMedia);
        d.DriveType = "DvdDrive";
        d.DiskType = "Empty";
        a = EvaluateAccess(d, VmState.Running, VmDiskAccessKind.Normal);
        Assert.IsTrue(a.CanEditMedia);
        Assert.IsFalse(a.CanExpand || a.CanEditSlot);
    }

    [TestMethod]
    [DataRow(VmDiskAccessKind.HostMounted, true, false)]
    [DataRow(VmDiskAccessKind.ParentInUse, false, true)]
    [DataRow(VmDiskAccessKind.LockedExternal, false, false)]
    [DataRow(VmDiskAccessKind.Missing, false, false)]
    [DataRow(VmDiskAccessKind.Unavailable, false, false)]
    public void StorageFileRestrictionsAreEnforcedEvenOnAnOfflineVm(VmDiskAccessKind kind, bool compact, bool export)
    {
        var access = EvaluateAccess(Disk("Differencing"), VmState.PoweredOff, kind);
        Assert.AreEqual(compact, access.CanCompact);
        Assert.AreEqual(export, access.CanExport);
        Assert.IsFalse(access.CanExpand || access.CanShrink || access.CanChangeType || access.CanDeleteFile || access.CanMerge || access.CanEditParentPath);
        Assert.IsFalse(EvaluateAccess(Disk("Fixed"), VmState.PoweredOff, kind).CanCompact);
        var avhdx = new VmStorageAttachment { DriveType = "HardDisk", DiskType = "Virtual", PathOrDiskNumber = @"C:\vm\snap.avhdx",
            VhdType = "Differencing", VhdFormat = ".vhdx", VirtualCapacityBytes = 20 * GiB, ControllerType = "SCSI", SelectionToken = "drive" };
        var avhdxAccess = EvaluateAccess(avhdx, VmState.PoweredOff, VmDiskAccessKind.Normal);
        Assert.AreEqual(VmDiskAccessKind.Normal, avhdxAccess.Kind);
        Assert.IsTrue(avhdxAccess.CanMerge && avhdxAccess.CanExport && avhdxAccess.CanEditParentPath && avhdxAccess.CanCompact);
    }

    [TestMethod]
    public void StorageCensusProtectsAliasesSiblingsAndTransitiveParents()
    {
        const string alias = @"D:\alias.vhdx";
        var targets = new HashSet<string> { Root };
        var references = new List<MediaReference> { new("selected", Root, true) };
        string Identify(string path) => path == alias ? Root : path;
        string? Parent(string path) => path switch { Leaf => Mid, Mid => alias, _ => null };
        VmDiskAccessKind State() => EvaluateDependencies(targets, "selected", references, Identify, Parent).Kind;
        Assert.AreEqual(VmDiskAccessKind.Normal, State());
        references.Add(new("sibling", Leaf, true));
        Assert.AreEqual(VmDiskAccessKind.ParentInUse, State());
        references[1] = new("other-vm", alias, true);
        Assert.AreEqual(VmDiskAccessKind.LockedExternal, State());
        references[1] = new("checkpoint", Mid, true);
        Assert.AreEqual(VmDiskAccessKind.Unavailable, EvaluateDependencies(targets, "selected", references, Identify, _ => Mid).Kind);
        Assert.AreEqual(VmDiskAccessKind.Unavailable, EvaluateDependencies(targets, "selected", references, Identify,
            _ => throw new UnauthorizedAccessException()).Kind);
        const string staleSnapshotDisk = @"D:\missing-checkpoint.avhdx";
        references[1] = new("stale-snapshot", staleSnapshotDisk, true);
        Assert.AreEqual(VmDiskAccessKind.Normal, EvaluateDependencies(targets, "selected", references,
            path => string.Equals(path, staleSnapshotDisk, StringComparison.OrdinalIgnoreCase)
                ? throw new FileNotFoundException(path) : Identify(path),
            Parent).Kind);
    }

    private static Dictionary<string, ChainFacts> Chain()
    {
        Guid root = Guid.NewGuid(), mid = Guid.NewGuid();
        return new(StringComparer.OrdinalIgnoreCase)
        {
            [Leaf] = new("leaf", ".vhdx", "Differencing", Guid.NewGuid(), mid, Mid),
            [Mid] = new("mid", ".vhdx", "Differencing", mid, root, Root),
            [Root] = new("root", ".vhdx", "Dynamic", root, null, null)
        };
    }

    private static Task<VmDiskChainInspection> Inspect(Dictionary<string, ChainFacts> facts, string? replacement = null, bool ignore = false)
        => WalkChain(Leaf, replacement, ignore, (p, _) => Task.FromResult(facts.TryGetValue(p, out var node)
            ? node : throw new FileNotFoundException(p)));

    [TestMethod]
    public void StorageVhdxLinkageUsesNewestValidHeaderAndNeverUsesPersistentDiskId()
    {
        Guid older = new("3057d4d8-4eff-45ae-a548-f7464f7800df"), newer = new("c11e2a61-daa2-4384-a908-79958f5745dd");
        byte[] bytes = new byte[2 * 1024 * 1024];
        void Header(int offset, ulong sequence, Guid id, uint crc32c)
        {
            var header = bytes.AsSpan(offset, 4096);
            "head"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], crc32c);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], sequence);
            id.TryWriteBytes(header[32..48]);
            header[66] = 1;
        }
        Header(65536, 41, older, 0xe7844130);
        Header(131072, 42, newer, 0x8db1fc53);
        using var stream = new MemoryStream(bytes);
        Assert.AreEqual(newer, VirtualDiskLinkage.ReadDataWriteGuid(stream));
        var region = bytes.AsSpan(192 * 1024, 64 * 1024);
        "regi"u8.CopyTo(region);
        BinaryPrimitives.WriteUInt32LittleEndian(region[4..], 0x87991136);
        region[8] = 1;
        new Guid("2dc27766-f623-4200-9d64-115e9bfd4a08").TryWriteBytes(region[16..32]);
        BinaryPrimitives.WriteInt64LittleEndian(region[32..], 1 << 20);
        BinaryPrimitives.WriteInt32LittleEndian(region[40..], 1 << 20);
        region[44] = 1;
        region.CopyTo(bytes.AsSpan(256 * 1024));
        Assert.IsTrue(VirtualDiskLinkage.HasEmptyAllocationTable(stream));
        bytes[^8] = 6;
        Assert.IsFalse(VirtualDiskLinkage.HasEmptyAllocationTable(stream));
        bytes[^8] = 0;
        bytes[256 * 1024 + 40] ^= 1;
        Assert.IsFalse(VirtualDiskLinkage.HasEmptyAllocationTable(stream));
        bytes[131072 + 40] ^= 1;
        Assert.AreEqual(older, VirtualDiskLinkage.ReadDataWriteGuid(stream));
        bytes[65536 + 40] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => VirtualDiskLinkage.ReadDataWriteGuid(stream));
    }

    [TestMethod]
    public async Task StorageChainListsEveryAncestorAndRestrictsMergeTargets()
    {
        var chain = await Inspect(Chain());
        Assert.IsTrue(chain.IsComplete);
        CollectionAssert.AreEqual(new[] { Leaf, Mid, Root }, chain.Nodes.Select(n => n.Path).ToArray());
        Assert.AreEqual(1, MergeDepth(chain, Mid.ToUpperInvariant()));
        Assert.AreEqual(2, MergeDepth(chain, Root));
        Assert.AreEqual(-1, MergeDepth(chain, Leaf));
        Assert.AreEqual(-1, MergeDepth(chain, @"C:\other.vhdx"));
    }

    [TestMethod]
    public async Task StorageOverrideCanIgnoreOnlyTheFirstIdentityMismatch()
    {
        var facts = Chain();
        string replacement = @"C:\vm\copy.vhdx";
        facts.Add(replacement, facts[Mid] with { Linkage = Guid.NewGuid() });
        facts.Remove(Mid);
        Assert.AreEqual(VmDiskChainStatus.Missing, (await Inspect(facts)).Status);
        Assert.AreEqual(VmDiskChainStatus.IdMismatch, (await Inspect(facts, replacement)).Status);
        var forced = await Inspect(facts, replacement, true);
        Assert.IsTrue(forced.IsComplete);
        Assert.IsTrue(forced.Nodes[1].IdMismatchIgnored);
        facts[Root] = facts[Root] with { Linkage = Guid.NewGuid() };
        Assert.AreEqual(VmDiskChainStatus.IdMismatch, (await Inspect(facts, replacement, true)).Status);
        facts[replacement] = facts[replacement] with { Format = ".vhd" };
        Assert.AreEqual(VmDiskChainStatus.FormatMismatch, (await Inspect(facts, replacement, true)).Status);
    }

    [TestMethod]
    public async Task StorageChainReportsMountedLockedCyclesAndMissingNodes()
    {
        foreach (var status in new[] { VmDiskChainStatus.HostMounted, VmDiskChainStatus.Locked })
        {
            var facts = Chain();
            facts[Root] = facts[Root] with { Status = status };
            var broken = await Inspect(facts);
            Assert.AreEqual(status, broken.Status);
            Assert.AreEqual(Root, broken.Nodes[^1].Path);
            Assert.AreEqual(-1, MergeDepth(broken, Mid));
        }
        var cycle = Chain();
        cycle[Root] = cycle[Root] with { Identity = cycle[Leaf].Identity };
        Assert.AreEqual(VmDiskChainStatus.Cycle, (await Inspect(cycle)).Status);
        cycle.Remove(Root);
        Assert.AreEqual(VmDiskChainStatus.Missing, (await Inspect(cycle)).Status);
    }

    [TestMethod]
    [DataRow(0L, 0L, 4L)]
    [DataRow(7L * GiB, 8L * GiB + 1, 9L)]
    [DataRow(11L * GiB + 1, 8L * GiB, 12L)]
    [DataRow(10L * GiB, 10L * GiB, 10L)]
    public void StorageShrinkRoundsUpTheGreaterOfBothBounds(long minimum, long end, long expected)
    {
        var disk = Disk();
        var safety = CalculateResize(disk, EvaluateAccess(disk, VmState.PoweredOff, VmDiskAccessKind.Normal), minimum,
            new(end == 0 ? VmPartitionLayoutMode.Empty : VmPartitionLayoutMode.Regular, end, ""));
        Assert.AreEqual(expected, safety.SafeMinimumGiB);
        Assert.IsFalse(ValidateResize(safety, expected - 1).Success);
        Assert.IsTrue(ValidateResize(safety, expected).Success);
        Assert.IsFalse(ValidateResize(safety, 20).Success);
    }

    [TestMethod]
    public void StorageResizeFailsClosedOnMissingCorruptAndOutOfRangePartitions()
    {
        var d = Disk();
        var access = EvaluateAccess(d, VmState.PoweredOff, VmDiskAccessKind.Normal);
        foreach (var layout in new VirtualDiskLayout[] { new(VmPartitionLayoutMode.Unavailable, null, ""),
            new(VmPartitionLayoutMode.Corrupt, 0, ""), new(VmPartitionLayoutMode.Regular, 21 * GiB, "") })
        {
            var safety = CalculateResize(d, access, 0, layout);
            Assert.IsNull(safety.SafeMinimumGiB);
            Assert.IsFalse(ValidateResize(safety, 19).Success);
            Assert.IsTrue(ValidateResize(safety, 21).Success);
        }
        var full = CalculateResize(d, access, 0, new(VmPartitionLayoutMode.Regular, 20 * GiB - 512, ""));
        Assert.AreEqual(VmPartitionLayoutMode.NoTailSpace, full.Layout);
        Assert.IsFalse(full.CanShrink);
        Assert.IsFalse(CalculateResize(d, access, null, new(VmPartitionLayoutMode.Empty, 0, "")).CanShrink);
        var legacy = Disk(ext: ".vhd");
        var vhd = CalculateResize(legacy, EvaluateAccess(legacy, VmState.PoweredOff, VmDiskAccessKind.Normal), null, null);
        Assert.IsTrue(ValidateResize(vhd, 2040).Success);
        Assert.IsFalse(ValidateResize(vhd, 2041).Success);
        Assert.IsFalse(ValidateResize(vhd, long.MaxValue).Success);
        Assert.IsFalse(ValidateResize(vhd, 19).Success);
    }

    [TestMethod]
    public void StoragePartitionProbeIncludesSmallFinalPartitionAndConservativeFilesystems()
    {
        byte[] layout = new byte[48 + 2 * 144];
        BitConverter.GetBytes(2).CopyTo(layout, 4);
        for (int i = 0; i < 2; i++)
        {
            var entry = layout.AsSpan(48 + i * 144);
            entry[32] = 7;
            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], (i + 1) * GiB);
            BinaryPrimitives.WriteInt64LittleEndian(entry[16..], 512 * 1024);
        }
        byte[] mbr = new byte[4096], encrypted = new byte[4096], ntfs = new byte[4096];
        mbr[510] = 0x55; mbr[511] = 0xAA;
        for (int i = 0; i < 2; i++)
        {
            mbr[450 + i * 16] = 7;
            BitConverter.GetBytes((uint)((i + 1) * GiB / 512)).CopyTo(mbr, 454 + i * 16);
            BitConverter.GetBytes(1024u).CopyTo(mbr, 458 + i * 16);
        }
        "-FVE-FS-"u8.CopyTo(encrypted.AsSpan(3));
        "NTFS    "u8.CopyTo(ntfs.AsSpan(3));
        var result = VirtualDiskPartitionProbe.DecodeLayout(layout, 4 * GiB, p => p == 0 ? mbr : p == GiB ? ntfs : encrypted);
        Assert.AreEqual(2 * GiB + 512 * 1024, result.LastEnd);
        Assert.AreEqual(VmPartitionLayoutMode.Conservative, result.Mode);
        Assert.AreEqual(VmPartitionLayoutMode.Corrupt, VirtualDiskPartitionProbe.DecodeLayout(layout[..^1], 4 * GiB, _ => mbr).Mode);
        Assert.AreEqual(VmPartitionLayoutMode.Corrupt, VirtualDiskPartitionProbe.DecodeLayout(layout, GiB, _ => mbr).Mode);
        Assert.AreEqual(VmPartitionLayoutMode.Regular, VirtualDiskPartitionProbe.DecodeLayout(layout, 4 * GiB, p => p == 0 ? mbr : ntfs).Mode);
        Assert.AreEqual(VmPartitionLayoutMode.Corrupt, VirtualDiskPartitionProbe.DecodeLayout(new byte[48], GiB, _ => mbr).Mode);
        mbr.AsSpan(446, 64).Clear();
        Assert.AreEqual(VmPartitionLayoutMode.Empty, VirtualDiskPartitionProbe.DecodeLayout(new byte[48], GiB, _ => mbr).Mode);
    }

    [TestMethod]
    [DataRow(512)]
    [DataRow(4096)]
    public void StorageShrinkRejectsDamagedOrInconsistentGptCopies(int sector)
    {
        const int sectors = 256;
        byte[] image = new byte[sector * sectors], layout = new byte[48 + 144];
        image[510] = 0x55; image[511] = 0xaa; image[450] = 0xee;
        byte[] table = new byte[sector];
        var partitionType = Guid.NewGuid();
        partitionType.TryWriteBytes(table);
        BitConverter.GetBytes(16L).CopyTo(table, 32);
        BitConverter.GetBytes(40L).CopyTo(table, 40);
        table.CopyTo(image, 2 * sector);
        table.CopyTo(image, (sectors - 2) * sector);
        static uint Crc(byte[] buffer)
        {
            var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
            crc.Update(buffer);
            return (uint)crc.Value;
        }
        void Header(int at, int alternate, int entries)
        {
            var h = new byte[92];
            "EFI PART"u8.CopyTo(h);
            BitConverter.GetBytes(0x10000).CopyTo(h, 8);
            BitConverter.GetBytes(h.Length).CopyTo(h, 12);
            BitConverter.GetBytes((long)at).CopyTo(h, 24);
            BitConverter.GetBytes((long)alternate).CopyTo(h, 32);
            BitConverter.GetBytes(3L).CopyTo(h, 40);
            BitConverter.GetBytes((long)sectors - 3).CopyTo(h, 48);
            BitConverter.GetBytes((long)entries).CopyTo(h, 72);
            BitConverter.GetBytes(sector / 128).CopyTo(h, 80);
            BitConverter.GetBytes(128).CopyTo(h, 84);
            BitConverter.GetBytes(Crc(table)).CopyTo(h, 88);
            BitConverter.GetBytes(Crc(h)).CopyTo(h, 16);
            h.CopyTo(image, at * sector);
        }
        Header(1, sectors - 1, 2);
        Header(sectors - 1, 1, sectors - 2);
        layout[0] = 1; layout[4] = 1; layout[48] = 1;
        BitConverter.GetBytes(16L * sector).CopyTo(layout, 56);
        BitConverter.GetBytes(25L * sector).CopyTo(layout, 64);
        partitionType.TryWriteBytes(layout.AsSpan(80));
        "NTFS    "u8.CopyTo(image.AsSpan(16 * sector + 3));
        VirtualDiskLayout Probe() => VirtualDiskPartitionProbe.DecodeLayout(layout, image.LongLength,
            p => image.AsSpan((int)p, Math.Min(4096, image.Length - (int)p)).ToArray());
        Assert.AreEqual(41L * sector, Probe().LastEnd);
        image[(sectors - 1) * sector + 16] ^= 1;
        Assert.AreEqual(VmPartitionLayoutMode.Corrupt, Probe().Mode);
        image[(sectors - 1) * sector + 16] ^= 1;
        image[(sectors - 2) * sector + 2] ^= 1;
        Assert.AreEqual(VmPartitionLayoutMode.Corrupt, Probe().Mode);
    }

    [TestMethod]
    public async Task StorageOnlyRetargetsAfterConfirmedMergeAndReportsRecoveryPath()
    {
        var calls = new List<string>();
        foreach (var failure in new[] { ApiErrorKind.OperationFailed, ApiErrorKind.OutcomeUnknown })
        {
            var result = await MergeAndRetarget(Leaf, Root, (_, _) => Task.FromResult(ApiResponse.Fail("merge", kind: failure)),
                _ => throw new AssertFailedException());
            Assert.AreEqual(failure, result.ErrorKind);
        }
        foreach (var failure in new[] { ApiErrorKind.None, ApiErrorKind.Conflict, ApiErrorKind.OutcomeUnknown })
        {
            calls.Clear();
            var result = await MergeAndRetarget(Leaf, Root,
                (s, t) => { calls.Add(s); calls.Add(t); return Task.FromResult(ApiResponse.Ok()); },
                t => { calls.Add(t); return Task.FromResult(failure == ApiErrorKind.None ? ApiResponse.Ok() : ApiResponse.Fail("slot", kind: failure)); });
            CollectionAssert.AreEqual(new[] { Leaf, Root, Root }, calls);
            Assert.AreEqual(failure == ApiErrorKind.None, result.Success);
            if (!result.Success)
            {
                StringAssert.Contains(result.Error, Root);
                Assert.AreEqual(failure == ApiErrorKind.Conflict ? ApiErrorKind.CleanupRequired : failure, result.ErrorKind);
            }
        }
        var thrown = await FinishRetarget(Root, "已合并。", () => throw new IOException("readback"));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, thrown.ErrorKind);
    }

    [TestMethod]
    public async Task StorageMergeCleanupOnlyDeletesOriginalUnreferencedResiduals()
    {
        VmPurgeDiskFile[] consumed = [new(Leaf, "original-leaf"), new(Mid, "original-mid")];
        var deleted = new List<string>();
        ApiResponse<string> Identify(string p) => p == Leaf ? ApiResponse<string>.Empty() : ApiResponse<string>.Ok("original-mid");
        var clean = await CleanMergedFiles(consumed, Identify, paths =>
        {
            CollectionAssert.AreEqual(new[] { Mid }, paths);
            return Task.FromResult(ApiResponse.Ok());
        }, disk => deleted.Add(disk.Identity));
        Assert.IsTrue(clean.Success);
        CollectionAssert.AreEqual(new[] { "original-mid" }, deleted);
        var replaced = await CleanMergedFiles(consumed, _ => ApiResponse<string>.Ok("replacement"),
            _ => throw new AssertFailedException(), _ => throw new AssertFailedException());
        Assert.AreEqual(ApiErrorKind.CleanupRequired, replaced.ErrorKind);
        StringAssert.Contains(replaced.Error, Leaf);
        var referenced = await CleanMergedFiles(consumed, Identify, _ => Task.FromResult(ApiResponse.Fail("referenced")),
            _ => throw new AssertFailedException());
        Assert.AreEqual(ApiErrorKind.CleanupRequired, referenced.ErrorKind);
        StringAssert.Contains(referenced.Error, Mid);
    }

    [TestMethod]
    public async Task StorageNeverDeletesAfterUnconfirmedRemoval()
    {
        var result = await DetachAndDelete(Leaf, () => Task.FromResult(ApiResponse.Fail("detach", kind: ApiErrorKind.OutcomeUnknown)),
            () => throw new AssertFailedException());
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
        var partial = await DetachAndDelete(Leaf, () => Task.FromResult(ApiResponse.Ok()), () => throw new IOException("locked"));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, partial.ErrorKind);
        StringAssert.Contains(partial.Error, Leaf);
    }

    [TestMethod]
    public void StorageMoveChecksGenerationPowerOccupancyAndSlotBounds()
    {
        var d = Disk();
        var other = new VmStorageAttachment { ControllerType = "SCSI", ControllerNumber = 0, ControllerLocation = 1, SelectionToken = "other" };
        VmConfigurationState Config(int gen, VmState state) => new(Guid.Empty.ToString(), gen, state);
        string? Error(int gen, VmState state, string type, int loc) => ValidateMove(Config(gen, state), d, new(type, 0, loc), [d, other]);
        Assert.IsNull(Error(2, VmState.PoweredOff, "SCSI", 63));
        Assert.IsNotNull(Error(2, VmState.PoweredOff, "SCSI", 1));
        Assert.IsNotNull(Error(2, VmState.PoweredOff, "SCSI", 64));
        Assert.IsNotNull(Error(2, VmState.PoweredOff, "IDE", 0));
        Assert.IsNotNull(Error(1, VmState.Saved, "IDE", 0));
        Assert.IsNull(Error(1, VmState.PoweredOff, "IDE", 0));
        d.DriveType = "DvdDrive";
        Assert.IsNotNull(Error(1, VmState.PoweredOff, "SCSI", 2));
    }

    [TestMethod]
    public void StoragePassthroughRankingUsesVmIdentityAndOpticalGeneration()
    {
        var current = Guid.NewGuid();
        var optical = new HostOpticalDriveInfo { PnpDeviceId = "DVD" };
        HostDiskInfo[] disks = [new() { Number = 0 }, new() { Number = 1, CanPassthrough = true }, new() { Number = 2 }];
        var rows = RankCandidates(current, 2, disks, [optical], [(current, new() { DriveType = "HardDisk", DiskNumber = 2 })]);
        CollectionAssert.AreEqual(new[] { VmPassthroughAvailability.Available, VmPassthroughAvailability.Assigned,
            VmPassthroughAvailability.Unavailable, VmPassthroughAvailability.Unavailable }, rows.Select(x => x.Availability).ToArray());
        Assert.AreEqual(current, rows[1].OwnerVmId);
        var assigned = RankCandidates(current, 1, [], [optical], [(current, new() { DriveType = "DvdDrive", PathOrDiskNumber = "dvd" })]);
        Assert.AreEqual(current, assigned.Single().OwnerVmId);
    }

    [TestMethod]
    public void StorageFileMetadataKeepsLegacyFileSizeAndDetectsReplacement()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".iso");
        string other = path + ".new";
        try
        {
            File.WriteAllBytes(path, [1, 2]);
            File.WriteAllBytes(other, [3, 4, 5]);
            var old = new VmStorageAttachment { DriveType = "DvdDrive", DiskType = "Virtual", PathOrDiskNumber = path };
            ReadAttachmentMetadata(old);
            Assert.AreEqual(VmDiskMetadataState.Available, old.MetadataState);
            Assert.AreEqual(2L, old.FileSizeBytes);
            Assert.AreEqual(2d / GiB, old.DiskSizeGiB);
            Assert.IsNull(old.VirtualCapacityBytes);
            File.Move(other, path, true);
            var current = new VmStorageAttachment { DriveType = "DvdDrive", DiskType = "Virtual", PathOrDiskNumber = path };
            ReadAttachmentMetadata(current);
            Assert.AreEqual(3L, current.FileSizeBytes);
            Assert.IsNotNull(current.MediaFileToken);
            Assert.AreNotEqual(old.MediaFileToken, current.MediaFileToken);
        }
        finally { File.Delete(other); File.Delete(path); }
    }

    [TestMethod]
    public async Task StorageMissingMetadataIsLocalToOneItemAndBadTokensNeverReachWmi()
    {
        var disk = Disk();
        disk.PathOrDiskNumber = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vhdx");
        ReadAttachmentMetadata(disk);
        Assert.AreEqual(VmDiskMetadataState.Missing, disk.MetadataState);
        Assert.IsNull(disk.FileSizeBytes);
        foreach (var result in await Task.WhenAll(
            ResizeVirtualDiskAsync(Guid.Empty, disk, 8), ConvertVirtualDiskTypeAsync(Guid.Empty, disk, "Fixed"),
            UpgradeVhdToVhdxAsync(Guid.Empty, disk), ReconnectParentDiskAsync(Guid.Empty, disk, Root, false),
            ExportDifferencingDiskAsync(Guid.Empty, disk, Root), MergeDifferencingDiskAsync(Guid.Empty, disk, Root),
            ModifyDriveSlotAsync(Guid.Empty, disk, "IDE", 0, 0), DeleteAttachedMediaFileAsync(Guid.Empty, disk)))
            Assert.AreEqual(ApiErrorKind.InvalidArgument, result.ErrorKind);
    }
}
