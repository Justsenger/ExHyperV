using ExHyperV.Tools;
using ExHyperV.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExHyperV.Services;

internal enum UpgradeStep
{
    Preflight, PrepareStorage, PrepareGuest, ExportRecovery, CreateCandidate, RecheckSource,
    DestroySource, RestoreDevices, VerifyStaged, ValidateGuest, PublishStorage, VerifyFinal, Cleanup, Rollback, Complete
}

internal sealed record UpgradeDrive(string Controller, int Number, int Location, bool Dvd, string? Path,
    bool Shared, string? Identity = null, long Length = 0, long WriteTime = 0, ulong MinimumIOPS = 0,
    ulong MaximumIOPS = 0, Guid? QoSPolicyID = null, string? Pool = null, int BiosDevice = 0);
internal sealed record UpgradeSource(string Name, string Root, string Version, UpgradeDrive[] Drives,
    JsonElement Hardware, Guid[] Groups, SortedDictionary<string, string> Metadata);

internal sealed record UpgradeExtras(bool Console, VmDisplayResolution? Display, bool Battery, UpgradeSerial[] Serial);
internal sealed record UpgradeConversion(UpgradeDrive Disk, string Work)
{
    [JsonIgnore] public string Destination => Path.ChangeExtension(Disk.Path!, ".vhdx");
    [JsonIgnore] public string DirectoryPath => Path.GetDirectoryName(Work)!;
    public string? Identity { get; set; }
    public bool DirectoryIntent { get; set; }
    public string? DirectoryIdentity { get; set; }
    public bool DirectoryOwned { get; set; }
    public Dictionary<string, string> TrackingFiles { get; init; } = new();
}
internal sealed record UpgradeSerial(int Port, string Pipe, bool Debug);

internal sealed class UpgradeJournal
{
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ExHyperV.GenerationUpgrade");
    [JsonRequired] public int Version { get; init; } = 2;
    [JsonRequired] public Guid OldId { get; init; }
    [JsonRequired] public Guid TransactionId { get; init; } = Guid.NewGuid();
    public Guid? NewId { get; set; }
    public UpgradeSource? Source { get; set; }
    public VmPurgeDiskFile[] OriginalConfiguration { get; set; } = [];
    public VmGenerationUpgradeOptions Options { get; set; } = new();
    public UpgradeConversion[] Conversions { get; set; } = [];
    public UpgradeExtras? Extras { get; set; }
    public string? RecoveryConfiguration { get; set; }
    [JsonRequired] public UpgradeStep Step { get; set; }
    public bool InFlight { get; set; }
    public bool Committed { get; set; }
    public bool CandidateCreated { get; set; }
    public string? ConfigurationPath { get; set; }
    public string[] DiskPaths { get; set; } = [];
    public Dictionary<string, string> DiskIdentities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FinalDisk { get; set; }
    public bool DestroyAuthorized { get; set; }
    public bool SourceRemoved { get; set; }
    public string? FailureStage { get; set; }
    public string? Error { get; set; }

    public string? ArtifactIdentity { get; set; }
    public Dictionary<string, string> RecoveryFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RecoveryDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RemovingOwnershipMarker { get; set; }
    public bool TracksOperations { get; set; }
    public bool ShutdownRequested { get; set; }
    public bool UnconfirmedWithoutJob { get; set; }
    public Dictionary<Guid, NativeOperation> PendingOperations { get; init; } = [];
    internal sealed record NativeOperation(string Method, string Target, string? Job);
    private readonly object gate = new();

    internal WmiOperationCapture CaptureOperations() => new(Record, Record);

    private void Record(WmiOperationReceipt receipt)
    {
        lock (gate)
        {
            if (receipt.IsSettled) PendingOperations.Remove(receipt.Id);
            else PendingOperations[receipt.Id] = new(receipt.Method, receipt.Target, receipt.Job);
            try { SaveCore(); }
            catch (Exception error)
            {
                PendingOperations[receipt.Id] = new(receipt.Method, receipt.Target, receipt.Job);
                throw new UpgradeUncertainException("Cannot persist the native receipt: " + error.Message);
            }
        }
    }

    internal void RequireSettledOperations()
    {
        lock (gate)
            if (PendingOperations.Count != 0) throw new UpgradeUncertainException("Native operations have outstanding receipts.");
    }

    [JsonIgnore] public string DirectoryPath { get; internal set; } = "";
    [JsonIgnore] public string Artifacts => Path.Combine(DirectoryPath, TransactionId.ToString("N"));
    [JsonIgnore] public string JournalPath => Path.Combine(DirectoryPath, "transaction.json");
    [JsonIgnore] public string OwnershipKey => "ExHyperV.GenerationUpgrade." + TransactionId.ToString("N");
    [JsonIgnore] public Guid Candidate => NewId ?? throw new InvalidDataException("Missing candidate GUID.");

    internal void Save()
    {
        lock (gate) SaveCore();
    }

    private void SaveCore()
    {
        string pending = JournalPath + ".new";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, this, UpgradeJournalJson.Default.UpgradeJournal);
            stream.Flush(true);
        }
        if (!MoveFileExW(pending, JournalPath, 0x9 /* REPLACE_EXISTING | WRITE_THROUGH */))
            throw new Win32Exception(Marshal.GetLastWin32Error(), JournalPath);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string replacement, uint flags);

    internal static UpgradeJournal Read(string directory, Guid id)
    {
        string file = Path.Combine(directory, "transaction.json");
        UpgradeJournal journal;
        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);
            var data = document.RootElement;
            bool legacy = data.GetProperty("Version").GetInt32() == 1;
            if (legacy && (data.GetProperty("Step").GetString() != "Complete" || !data.GetProperty("Committed").GetBoolean()))
                throw new InvalidDataException("An earlier guest-upgrade transaction requires recovery with its original version: " + file);
            journal = data.Deserialize(UpgradeJournalJson.Default.UpgradeJournal) ?? throw new InvalidDataException(file);
            if (legacy)
            {
                journal.FinalDisk = Path.ChangeExtension(data.GetProperty("Original").GetString(), ".vhdx");
                if (journal.FinalDisk == null || data.GetProperty("WorkIdentity").GetString() is not { } identity ||
                    !journal.DiskPaths.Contains(journal.FinalDisk, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException(file);
                journal.DiskIdentities.Add(journal.FinalDisk, identity);
            }
        }
        catch (FileNotFoundException)
        {
            if (File.Exists(file + ".new")) throw new InvalidDataException("Incomplete journal: " + file);
            journal = new() { OldId = id };
        }
        if (journal.Version is not (1 or 2) || journal.OldId != id || journal.TransactionId == Guid.Empty ||
            journal.NewId == id || journal.NewId == Guid.Empty || !Enum.IsDefined(journal.Step) || journal.Step == UpgradeStep.Complete && !journal.Committed ||
            journal.Committed && (!journal.CandidateCreated || !journal.SourceRemoved || journal.NewId == null))
            throw new InvalidDataException(file);
        journal.DirectoryPath = directory;
        return journal;
    }

    // The manifest authorizes later moves and deletions by an elevated process.
    // Establish the ACL when creating the directory and verify it on every reopen.
    internal static void PrivateDirectory(string path, bool mustBeNew = false)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(admins);
        foreach (var sid in new[] { admins, system })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(path);
        if (mustBeNew)
        {
            var descriptor = acl.GetSecurityDescriptorBinaryForm();
            unsafe
            {
                fixed (byte* ptr = descriptor)
                {
                    var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = (nint)ptr };
                    if (!CreateDirectoryW(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
                }
            }
        }
        else info.Create(acl);
        VmPurgeFileSafety.EnsureNoReparse(path);
        var actual = info.GetAccessControl();
        if ((!admins.Equals(actual.GetOwner(typeof(SecurityIdentifier))) && !system.Equals(actual.GetOwner(typeof(SecurityIdentifier))))
            || !actual.AreAccessRulesProtected || actual.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    !admins.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference)))
            throw new UnauthorizedAccessException(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Size;
        internal nint Descriptor;
        internal int Inherit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
}
