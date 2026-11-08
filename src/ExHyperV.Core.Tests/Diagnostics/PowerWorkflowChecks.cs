using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Gpu = ExHyperV.Services.VmGpuRepairService.StaleGpuPartition;

internal static class PowerWorkflowChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        void Verify(bool value, string name) => check(value, "Power workflow: " + name);
        var vmId = Guid.NewGuid();
        var targetInstance = Guid.NewGuid().ToString("D");
        var backend = new Backend("GPU Partition " + targetInstance);
        backend.Gpus.AddRange(new[] { new Gpu(vmId, 1, targetInstance, "old-a", "new-a"), new Gpu(vmId, 2, Guid.NewGuid().ToString("D"), "old-b", null) });
        var operation = new VmPowerOperationSession(vmId, VmPowerAction.Start, backend, "test");
        await operation.RunAsync();
        Verify(operation.Stage == VmPowerOperationStage.AwaitingConfirmation && backend.Executions == 1 && backend.Repaired.Count == 0,
            "headless driver pauses before any unapproved repair");
        Verify(operation.Confirmation?.Title == ExHyperV.Properties.Resources.Gpu_StalePathTitle && backend.DiskQueries == 0,
            "only implicated GPU is considered and GPU diagnosis precedes disk diagnosis");
        var requestId = operation.Confirmation!.RequestId;
        await operation.SubmitDecisionAsync(Guid.NewGuid(), true);
        Verify(operation.Stage == VmPowerOperationStage.AwaitingConfirmation && backend.Repaired.Count == 0
            && operation.Confirmation?.RequestId == requestId && operation.Failure?.ErrorKind == ApiErrorKind.Conflict,
            "stale confirmation leaves the current request and devices intact");
        await operation.SubmitDecisionAsync(requestId, true);
        Verify(operation.Stage == VmPowerOperationStage.ReadyToRetry && backend.Repaired.Single().Instance == targetInstance && backend.Executions == 1,
            "approval repairs the exact GPU and exposes retry as a separate step");
        await operation.AdvanceAsync();
        Verify(operation.Stage == VmPowerOperationStage.Completed && backend.Executions == 2, "repair retries the original power action once");
        try { await operation.AdvanceAsync(); throw new Exception("completed operation was replayed"); }
        catch (InvalidOperationException)
        {
            Verify(operation.Stage == VmPowerOperationStage.Completed && backend.Executions == 2,
                "invalid repeated advancement leaves completed operation intact");
        }

        backend = new Backend("GPU Partition");
        backend.Gpus.Add(new Gpu(vmId, 1, Guid.NewGuid().ToString("D"), "old", null));
        operation = new(vmId, VmPowerAction.Restart, backend);
        await operation.RunAsync(_ => Task.FromResult(false));
        Verify(operation.Stage == VmPowerOperationStage.Cancelled && backend.Executions == 1 && backend.Repaired.Count == 0,
            "declined confirmation neither repairs nor retries");

        backend = new Backend("out of memory");
        backend.Gpus.Add(new Gpu(vmId, 1, Guid.NewGuid().ToString("D"), "old", null));
        backend.Disks.Add(new VmStorageAttachment());
        operation = new(vmId, VmPowerAction.Start, backend);
        await operation.RunAsync(_ => throw new Exception("must not prompt"));
        Verify(operation.Stage == VmPowerOperationStage.Failed && operation.Error == "out of memory" && backend.Repaired.Count == 0,
            "unrelated failure cannot authorize repair of stale devices");

        backend = new Backend("GPU Partition", "failed to open");
        backend.Gpus.Add(new Gpu(vmId, 1, Guid.NewGuid().ToString("D"), "old", null));
        backend.Disks.Add(new VmStorageAttachment());
        operation = new(vmId, VmPowerAction.Start, backend);
        await operation.RunAsync(_ => Task.FromResult(true));
        Verify(operation.Stage == VmPowerOperationStage.Failed && operation.Error == "failed to open" && backend.Executions == 2 && backend.DiskQueries == 0,
            "failed GPU retry does not begin a second disk repair workflow");

        backend = new Backend("0x80070103");
        backend.Disks.AddRange(new[] { new VmStorageAttachment { DiskNumber = 2 }, new VmStorageAttachment { DiskModel = "missing" } });
        backend.Removals.Enqueue(true); backend.Removals.Enqueue(false);
        operation = new(vmId, VmPowerAction.Start, backend);
        await operation.RunAsync(_ => Task.FromResult(true));
        Verify(operation.Stage == VmPowerOperationStage.Failed && backend.Removed.Count == 2 && backend.Executions == 1
            && operation.Failure?.ErrorKind == ApiErrorKind.OperationFailed && operation.Error.Contains("1"),
            "partial disk repair retains failure details and stops before retry");

        backend = new Backend("0x80070103") { ThrowGpuQuery = true };
        backend.Disks.Add(new VmStorageAttachment());
        operation = new(vmId, VmPowerAction.Start, backend);
        await operation.RunAsync(_ => throw new Exception("Failed diagnosis must end before confirmation"));
        Verify(operation.Stage == VmPowerOperationStage.Failed && backend.Executions == 1
            && backend.DiskQueries == 0 && backend.Removed.Count == 0 && operation.Error.Contains("query failed"),
            "a failed GPU diagnosis cannot be treated as an empty result or authorize disk repair");

        backend = new Backend("failed to open");
        backend.Disks.Add(new VmStorageAttachment()); backend.Removals.Enqueue(false);
        operation = new(vmId, VmPowerAction.Restart, backend);
        await operation.RunAsync(_ => Task.FromResult(true));
        Verify(operation.Stage == VmPowerOperationStage.Failed && backend.Executions == 1 && operation.Error.Contains("remove failed"),
            "zero successful disk removals never retry");

        backend = new Backend("GPU Partition");
        operation = new(vmId, VmPowerAction.Stop, backend);
        await operation.RunAsync();
        Verify(operation.Stage == VmPowerOperationStage.Failed && backend.GpuQueries == 0 && backend.DiskQueries == 0,
            "non-start power failures do not diagnose start repairs");

    }

    internal sealed class Backend : IVmPowerOperationBackend
    {
        private readonly Queue<string> _errors;
        public Backend(params string[] errors) => _errors = new(errors);
        public int Executions, GpuQueries, DiskQueries;
        public bool ThrowGpuQuery;
        public Func<Task<ApiResponse>>? Execute { get; set; }
        public List<Gpu> Gpus = new(), Repaired = new();
        public List<VmStorageAttachment> Disks = new(), Removed = new();
        public Queue<bool> Removals = new();
        public Task<ApiResponse> ExecuteAsync(Guid vmId, VmPowerAction action)
        {
            Executions++;
            return Execute?.Invoke() ?? Task.FromResult(_errors.Count > 0 ? ApiResponse.Fail(_errors.Dequeue()) : ApiResponse.Ok());
        }
        public Task<ApiResponse<IReadOnlyList<Gpu>>> FindGpuAsync(Guid vmId)
        { GpuQueries++; return Task.FromResult(ThrowGpuQuery ? ApiResponse<IReadOnlyList<Gpu>>.Fail("query failed") : ApiResponse<IReadOnlyList<Gpu>>.Ok(Gpus)); }
        public Task<ApiResponse<VmGpuRepairService.RepairResult>> RepairGpuAsync(Guid vmId, IEnumerable<Gpu> targets)
        { Repaired.AddRange(targets); return Task.FromResult(ApiResponse<VmGpuRepairService.RepairResult>.Ok(new(Repaired.Count(g => g.RebindPath != null), Repaired.Count(g => g.RebindPath == null)))); }
        public Task<ApiResponse<List<VmStorageAttachment>>> FindDisksAsync(Guid vmId) { DiskQueries++; return Task.FromResult(ApiResponse<List<VmStorageAttachment>>.Ok(Disks)); }
        public Task<ApiResponse> RemoveDiskAsync(Guid vmId, VmStorageAttachment disk)
        { Removed.Add(disk); return Task.FromResult(Removals.Count == 0 || Removals.Dequeue() ? ApiResponse.Ok() : ApiResponse.Fail("remove failed", kind: ApiErrorKind.OperationFailed)); }
    }
}
