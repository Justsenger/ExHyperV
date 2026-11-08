using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

internal static class GpuWorkflowChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        static Task<GpuDeploymentSnapshot> Yes(GpuDeploymentSession s) =>
            s.SubmitDecisionAsync(new GpuConfirmation(s.GetStatus().PendingDecision!.Id, true));
        static async Task<bool> Rejected(Func<Task> action)
        {
            try { await action(); return false; }
            catch (InvalidOperationException) { return true; }
            catch (ArgumentException) { return true; }
        }
        static SshCredentials Credentials() => new() { Host = "192.0.2.1", Port = 22, Username = "root", Password = "private" };
        static LinuxGpuDeploymentScript Script() => new() { Name = "fixture", FileName = "fixture.sh", SourceUrl = "https://example.invalid/fixture.sh" };

        var noDriver = new Backend { Off = false };
        var noDriverSession = new GpuDeploymentSession(noDriver, GpuDeploymentMode.AddWithoutDrivers);
        var first = await noDriverSession.StartAsync();
        check(first.State == GpuDeploymentState.Completed && !noDriver.Calls.Contains("TurnOff") && !noDriver.Calls.Contains("Optimize"),
            "GPU without drivers leaves a running VM running");
        check(first.Steps.Count == 5 && first.Steps.All(s => s.Status == GpuDeploymentStepStatus.Success), "Core owns the no-driver step list");
        await noDriverSession.CancelAsync();
        check(noDriver.Removed.Count == 0 && first.PendingCleanupAdapterId == null, "Cancelling a completed deployment cannot remove its GPU");
        check(await Rejected(() => noDriverSession.StartAsync()), "A session cannot be started twice");

        var windows = new Backend { Off = false };
        var session = new GpuDeploymentSession(windows);
        var pending = await session.StartAsync();
        check(pending.PendingDecision?.Kind == GpuDecisionKind.PowerOff && !windows.Calls.Contains("TurnOff") && !windows.Calls.Contains("Assign"),
            "Running VM requires a power-off decision before assignment");
        check(pending.AllowedActions.Contains(GpuDeploymentAction.SubmitDecision) && pending.State == GpuDeploymentState.WaitingForDecision,
            "Core exposes the pending decision and allowed actions");
        var powerRequest = pending.PendingDecision!;
        check(await Rejected(() => session.SubmitDecisionAsync(new GpuConfirmation(Guid.NewGuid(), true)))
            && session.GetStatus().PendingDecision?.Id == powerRequest.Id, "A stale decision leaves the pending operation intact");
        check(await Rejected(() => session.SubmitDecisionAsync(new GpuLinuxInput(powerRequest.Id, Script(), Credentials())))
            && windows.Calls.Count == 1, "A caller cannot skip power confirmation by submitting SSH input");
        pending = await Yes(session);
        check(pending.PendingDecision?.Kind == GpuDecisionKind.InstallWindows && !windows.Calls.Contains("Windows"),
            "Even a single Windows partition waits for explicit installation confirmation");
        check(windows.Calls.IndexOf("TurnOff") < windows.Calls.IndexOf("Assign") && pending.PendingCleanupAdapterId == "new-adapter",
            "Power, assignment and driver stages preserve their order and cleanup target");
        check(await Rejected(() => session.SubmitDecisionAsync(new GpuConfirmation(powerRequest.Id, true))),
            "A consumed confirmation cannot be replayed");
        var prior = pending;
        var duplicate = new GpuConfirmation(pending.PendingDecision!.Id, true);
        var finished = await session.SubmitDecisionAsync(duplicate);
        check(finished.State == GpuDeploymentState.Completed && windows.Calls.Count(c => c == "Windows") == 1,
            "A headless consumer completes Windows deployment through decisions");
        check(await Rejected(() => session.SubmitDecisionAsync(duplicate)) && windows.Calls.Count(c => c == "Windows") == 1,
            "Duplicate installation confirmation never executes twice");
        check(prior.State == GpuDeploymentState.WaitingForDecision && prior.Steps.Last().Status == GpuDeploymentStepStatus.Running
            && finished.Steps.Last().Status == GpuDeploymentStepStatus.Success, "Published snapshots remain unchanged after later transitions");
        check(prior.Steps is System.Collections.Generic.IList<GpuDeploymentStep> readOnlySteps && readOnlySteps.IsReadOnly
            && prior.PendingDecision!.Partitions is System.Collections.Generic.IList<GpuDeploymentPartition> readOnlyParts && readOnlyParts.IsReadOnly,
            "Step and partition collections are read-only");
        check(finished.AddedAdapterId == "new-adapter" && finished.PendingCleanupAdapterId == null, "Success keeps history but releases cleanup intent");

        var declined = new Backend { Off = false };
        var declinedSession = new GpuDeploymentSession(declined);
        var decline = await declinedSession.StartAsync();
        var declinedResult = await declinedSession.SubmitDecisionAsync(new GpuConfirmation(decline.PendingDecision!.Id, false));
        check(declinedResult.State == GpuDeploymentState.Cancelled && !declined.Calls.Contains("TurnOff") && !declined.Calls.Contains("Assign"),
            "Declining confirmation stops before the unapproved operation");

        var manual = new Backend { MultiplePartitions = true };
        var manualSession = new GpuDeploymentSession(manual);
        var selection = await manualSession.StartAsync();
        check(selection.PendingDecision?.Kind == GpuDecisionKind.SelectPartition && selection.PendingCleanupAdapterId == "new-adapter",
            "Multiple partitions pause the same deployment session");
        var selectionId = selection.PendingDecision!.Id;
        check(await Rejected(() => manualSession.SubmitDecisionAsync(new GpuPartitionSelection(selectionId, "arbitrary.vhdx")))
            && manualSession.GetStatus().PendingDecision?.Id == selectionId, "A caller cannot inject a disk path instead of an issued partition ID");
        var selected = await manualSession.SubmitDecisionAsync(new GpuPartitionSelection(selectionId, selection.PendingDecision.Partitions[0].Id));
        check(selected.PendingDecision?.Kind == GpuDecisionKind.InstallWindows, "Selecting Windows still requires installation confirmation");
        manual.FailDrivers = true;
        var failed = await Yes(manualSession);
        check(failed.State == GpuDeploymentState.Failed && failed.Error == "failed" && manual.Removed.SequenceEqual(new[] { "new-adapter" }),
            "Driver failure reports its cause and removes only this deployment's adapter");

        foreach (var os in new[] { OperatingSystemType.Unknown, OperatingSystemType.EFI })
        {
            var unsupported = new GpuDeploymentSession(new Backend { Os = os });
            var choice = await unsupported.StartAsync();
            check(choice.State == GpuDeploymentState.WaitingForDecision &&
                await Rejected(() => unsupported.SubmitDecisionAsync(new GpuPartitionSelection(choice.PendingDecision!.Id, choice.PendingDecision.Partitions[0].Id))),
                "Unsupported partition cannot be reported as successful: " + os);
            await unsupported.CancelAsync();
        }
        var encryptedSession = new GpuDeploymentSession(new Backend { BitLocker = true });
        var encrypted = await encryptedSession.StartAsync();
        check(await Rejected(() => encryptedSession.SubmitDecisionAsync(new GpuPartitionSelection(encrypted.PendingDecision!.Id, encrypted.PendingDecision.Partitions[0].Id))),
            "Core rejects encrypted partitions before installation");
        await encryptedSession.CancelAsync();

        var diskChanged = new Backend();
        var changedDiskSession = new GpuDeploymentSession(diskChanged);
        await changedDiskSession.StartAsync();
        diskChanged.DiskPath = "replacement.vhdx";
        var changedDisk = await Yes(changedDiskSession);
        check(changedDisk.State == GpuDeploymentState.Failed && !diskChanged.Calls.Contains("Windows"),
            "Changed disk identity is rejected before driver writes");

        var restarted = new Backend();
        var restartSession = new GpuDeploymentSession(restarted);
        await restartSession.StartAsync();
        restarted.Off = false;
        var powerAgain = await Yes(restartSession);
        check(powerAgain.PendingDecision?.Kind == GpuDecisionKind.PowerOff && !restarted.Calls.Contains("Windows"),
            "VM restarted during confirmation requires renewed power-off consent");
        var installAgain = await Yes(restartSession);
        check(installAgain.PendingDecision?.Kind == GpuDecisionKind.InstallWindows && !restarted.Calls.Contains("Windows"),
            "Power-off consent does not implicitly approve the subsequent disk write");
        await restartSession.CancelAsync();

        var ambiguous = new Backend { UnidentifiedAssignment = true, ConcurrentAssignment = true };
        var ambiguousSession = new GpuDeploymentSession(ambiguous);
        var ambiguousResult = await ambiguousSession.StartAsync();
        check(ambiguousResult.State == GpuDeploymentState.CleanupRequired && ambiguous.Removed.Count == 0 && !ambiguous.Calls.Contains("Windows"),
            "Ambiguous adapter ownership stops deployment without removing an unrelated adapter");
        var stillAmbiguous = await ambiguousSession.RetryCleanupAsync();
        check(stillAmbiguous.State == GpuDeploymentState.CleanupRequired && ambiguous.Removed.Count == 0,
            "Cleanup retry does not turn an unidentified assignment into an inferred owner");

        var unrelated = new Backend { FailAssignmentBeforeCreation = true, ConcurrentAssignment = true };
        var unrelatedResult = await new GpuDeploymentSession(unrelated).StartAsync();
        check(unrelatedResult.State == GpuDeploymentState.Failed && unrelated.Removed.Count == 0
            && unrelatedResult.AddedAdapterId == null && unrelated.AdapterReads == 0,
            "An assignment rejected before creation never claims another caller's new adapter");

        var overlapping = new Backend { ConcurrentAssignment = true, FailDrivers = true };
        var overlappingSession = new GpuDeploymentSession(overlapping);
        var overlappingPending = await overlappingSession.StartAsync();
        check(overlappingPending.State == GpuDeploymentState.WaitingForDecision && overlapping.AdapterReads == 0,
            "An exact creation receipt remains usable when another caller adds an adapter concurrently");
        var overlappingFailure = await Yes(overlappingSession);
        check(overlappingFailure.State == GpuDeploymentState.Failed
            && overlapping.Removed.SequenceEqual(new[] { "new-adapter" }),
            "Failure cleans only the receipt's adapter and preserves the concurrent adapter");

        var partialAssignment = new Backend { FailAssignmentAfterCreation = true, ConcurrentAssignment = true };
        var partialAssignmentResult = await new GpuDeploymentSession(partialAssignment).StartAsync();
        check(partialAssignmentResult.State == GpuDeploymentState.Failed
            && partialAssignment.Removed.SequenceEqual(new[] { "new-adapter" }),
            "A failed assignment retains its exact created identity for cleanup");

        var thrownAssignment = new Backend { ThrowAssignment = true, ConcurrentAssignment = true };
        var thrownSession = new GpuDeploymentSession(thrownAssignment);
        var thrownResult = await thrownSession.StartAsync();
        check(thrownResult.State == GpuDeploymentState.CleanupRequired && thrownAssignment.Removed.Count == 0
            && (await thrownSession.RetryCleanupAsync()).State == GpuDeploymentState.CleanupRequired,
            "An exception without a creation receipt never authorizes cleanup, including on retry");

        var incompleteReceipt = new Backend { OmitCreatedIdentity = true };
        var incompleteResult = await new GpuDeploymentSession(incompleteReceipt).StartAsync();
        check(incompleteResult.State == GpuDeploymentState.CleanupRequired && incompleteReceipt.Removed.Count == 0,
            "Successful assignment without a created identity is not treated as clean success");

        var unfinishedAssignment = new Backend { OutcomeUnknown = true };
        var unfinishedResult = await new GpuDeploymentSession(unfinishedAssignment).StartAsync();
        check(unfinishedResult.State == GpuDeploymentState.CleanupRequired && unfinishedAssignment.Removed.Count == 0
            && unfinishedResult.PendingCleanupAdapterId == "new-adapter",
            "A known adapter is not removed while the creating operation may still be running");

        var changed = new Backend { TargetChanged = true };
        var rejected = await new GpuDeploymentSession(changed).StartAsync();
        check(rejected.State == GpuDeploymentState.Failed && changed.Calls.Count == 0, "Changed VM identity is rejected before any action");

        var update = new Backend { FailDrivers = true };
        var updateSession = new GpuDeploymentSession(update, GpuDeploymentMode.UpdateDrivers);
        var updating = await updateSession.StartAsync();
        await Yes(updateSession);
        check(updating.Steps.Select(s => s.Type).SequenceEqual(new[] { GpuDeploymentStepKind.PowerCheck, GpuDeploymentStepKind.Driver })
            && !update.Calls.Contains("Assign") && update.Removed.Count == 0, "Driver-only update neither adds nor rolls back an existing GPU");

        var linux = new Backend { Os = OperatingSystemType.Linux };
        var linuxSession = new GpuDeploymentSession(linux);
        var linuxPending = await linuxSession.StartAsync();
        check(linuxPending.PendingDecision?.Kind == GpuDecisionKind.StartLinux && !linux.Calls.Contains("Start"),
            "Linux boot requires explicit consent");
        var ssh = await Yes(linuxSession);
        check(ssh.PendingDecision?.Input == GpuDecisionInput.LinuxCredentials && ssh.PendingDecision.SuggestedHost == "192.0.2.1",
            "Core prepares Linux and returns SSH input with an IP suggestion");
        var sshId = ssh.PendingDecision!.Id;
        var invalid = Credentials(); invalid.Port = 0;
        check(await Rejected(() => linuxSession.SubmitDecisionAsync(new GpuLinuxInput(sshId, Script(), invalid)))
            && linuxSession.GetStatus().PendingDecision?.Id == sshId, "Invalid SSH input stays at the same decision");
        invalid = Credentials(); invalid.UseProxy = true; invalid.ProxyPort = 70000;
        check(await Rejected(() => linuxSession.SubmitDecisionAsync(new GpuLinuxInput(sshId, Script(), invalid))),
            "Proxy validation belongs to Core");
        check(await Rejected(() => linuxSession.SubmitDecisionAsync(new GpuLinuxInput(sshId, null, Credentials()))),
            "Missing Linux script cannot advance the state machine");
        var back = await linuxSession.SubmitDecisionAsync(new GpuBackToPartitions(sshId));
        check(back.PendingDecision?.Kind == GpuDecisionKind.SelectPartition && back.PendingDecision.Id != sshId && linux.Off == false,
            "Returning to partition choice issues a fresh decision without mounting a running guest's disk");
        check(await Rejected(() => linuxSession.SubmitDecisionAsync(new GpuLinuxInput(sshId, Script(), Credentials()))),
            "An old SSH form cannot submit after returning to partitions");
        ssh = await linuxSession.SubmitDecisionAsync(new GpuPartitionSelection(back.PendingDecision!.Id, back.PendingDecision.Partitions[0].Id));
        var credentials = Credentials();
        var linuxResult = await linuxSession.SubmitDecisionAsync(new GpuLinuxInput(ssh.PendingDecision!.Id, Script(), credentials));
        check(linuxResult.State == GpuDeploymentState.Completed && linux.Calls.Count(c => c == "Linux") == 1
            && linuxResult.Steps.Last().Status == GpuDeploymentStepStatus.Success, "Core interprets Linux progress and completion without WPF");
        check(credentials.Host == "192.0.2.1" && !System.Text.Json.JsonSerializer.Serialize(linuxResult).Contains("private"),
            "Backend credential changes do not mutate the caller or leak passwords into state");

        var assigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelBackend = new Backend { ConcurrentAssignment = true, AssignPause = async () => { assigned.SetResult(); await release.Task; } };
        var cancelSession = new GpuDeploymentSession(cancelBackend);
        var running = cancelSession.StartAsync();
        await assigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(cancelSession.GetStatus().State == GpuDeploymentState.Running, "Polling state remains available during a native call");
        var cancelling = cancelSession.CancelAsync();
        check(!cancelling.IsCompleted, "Cancellation waits for active assignment to identify its cleanup target");
        release.SetResult();
        var cancelled = await running;
        await cancelling;
        check(cancelled.State == GpuDeploymentState.Cancelled && !cancelBackend.Calls.Contains("Windows")
            && cancelBackend.Removed.SequenceEqual(new[] { "new-adapter" }), "Cancellation skips later stages and cleans the exact adapter once");

        var cleanup = new Backend { FailRemoval = true, ConcurrentAssignment = true };
        var cleanupSession = new GpuDeploymentSession(cleanup);
        await cleanupSession.StartAsync();
        var dirty = await cleanupSession.CancelAsync();
        check(dirty.State == GpuDeploymentState.CleanupRequired && dirty.PendingCleanupAdapterId == "new-adapter"
            && dirty.CleanupError != null && dirty.PendingDecision == null, "Failed cancellation retains its cleanup target and exposes the failure");
        cleanup.FailRemoval = false;
        var clean = await cleanupSession.RetryCleanupAsync();
        check(clean.State == GpuDeploymentState.Cancelled && clean.PendingCleanupAdapterId == null && cleanup.Calls.Count(c => c == "Assign") == 1,
            "Retry performs cleanup only and never repeats assignment");

        var manuallyCleaned = new Backend { FailRemoval = true };
        var manualCleanupSession = new GpuDeploymentSession(manuallyCleaned);
        await manualCleanupSession.StartAsync();
        await manualCleanupSession.CancelAsync();
        manuallyCleaned.FailRemoval = false;
        await manuallyCleaned.RemoveAsync("new-adapter");
        int removals = manuallyCleaned.Removed.Count;
        var reconciled = await manualCleanupSession.RetryCleanupAsync();
        check(reconciled.State == GpuDeploymentState.Cancelled && manuallyCleaned.Removed.Count == removals,
            "Cleanup retry recognizes an already-removed target without issuing another removal");

        var callbackBackend = new Backend();
        var callbackSession = new GpuDeploymentSession(callbackBackend, GpuDeploymentMode.AddWithoutDrivers);
        var callbackResult = await callbackSession.StartAsync(_ => throw new InvalidOperationException("UI gone"));
        check(callbackResult.State == GpuDeploymentState.Completed && callbackBackend.Removed.Count == 0,
            "A presentation callback failure cannot derail native deployment");

        var concurrent = new Backend();
        var concurrentSession = new GpuDeploymentSession(concurrent);
        var concurrentPending = await concurrentSession.StartAsync();
        var enter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        concurrent.WindowsPause = async () => { enter.SetResult(); await finish.Task; };
        var same = new GpuConfirmation(concurrentPending.PendingDecision!.Id, true);
        var one = concurrentSession.SubmitDecisionAsync(same);
        await enter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var two = Rejected(() => concurrentSession.SubmitDecisionAsync(same));
        finish.SetResult();
        await one;
        check(await two && concurrent.Calls.Count(c => c == "Windows") == 1,
            "Concurrent duplicate submissions execute the native operation exactly once");

        var cancelledByProgress = new Backend();
        var progressSession = new GpuDeploymentSession(cancelledByProgress);
        var progressResult = await progressSession.StartAsync(s =>
        {
            if (s.Steps.Any(step => step.Type == GpuDeploymentStepKind.Assign && step.Status == GpuDeploymentStepStatus.Running))
                progressSession.RequestCancellation();
        });
        check(progressResult.State == GpuDeploymentState.Cancelled && !cancelledByProgress.Calls.Contains("Assign"),
            "Cancellation from a progress callback prevents the next native action");
    }

    internal sealed class Backend : IGpuDeploymentBackend
    {
        public List<string> Calls { get; } = new();
        public List<string> Removed { get; } = new();
        public bool FailDrivers, MultiplePartitions, UnidentifiedAssignment, TargetChanged, FailRemoval, BitLocker;
        public bool ConcurrentAssignment, FailAssignmentBeforeCreation, FailAssignmentAfterCreation, ThrowAssignment;
        public bool OmitCreatedIdentity, OutcomeUnknown;
        public int AdapterReads;
        public bool Off = true;
        public OperatingSystemType Os = OperatingSystemType.Windows;
        public string DiskPath = "owned.vhdx";
        public Func<Task>? AssignPause, WindowsPause;
        private bool assigned;
        private bool unrelatedAssigned;
        internal bool ReconciliationComplete;
        public Task<GpuAssignmentAttempt> ReconcileAssignmentAsync() => Task.FromResult(ReconciliationComplete
            ? new GpuAssignmentAttempt(ApiResponse.Ok(), "new-adapter")
            : new GpuAssignmentAttempt(ApiResponse.Fail("controlled-pending", kind: ApiErrorKind.OutcomeUnknown), OwnershipUncertain: true));
        public Task VerifyTargetAsync() => TargetChanged ? Task.FromException(new InvalidOperationException("changed")) : Task.CompletedTask;
        public Task PrepareHostAsync() { Calls.Add("Prepare"); return Task.CompletedTask; }
        public Task<bool> CheckConfigurationAsync() => Task.FromResult(false);
        public Task<(bool IsOff, string State)> IsPoweredOffAsync() => Task.FromResult((Off, Off ? "Off" : "Running"));
        public Task PowerAsync(string action) { Calls.Add(action); Off = action == "TurnOff"; return Task.CompletedTask; }
        public Task<bool> OptimizeAsync() { Calls.Add("Optimize"); return Task.FromResult(true); }
        public Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> GetAdaptersAsync()
        {
            AdapterReads++;
            var entries = new List<(string, string)> { ("existing", "gpu") };
            if (assigned) entries.Add(("new-adapter", "gpu"));
            if (unrelatedAssigned) entries.Add(("unrelated-new-adapter", "gpu"));
            return Task.FromResult((true, entries));
        }
        public async Task<GpuAssignmentAttempt> AssignAsync()
        {
            Calls.Add("Assign");
            assigned = !FailAssignmentBeforeCreation;
            unrelatedAssigned = ConcurrentAssignment;
            if (AssignPause != null) await AssignPause();
            if (ThrowAssignment) throw new InvalidOperationException("assignment receipt lost");
            if (FailAssignmentBeforeCreation) return new(ApiResponse.Fail("assignment rejected"));
            if (UnidentifiedAssignment) return new(ApiResponse.Fail("assignment identity unknown"), OwnershipUncertain: true);
            if (OutcomeUnknown) return new(ApiResponse.Fail("job status unknown"), "new-adapter", true);
            return new(FailAssignmentAfterCreation ? ApiResponse.Fail("post-create verification failed") : ApiResponse.Ok(),
                OmitCreatedIdentity ? null : "new-adapter");
        }
        public Task<bool> RemoveAsync(string id)
        { Removed.Add(id); if (!FailRemoval) assigned = false; return Task.FromResult(!FailRemoval); }
        public Task<List<DiskPartitionInfo>> GetPartitionsAsync()
        {
            if (!Off) throw new InvalidOperationException("Cannot mount a running guest disk");
            var parts = new List<DiskPartitionInfo> { new(1, 1024, Os, "OS") { DiskPath = DiskPath, IsBitLocker = BitLocker } };
            if (MultiplePartitions) parts.Add(new(2, 1024, OperatingSystemType.Windows, "OS") { DiskPath = DiskPath });
            return Task.FromResult(parts);
        }
        public async Task<(bool Success, string Message)> SyncWindowsAsync(DiskPartitionInfo partition, Action<string> log)
        { Calls.Add("Windows"); if (WindowsPause != null) await WindowsPause(); return (!FailDrivers, FailDrivers ? "failed" : "OK"); }
        public Task<string> FindIpAsync() => Task.FromResult("192.0.2.1");
        public Task<string> ProvisionLinuxAsync(LinuxGpuDeploymentScript script, SshCredentials credentials, Action<string> log, CancellationToken token)
        {
            Calls.Add("Linux");
            credentials.Host = "192.0.2.2";
            log("[STEP: Installing]");
            return Task.FromResult("completed successfully with signing");
        }
    }
}
