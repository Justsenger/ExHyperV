using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed record StartApiTestRequest(string ResourceRunId, string RequestId, string[] CheckIds, bool Trial = false);
internal sealed record ApiTestItem(string Id, string Name, string Description, bool Selected, ApiTestStatus Status, string Detail, string[] Evidence, string Api = "");
internal sealed record OmittedApiTest(string Id, string Name, string Api, string[] Reasons, bool Implemented);
internal sealed record ApiTestReport(string RunId, string ResourceRunId, string RequestId, string Api,
    DateTimeOffset Started, DateTimeOffset? Finished, bool Running, string Phase, int ApiCalls,
    double? ApiMilliseconds, ApiTestItem[] Items, string? Error = null, bool Trial = false,
    TestRunEnvironment? Environment = null, OmittedApiTest[]? Omitted = null);

internal sealed class VmQueryTestRunner(string reportDirectory,
    Func<Action<string>, CancellationToken, Task<VmQueryCapture>>? capture = null,
    Func<string>? workspaceProvider = null,
    Func<string, string, CancellationToken, Task<ApiCaseResult>>? execute = null, int inspectionPort = 5187,
    Action<ApiTestReport>? onStarted = null)
{
    private readonly object gate = new();
    private ApiTestReport? report;
    private CancellationTokenSource? cancellation;
    private Task completion = Task.CompletedTask;
    private HashSet<string> activeSelection = [];
    private string? runConfigurationPath;
    internal Task Completion { get { lock (gate) return completion; } }
    internal ApiTestReport? Snapshot()
    {
        lock (gate)
        {
            var continued = RebootContinuation.LatestReport(reportDirectory);
            var current = continued is not null && (report is null || report.RunId == continued.RunId) ? continued : report;
            return current is null ? null : current with { Items = current.Items.ToArray() };
        }
    }

    internal bool TryStart(InspectionReport resources, StartApiTestRequest request, CancellationToken stop, out string reason)
    {
        lock (gate)
        {
            reason = "";
            if (RebootContinuation.LatestReport(reportDirectory)?.Running == true) { reason = "宿主重启测试正在续跑，请等待当前轮次结束。"; return false; }
            if (!Guid.TryParse(request.RequestId, out _) || request.CheckIds is null || request.CheckIds.Length == 0)
            { reason = "请选择检查点后继续。"; return false; }
            var selected = request.CheckIds.ToHashSet(StringComparer.Ordinal);
            var plan = resources.Plan;
            if (resources.RunId != request.ResourceRunId || !plan.Available)
            { reason = "资源检查已更新，请返回清单确认。"; return false; }
            if (report?.RequestId == request.RequestId)
            {
                if (report.ResourceRunId == request.ResourceRunId && report.Trial == request.Trial && selected.SetEquals(activeSelection)) return true;
                reason = "请求编号已使用，请重新提交。"; return false;
            }
            if (report?.Running == true) { reason = "本轮测试执行中，请等待结果。"; return false; }
            if (selected.Count != request.CheckIds.Length || selected.Any(id => !plan.Items.Any(i => i.Id == id && i.Eligible)))
            { reason = "所选检查点的资源条件需重新检查。"; return false; }
            report = new(Guid.NewGuid().ToString("N"), resources.RunId, request.RequestId,
                request.Trial ? "受控试运行" : "已选 API 测试", DateTimeOffset.Now, null, true, "准备执行", 0, null,
                plan.Items.Where(i => selected.Contains(i.Id) || TestPlanner.IsVmListCheck(i.Id))
                    .Select(i => new ApiTestItem(i.Id, i.Name, i.Description, selected.Contains(i.Id),
                    !selected.Contains(i.Id) ? ApiTestStatus.Skipped : ApiTestStatus.Waiting,
                    !selected.Contains(i.Id) ? "本轮未选择" : "等待执行", [], i.Api)).ToArray(), Trial: request.Trial,
                Environment: TestRunEnvironment.Capture(), Omitted: plan.Items.Where(i => !selected.Contains(i.Id))
                    .Select(i => new OmittedApiTest(i.Id, i.Operation + "：" + i.Name, i.Api,
                        i.Reasons.Length == 0 ? ["条件已齐备，本轮由调用方取消选择。"] : i.Reasons, i.Implemented)).ToArray());
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
            activeSelection = selected;
            runConfigurationPath = Path.Combine(reportDirectory, resources.RunId, "resource-settings.json");
            // Save identity before any worker can submit a native operation.
            DurableJson.Write(Path.Combine(reportDirectory, resources.RunId, "api-tests", report.RunId, "results.json"), report);
            onStarted?.Invoke(report);
            var token = cancellation.Token;
            completion = Task.Run(() => RunAsync(resources, selected, token), CancellationToken.None);
            return true;
        }
    }

    internal bool Cancel(string runId)
    {
        if (RebootContinuation.Cancel(reportDirectory, runId)) return true;
        lock (gate)
        {
            if (report?.RunId != runId || !report.Running) return false;
            cancellation!.Cancel();
            report = report with { Phase = "正在停止" };
            return true;
        }
    }

    private async Task RunAsync(InspectionReport resources, HashSet<string> selected, CancellationToken token)
    {
        VmQueryCapture? snapshot = null;
        FileStream? hostLease = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (execute is null && capture is null) hostLease = await TestExecutionLease.AcquireAsync();
            var workspace = workspaceProvider?.Invoke() ?? Path.Combine(reportDirectory, "work");
            if (execute is null && capture is null) await TemporaryVm.RecoverAsync(workspace);
            var queries = selected.Where(TestPlanner.IsVmListCheck).ToHashSet(StringComparer.Ordinal);
            if (queries.Count > 0)
            {
                foreach (var id in queries) UpdateItem(id, ApiTestStatus.Running, "等待共享 API 快照", []);
                try
                {
                    snapshot = await (capture ?? VmQueryWorker.CaptureAsync)(SetPhase, token);
                    token.ThrowIfCancellationRequested();
                    lock (gate) report = report! with { ApiMilliseconds = snapshot.ApiMilliseconds };
                    foreach (var id in queries)
                    {
                        var result = VmQueryComparison.Evaluate(id, snapshot);
                        UpdateItem(id, result.Status, result.Detail, result.Evidence);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    foreach (var id in queries) UpdateItem(id, ApiTestStatus.Failed, error.Message, []);
                    lock (gate) report = report! with { Error = error.Message };
                }
            }
            var cases = TestPlanner.Catalog.Where(c => selected.Contains(c.Id) && !TestPlanner.IsVmListCheck(c.Id)).ToArray();
            // Pure cases have isolated processes/files; native image cases run one at a time after them.
            await Parallel.ForEachAsync(cases.Where(c => !ApiCaseRegistry.Get(c.Id).Exclusive),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (item, cancellationToken) => await RunCaseAsync(item, workspace, cancellationToken));
            foreach (var item in cases.Where(c => ApiCaseRegistry.Get(c.Id).Exclusive && !RebootApiCases.Supports(c)))
            {
                token.ThrowIfCancellationRequested();
                await RunCaseAsync(item, workspace, token);
            }
            var reboots = cases.Where(RebootApiCases.Supports).ToArray();
            if (reboots.Length > 0)
            {
                if (execute is not null) foreach (var item in reboots) await RunCaseAsync(item, workspace, token);
                else
                {
                    if (snapshot is not null)
                        DurableJson.Write(Path.Combine(reportDirectory, resources.RunId, "api-tests", report!.RunId, "snapshots.json"), snapshot);
                    hostLease?.Dispose(); hostLease = null;
                    var resumed = await RebootContinuation.QueueAsync(reportDirectory, resources, Snapshot()!, runConfigurationPath, reboots, value => { lock (gate) report = value; }, inspectionPort);
                    lock (gate) report = resumed;
                }
            }
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            lock (gate)
            {
                var detail = cancelled ? "本轮测试已停止。" : $"{report!.Phase}失败：{error.Message}";
                report = report! with { Error = cancelled ? null : detail, Items = report!.Items.Select(i => selected.Contains(i.Id)
                    && i.Status is ApiTestStatus.Waiting or ApiTestStatus.Running
                    ? i with { Status = cancelled ? ApiTestStatus.Cancelled : i.Status == ApiTestStatus.Waiting ? ApiTestStatus.Skipped : ApiTestStatus.Failed,
                        Detail = i.Status == ApiTestStatus.Waiting ? "前序恢复或执行中断，本项尚未调用。" + detail : detail } : i).ToArray() };
            }
        }
        finally
        {
            hostLease?.Dispose();
            lock (gate)
            {
                var continued = RebootContinuation.LatestReport(reportDirectory);
                bool continuationOwnsResult = continued?.RunId == report?.RunId;
                if (continuationOwnsResult) report = continued;
                if (!continuationOwnsResult)
                {
                report = report! with { Running = false, Finished = DateTimeOffset.Now,
                    Phase = token.IsCancellationRequested ? "已停止" : report.Error is not null ? "执行结束，详情见异常记录" : "所选测试已完成" };
                try
                {
                    var directory = Path.Combine(reportDirectory, resources.RunId, "api-tests", report.RunId);
                    Directory.CreateDirectory(directory);
                    var json = JsonSerializer.Serialize(report, InspectionJson.Options);
                    DurableJson.Write(Path.Combine(directory, "results.json"), report);
                    File.WriteAllText(Path.Combine(directory, "resources.json"), JsonSerializer.Serialize(resources, InspectionJson.Options));
                    if (snapshot is not null) File.WriteAllText(Path.Combine(directory, "snapshots.json"), JsonSerializer.Serialize(snapshot, InspectionJson.Options));
                    File.WriteAllText(Path.Combine(directory, "results.html"), InspectionPage.Render(JsonSerializer.Serialize(resources, InspectionJson.Options), testJson: json));
                }
                catch (Exception error) { report = report with { Error = $"报告保存失败：{error.Message}" }; }
                }
                cancellation?.Dispose(); cancellation = null;
            }
        }
    }

    private async Task RunCaseAsync(CatalogTest item, string workspace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UpdateItem(item.Id, ApiTestStatus.Running, "正在核对 API 结果", []);
        lock (gate) report = report! with { Phase = "执行所选 API 测试" };
        ApiCaseResult result;
        try { result = execute is null ? await ApiCaseWorker.ExecuteAsync(item.Id, workspace, token, runConfigurationPath) : await execute(item.Id, workspace, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { result = new(ApiTestStatus.Failed, error.Message, [], 0, 0); }
        lock (gate) report = report! with { ApiCalls = report!.ApiCalls + result.Calls,
            ApiMilliseconds = (report.ApiMilliseconds ?? 0) + result.Milliseconds };
        UpdateItem(item.Id, result.Status, result.Detail, result.Evidence);
        if (result.RecoveryRequired)
            throw new IOException($"{item.Name}（{item.Api}）需要核实并恢复资源，后续测试已停止。{result.Detail}");
    }

    private void UpdateItem(string id, ApiTestStatus status, string detail, string[] evidence)
    {
        lock (gate)
        {
            report = report! with { Items = report!.Items.Select(i => i.Id == id
                ? i with { Status = status, Detail = detail, Evidence = evidence } : i).ToArray() };
            // Persist every transition, including the current target before its native call.
            // A process exit must leave the completed evidence and the in-flight case identifiable.
            string directory = Path.Combine(reportDirectory, report.ResourceRunId, "api-tests", report.RunId);
            Directory.CreateDirectory(directory);
            DurableJson.Write(Path.Combine(directory, "results.json"), report);
        }
    }

    private void SetPhase(string stage)
    {
        lock (gate)
        {
            var phase = stage switch { "before" => "读取系统基准（调用前）", "api" => "调用 Core API", "after" => "读取系统基准（调用后）", "captured" => "核对返回结果", _ => report!.Phase };
            report = report! with { Phase = phase, ApiCalls = stage == "api" ? 1 : report!.ApiCalls };
        }
    }
}
