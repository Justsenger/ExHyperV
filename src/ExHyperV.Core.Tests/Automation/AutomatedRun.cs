using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.Automation;

internal sealed record AutomatedRequest(string RunId, string SourceRevision, string ConfigurationPath,
    string ReportsRoot, string CoreSha256, bool IncludeRebootTests = false, bool PrepareTemplate = true,
    string[]? CheckIds = null, string? Sid = null, bool DirectNetwork = false);

internal sealed record AutomatedStatus(string RunId, string SourceRevision, string Status,
    DateTimeOffset Updated, string Message, string? ApiRunId = null, string? ResourceRunId = null,
    string? ResultsPath = null, int Passed = 0, int Failed = 0, int Skipped = 0, int Omitted = 0,
    int Unexecuted = 0, TestRunEnvironment? Environment = null);

// The scheduled worker owns the run. Runner jobs only launch it or read its durable reports.
internal static class AutomatedRun
{
    internal static async Task<int> RunAsync(string manifestPath)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var request = DurableJson.Read<AutomatedRequest>(manifestPath);
        string statusPath = Path.Combine(root, "status.json");
        AutomatedStatus state = File.Exists(statusPath) ? DurableJson.Read<AutomatedStatus>(statusPath)
            : new(request.RunId, request.SourceRevision, "preparing", DateTimeOffset.Now, "准备自动测试");
        // A foreign or damaged request may never rewrite a valid run's status.
        try
        {
            Validate(request, state);
            if (!string.Equals(Path.GetFullPath(request.ReportsRoot), Path.Combine(root, "reports"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("报告目录须位于本轮的 reports 子目录。");
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        FileStream? lease = null;
        bool finished = false;
        string? lastMessage = null;
        void Save(string status, string message)
        {
            state = state with { Status = status, Message = message, Updated = DateTimeOffset.Now };
            DurableJson.Write(statusPath, state);
            if (message != lastMessage) Console.WriteLine($"[{state.Updated:HH:mm:ss}] {message}");
            lastMessage = message;
        }
        try
        {
            Directory.CreateDirectory(root);
            try { lease = new FileStream(Path.Combine(root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return 0; } // The same durable run already has an owner.
            state = File.Exists(statusPath) ? DurableJson.Read<AutomatedStatus>(statusPath) : state;
            try { Validate(request, state); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            if (IsTerminal(state.Status)) return state.Status == "completed" ? 0 : 1;
            if (request.Sid is not null && request.Sid != WindowsIdentity.GetCurrent().User?.Value)
                throw new InvalidOperationException("本轮须由原登记账户继续执行。");
            // A startup task may inherit a retired desktop proxy address. Clear
            // overrides so Windows supplies its current proxy for this process.
            TestNetwork.Initialize(request.DirectNetwork);
            Environment.SetEnvironmentVariable("EXHYPERV_TEST_PREPARE_TEMPLATE", request.PrepareTemplate ? "1" : "0");
            Environment.SetEnvironmentVariable("NO_PROXY", request.DirectNetwork ? "*" : "localhost,127.0.0.1,::1");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("自动实机测试需要已具备管理员权限的执行账户。");
            string core = typeof(ExHyperV.Services.VmQueryService).Assembly.Location;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core))), request.CoreSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前 Core 与本轮固定的源码构建结果相异，保留原轮次。");
            state = state with { Environment = TestRunEnvironment.Capture() };

            ApiTestReport report;
            if (state.ApiRunId is not null)
            {
                // After a restart, a result is observed, never submitted again.
                report = ReadOwnedReport(request, state);
                if (report.Running && RebootContinuation.LatestReport(request.ReportsRoot)?.RunId != report.RunId)
                    throw new IOException("执行进程曾中断，最后一项的原生结果尚待核对。已保留报告，后续写入暂停。");
                while (report.Running)
                {
                    state = Summarize(request, report, state.ResultsPath!);
                    Save(report.Phase.Contains("重启等待", StringComparison.Ordinal) ? "waiting-reboot" : "running", report.Phase);
                    await Task.Delay(2000);
                    report = ReadOwnedReport(request, state);
                }
            }
            else
            {
                Save("preparing", "检查模板与本机资源");
                var configuration = InspectionConfiguration.Read(request.ConfigurationPath);
                if (TemplatePowerLease.HasPendingChange(root)) throw new IOException("旧轮次的模板电源记录待核对，保留原恢复现场。");
                var scanner = new ResourceScanner(request.ReportsRoot, request.ConfigurationPath);
                scanner.TryStart(CancellationToken.None);
                await scanner.Completion;
                var resources = scanner.Snapshot();
                if (!resources.Plan.Available) throw new IOException(resources.Plan.Reason);
                string[] selected = Select(resources.Plan, request);
                DurableJson.Write(Path.Combine(root, "selection.json"), resources.Plan.Items.Select(i => new
                {
                    i.Id, i.Name, Selected = selected.Contains(i.Id),
                    Reasons = selected.Contains(i.Id) ? Array.Empty<string>() : i.Reasons.Length > 0 ? i.Reasons
                        : !request.IncludeRebootTests && TestPlanner.Catalog.Any(c => c.Id == i.Id && RebootApiCases.Supports(c))
                            ? ["本轮未启用宿主重启测试。"] : ["本轮按指定范围执行。"]
                }).ToArray());
                if (selected.Length == 0) throw new IOException("本轮条件齐备的测试项为零，详见资源报告。");
                var runner = new VmQueryTestRunner(request.ReportsRoot, workspaceProvider: () => configuration.WorkDirectory!,
                    onStarted: started =>
                    {
                        string path = Path.Combine(request.ReportsRoot, started.ResourceRunId, "api-tests", started.RunId, "results.json");
                        state = Summarize(request, started, path);
                        Save("running", "已固定本轮身份，准备执行 API 测试");
                    });
                if (!runner.TryStart(resources, new(resources.RunId, Guid.NewGuid().ToString("N"), selected), CancellationToken.None, out var reason))
                    throw new IOException(reason);
                report = runner.Snapshot()!;
                string results = Path.Combine(request.ReportsRoot, report.ResourceRunId, "api-tests", report.RunId, "results.json");
                // Persist the exact identities before observing completion or reboot.
                state = Summarize(request, report, results);
                Save("running", "执行条件齐备的 API 测试");
                while (!runner.Completion.IsCompleted)
                {
                    report = runner.Snapshot()!;
                    state = Summarize(request, report, results);
                    Save(report.Phase.Contains("重启等待", StringComparison.Ordinal) ? "waiting-reboot" : "running", report.Phase);
                    await Task.WhenAny(runner.Completion, Task.Delay(5000));
                }
                await runner.Completion;
                report = runner.Snapshot()!;
            }
            state = Summarize(request, report, state.ResultsPath!);
            if (report.Error is not null || state.Unexecuted > 0)
                throw new IOException(report.Error ?? "前序中断留下尚未调用的测试项，需先核对恢复记录。");
            await TemplatePowerLease.RestoreAsync(root);
            Save(state.Failed > 0 ? "failed" : state.Passed > 0 ? "completed" : "blocked",
                $"通过 {state.Passed}，失败 {state.Failed}，环境跳过 {state.Skipped}，条件缺项 {state.Omitted}。");
            finished = true;
            return state.Status == "completed" ? 0 : 1;
        }
        catch (Exception error)
        {
            // Restoring a template while native writes are unresolved can race them.
            // Retain its power journal and make the intervention requirement explicit.
            Save(state.ApiRunId is null && !TemplatePowerLease.HasPendingChange(root) ? "blocked" : "needs-recovery", error.Message);
            return 1;
        }
        finally
        {
            lease?.Dispose();
            if (finished)
            {
                try
                {
                    await WindowsTestCommand.RunAsync("""
                        $task=Get-ScheduledTask -TaskName $a.name -ErrorAction SilentlyContinue
                        if($task){
                            if(@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ne $a.exe -or $task.Actions[0].Arguments -ne $a.arguments){throw '开机任务身份已变化，保留任务'}
                            Unregister-ScheduledTask -TaskName $a.name -Confirm:$false
                        }
                        @{released=$true}|ConvertTo-Json
                        """, new { name = "ExHyperV.Core.Tests.Automation." + request.RunId, exe = Environment.ProcessPath,
                            arguments = "--run-api-suite \"" + Path.GetFullPath(manifestPath) + "\"" });
                }
                catch (Exception error) { DurableJson.Write(Path.Combine(root, "startup-cleanup.json"), new { message = error.Message }); }
            }
        }
    }

    internal static void Validate(AutomatedRequest request, AutomatedStatus state)
    {
        if (!Guid.TryParseExact(request.RunId, "N", out _) || !System.Text.RegularExpressions.Regex.IsMatch(request.SourceRevision, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("轮次或源码提交标识无效。");
        if (request.RunId != state.RunId || request.SourceRevision != state.SourceRevision)
            throw new InvalidDataException("持久状态属于其他轮次或源码提交。");
        if (!Path.IsPathFullyQualified(request.ConfigurationPath) || !Path.IsPathFullyQualified(request.ReportsRoot))
            throw new InvalidDataException("资源登记和报告目录必须使用完整路径。");
    }

    internal static bool IsTerminal(string status) => status is "completed" or "failed" or "blocked" or "needs-recovery";

    internal static string[] Select(TestPlan plan, AutomatedRequest request)
    {
        var explicitIds = request.CheckIds?.ToHashSet(StringComparer.Ordinal);
        if (explicitIds is not null && explicitIds.Any(id => !plan.Items.Any(i => i.Id == id)))
            throw new InvalidDataException("指定的测试编号已离开当前清单。");
        var rebootIds = TestPlanner.Catalog.Where(RebootApiCases.Supports).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return plan.Items.Where(i => i.Eligible && (explicitIds is null || explicitIds.Contains(i.Id))
            && (request.IncludeRebootTests || !rebootIds.Contains(i.Id))).Select(i => i.Id).ToArray();
    }

    internal static ApiTestReport ReadOwnedReport(AutomatedRequest request, AutomatedStatus state)
    {
        if (!Guid.TryParseExact(state.ResourceRunId, "N", out _) || !Guid.TryParseExact(state.ApiRunId, "N", out _))
            throw new InvalidDataException("API 或资源轮次标识无效。");
        string expected = Path.GetFullPath(Path.Combine(request.ReportsRoot, state.ResourceRunId!, "api-tests", state.ApiRunId!, "results.json"));
        if (!string.Equals(expected, Path.GetFullPath(state.ResultsPath!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("报告路径离开本轮目录。");
        var report = DurableJson.Read<ApiTestReport>(expected);
        if (report.RunId != state.ApiRunId || report.ResourceRunId != state.ResourceRunId)
            throw new InvalidDataException("报告轮次与本次请求相异。");
        return report;
    }

    internal static AutomatedStatus Summarize(AutomatedRequest request, ApiTestReport report, string resultsPath)
    {
        var items = report.Items.Where(i => i.Selected).ToArray();
        bool Unexecuted(ApiTestItem i) => i.Status is ApiTestStatus.Waiting or ApiTestStatus.Running or ApiTestStatus.Cancelled
            || i.Detail.StartsWith("前序恢复或执行中断", StringComparison.Ordinal);
        return new(request.RunId, request.SourceRevision, "running", DateTimeOffset.Now, report.Phase,
            report.RunId, report.ResourceRunId, resultsPath,
            items.Count(i => i.Status == ApiTestStatus.Passed), items.Count(i => i.Status == ApiTestStatus.Failed),
            items.Count(i => i.Status == ApiTestStatus.Skipped && !Unexecuted(i)), report.Omitted?.Length ?? 0,
            items.Count(Unexecuted), report.Environment);
    }
}
