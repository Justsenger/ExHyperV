using ExHyperV.Tools;
using ExHyperV.Models;
using static ExHyperV.Models.VmGenerationUpgradeStatus;

namespace ExHyperV.Services;

internal interface IGenerationUpgradeHost
{
    Task ExecuteAsync(UpgradeStep step, UpgradeJournal journal, CancellationToken token);
    Task ReconcileAsync(UpgradeJournal journal);
    Task RollbackAsync(UpgradeJournal journal);
    Task CleanupAsync(UpgradeJournal journal);
    Task VerifyReceiptAsync(UpgradeJournal journal);
}

internal class UpgradeUncertainException(string message) : IOException(message);

internal sealed class UpgradeReadbackException(string message) : UpgradeUncertainException(message);

internal static class GenerationUpgradeTransaction
{
    internal static async Task<VmGenerationUpgradeResult> RunAsync(Guid id, string root, IGenerationUpgradeHost host,
        CancellationToken token, VmGenerationUpgradeOptions? options = null)
    {
        string directory = Path.Combine(root, id.ToString("D"));
        UpgradeJournal? journal = null;
        try
        {
            if (host is WindowsUpgradeHost) UpgradeJournal.PrivateDirectory(root);
            Directory.CreateDirectory(directory);
            VmPurgeFileSafety.EnsureNoReparse(directory);
            using var transactionLock = new FileStream(Path.Combine(directory, "lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            bool existing = File.Exists(Path.Combine(directory, "transaction.json"));
            journal = UpgradeJournal.Read(directory, id);
            if (journal.Step == UpgradeStep.Complete)
            {
                await host.VerifyReceiptAsync(journal);
                return Result(journal, Succeeded);
            }
            if (existing)
            {
                await host.ReconcileAsync(journal);
                return journal.Committed ? await Cleanup(journal, host) : await Rollback(journal, host);
            }
            journal.TracksOperations = true;
            journal.Options = options ?? new();
            journal.Save();
            if (host is WindowsUpgradeHost)
            {
                UpgradeJournal.PrivateDirectory(journal.Artifacts, mustBeNew: true);
                using var directoryPin = HostDiskResource.PinDirectory(journal.Artifacts);
                journal.ArtifactIdentity = HostDiskResource.Image(directoryPin);
            }
            else Directory.CreateDirectory(journal.Artifacts);
            for (var step = UpgradeStep.Preflight; step <= UpgradeStep.VerifyFinal; step++)
            {
                journal.Step = step;
                try
                {
                    token.ThrowIfCancellationRequested();
                    journal.InFlight = true;
                    journal.Save();
                    using (journal.CaptureOperations()) await host.ExecuteAsync(step, journal, token);
                    journal.RequireSettledOperations();
                    journal.InFlight = false;
                    if (step == UpgradeStep.VerifyFinal) journal.Committed = true;
                    journal.Save();
                }
                catch (Exception error)
                {
                    journal.FailureStage = step.ToString();
                    journal.Error = error.Message;
                    if (journal.Committed || IsUnknown(error) || journal.PendingOperations.Count != 0)
                    {
                        journal.UnconfirmedWithoutJob = journal.PendingOperations.Count == 0 && !journal.ShutdownRequested && step != UpgradeStep.VerifyFinal;
                        journal.Save();
                        return Result(journal, OutcomeUnknown);
                    }
                    return await Rollback(journal, host);
                }
            }
            return await Cleanup(journal, host);
        }
        catch (Exception error)
        {
            return new(OutcomeUnknown, id, journal?.NewId, journal?.Source?.Root, journal?.FinalDisk,
                journal?.FailureStage ?? journal?.Step.ToString() ?? "Journal",
                Detail(journal?.Step.ToString() ?? "Journal", string.Join(Environment.NewLine, new[] { journal?.Error, error.Message }.Where(s => !string.IsNullOrEmpty(s)).Distinct())),
                journal == null ? [directory] : Retained(journal))
                { ConfigurationPath = journal?.ConfigurationPath, DiskPaths = journal?.DiskPaths ?? [] };
        }
    }

    private static async Task<VmGenerationUpgradeResult> Rollback(UpgradeJournal journal, IGenerationUpgradeHost host)
    {
        journal.FailureStage ??= journal.Step.ToString();
        journal.Error ??= "Interrupted at " + journal.FailureStage;
        bool changed = journal.SourceRemoved || journal.CandidateCreated;
        journal.Step = UpgradeStep.Rollback;
        journal.InFlight = true;
        journal.Save();
        try { using (journal.CaptureOperations()) await host.RollbackAsync(journal); }
        catch (Exception error) when (IsUnknown(error))
        {
            journal.UnconfirmedWithoutJob = journal.PendingOperations.Count == 0;
            journal.Save();
            throw;
        }
        journal.RequireSettledOperations();
        var result = Result(journal, changed ? RolledBack : Rejected);
        // Keep the failed attempt's logs with its own operation ID; subsequent calls may retry.
        string archived = Path.Combine(journal.DirectoryPath, "rolled-back-" + journal.TransactionId.ToString("N") + ".json");
        File.Move(journal.JournalPath, archived);
        return result with { RetainedPaths = [journal.Artifacts, archived] };
    }

    private static async Task<VmGenerationUpgradeResult> Cleanup(UpgradeJournal journal, IGenerationUpgradeHost host)
    {
        journal.Step = UpgradeStep.Cleanup;
        try
        {
            journal.InFlight = true;
            journal.Save();
            using (journal.CaptureOperations()) await host.CleanupAsync(journal);
            journal.RequireSettledOperations();
            if (journal.Source is { } source)
                journal.Source = source with { Drives = [], Hardware = System.Text.Json.JsonElement.Parse("{}"u8),
                    Metadata = new(), Groups = [] };
            journal.OriginalConfiguration = [];
            journal.Conversions = [];
            journal.Extras = null;
            journal.RecoveryConfiguration = null;
            journal.Step = UpgradeStep.Complete;
            journal.InFlight = false;
            journal.Error = journal.FailureStage = null;
            journal.Save();
            return Result(journal, Succeeded);
        }
        catch (Exception error)
        {
            journal.Error = error.Message;
            journal.FailureStage = UpgradeStep.Cleanup.ToString();
            bool unknown = IsUnknown(error) || journal.PendingOperations.Count != 0;
            journal.UnconfirmedWithoutJob = unknown && journal.PendingOperations.Count == 0 && !journal.RemovingOwnershipMarker
                && error is not UpgradeReadbackException;
            journal.InFlight = unknown;
            journal.Save();
            return Result(journal, unknown ? OutcomeUnknown : CommittedCleanupPending);
        }
    }

    internal static bool IsUnknown(Exception error) => error is UpgradeUncertainException
        or CoreOperationException { Result.ErrorKind: ApiErrorKind.OutcomeUnknown }
        || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(IsUnknown);

    private static string[] Retained(UpgradeJournal j)
    {
        var paths = new List<string> { j.JournalPath };
        if (Directory.Exists(j.Artifacts)) paths.Add(j.Artifacts);
        if (j.RecoveryConfiguration != null) paths.Add(j.RecoveryConfiguration);
        foreach (var conversion in j.Conversions)
        {
            if (conversion.DirectoryIntent)
            {
                paths.Add(conversion.DirectoryPath);
                if (conversion.Identity == null || File.Exists(conversion.Work)) paths.Add(conversion.Work);
            }
            if (j.SourceRemoved && File.Exists(conversion.Disk.Path)) paths.Add(conversion.Disk.Path!);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? Detail(string? stage, string? error)
    {
        string? message = Properties.Resources.ResourceManager.GetString("GenerationUpgrade_" + stage);
        return message == null || error == null ? error : message + Environment.NewLine + error;
    }

    private static VmGenerationUpgradeResult Result(UpgradeJournal j, VmGenerationUpgradeStatus status) =>
        new(status, j.OldId, j.NewId, j.Source?.Root, j.FinalDisk, j.FailureStage, Detail(j.FailureStage, j.Error),
            status == Succeeded ? [] : Retained(j)) { ConfigurationPath = j.ConfigurationPath, DiskPaths = j.DiskPaths };
}
