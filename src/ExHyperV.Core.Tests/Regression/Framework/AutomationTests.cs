using ExHyperV.Core.Tests.Automation;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Core.Tests.TestPlanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class AutomationTests
{
    private static AutomatedRequest Request(string root) => new(Guid.NewGuid().ToString("N"), new string('a', 40),
        Path.Combine(root, "resources.json"), Path.Combine(root, "reports"), new string('B', 64));
    private static AutomatedStatus State(AutomatedRequest r) => new(r.RunId, r.SourceRevision, "preparing", DateTimeOffset.Now, "准备");
    private static ApiTestReport Report(params ApiTestItem[] items) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "request", "API",
        DateTimeOffset.Now, DateTimeOffset.Now, false, "完成", 2, 10, items);

    private static InspectionReport Resources() => new("resources", DateTimeOffset.Now, DateTimeOffset.Now, false, "部署检查", null,
        ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Ready, "已满足"))).ToArray());

    [TestMethod]
    [DataRow("platform")]
    [DataRow("identity")]
    [DataRow("dotnet")]
    [DataRow("msbuild")]
    [DataRow("powershell")]
    [DataRow("workspace")]
    public void DeploymentRequiresUsableBaseEnvironment(string missing)
    {
        var ready = Resources();
        Assert.HasCount(0, DeploymentSetup.GetIssues(ready));
        var blocked = ready with { Items = ready.Items.Select(i => i.Check.Id == missing ? i with { Result = new(InspectionStatus.Unavailable, "环境样例") } : i).ToArray() };
        Assert.HasCount(1, DeploymentSetup.GetIssues(blocked));
    }

    [TestMethod]
    public void OptionalDeviceAbsenceLimitsScopeRatherThanBlockingDeployment()
    {
        var ready = Resources();
        var report = ready with { Items = ready.Items.Select(i => i.Check.Id is "pcie-host" or "test-pcie" or "disks" or "optical" or "templates"
            ? i with { Result = new(InspectionStatus.Missing, "跳过该设备") } : i).ToArray() };
        Assert.HasCount(0, DeploymentSetup.GetIssues(report));
        Assert.IsTrue(DeploymentSetup.AvailableCount(report) > 0);
        Assert.IsTrue(DeploymentSetup.AvailableCount(report) < DeploymentSetup.AvailableCount(ready));
        Assert.IsTrue(DeploymentSetup.GetIssues(report with { Running = true }).Length > 0);
    }

    [TestMethod]
    public async Task ForeignRunCannotOverwriteExistingProgress()
    {
        await InDirectory(async root =>
        {
            var request = Request(root);
            var foreign = State(request) with { RunId = Guid.NewGuid().ToString("N") };
            string manifest = Path.Combine(root, "request.json"), status = Path.Combine(root, "status.json");
            DurableJson.Write(manifest, request); DurableJson.Write(status, foreign);
            var original = File.ReadAllBytes(status);
            Assert.AreEqual(1, await AutomatedRun.RunAsync(manifest));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(status));
            Assert.IsFalse(File.Exists(Path.Combine(root, "template-power.json")));
        });
    }

    [TestMethod]
    [DataRow("completed", 0)]
    [DataRow("failed", 1)]
    [DataRow("blocked", 1)]
    [DataRow("needs-recovery", 1)]
    public async Task TerminalRunIsObservedWithoutRepeatingWork(string outcome, int exitCode)
    {
        await InDirectory(async root =>
        {
            var request = Request(root);
            string manifest = Path.Combine(root, "request.json"), status = Path.Combine(root, "status.json");
            DurableJson.Write(manifest, request); DurableJson.Write(status, State(request) with { Status = outcome });
            var original = File.ReadAllBytes(status);
            Assert.AreEqual(exitCode, await AutomatedRun.RunAsync(manifest));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(status));
        });
    }

    [TestMethod]
    public void SelectionRequiresResourcesAndExplicitRebootOptIn()
    {
        string reboot = TestPlanner.Catalog.First(RebootApiCases.Supports).Id;
        TestOption Option(string id, bool eligible) => new(id, "组", id, "", eligible, [], "", "", [], [], true, "", [], []);
        var plan = new TestPlan("resource", true, null, "", [Option("ready", true), Option("missing", false), Option(reboot, true)]);
        var request = Request(Path.GetTempPath());
        CollectionAssert.AreEqual(new[] { "ready" }, AutomatedRun.Select(plan, request));
        CollectionAssert.AreEqual(new[] { "ready", reboot }, AutomatedRun.Select(plan, request with { IncludeRebootTests = true }));
        CollectionAssert.AreEqual(Array.Empty<string>(), AutomatedRun.Select(plan, request with { CheckIds = ["missing"] }));
        Assert.ThrowsExactly<InvalidDataException>(() => AutomatedRun.Select(plan, request with { CheckIds = ["gone"] }));
    }

    [TestMethod]
    public void InterruptedItemsRemainDistinctFromEnvironmentSkips()
    {
        ApiTestItem Item(string id, ApiTestStatus status, string detail = "") => new(id, id, "", true, status, detail, []);
        var report = Report(Item("pass", ApiTestStatus.Passed), Item("fail", ApiTestStatus.Failed),
            Item("environment", ApiTestStatus.Skipped, "缺少光驱"), Item("interrupted", ApiTestStatus.Skipped, "前序恢复或执行中断，本项尚未调用。"),
            Item("waiting", ApiTestStatus.Waiting), Item("running", ApiTestStatus.Running), Item("cancelled", ApiTestStatus.Cancelled))
            with { Omitted = [new("omitted", "", "", ["需要指定设备"], true)] };
        var state = AutomatedRun.Summarize(Request(Path.GetTempPath()), report, "results.json");
        Assert.AreEqual(1, state.Passed); Assert.AreEqual(1, state.Failed); Assert.AreEqual(1, state.Skipped);
        Assert.AreEqual(1, state.Omitted); Assert.AreEqual(4, state.Unexecuted);
    }

    [TestMethod]
    public async Task ResumeRejectsForeignReportsAndPaths()
    {
        await InDirectory(root =>
        {
            var request = Request(root); var report = Report();
            string path = Path.Combine(request.ReportsRoot, report.ResourceRunId, "api-tests", report.RunId, "results.json");
            var state = AutomatedRun.Summarize(request, report, path);
            DurableJson.Write(path, report);
            Assert.AreEqual(report.RunId, AutomatedRun.ReadOwnedReport(request, state).RunId);
            Assert.ThrowsExactly<InvalidDataException>(() => AutomatedRun.ReadOwnedReport(request, state with { ResultsPath = Path.Combine(root, "foreign.json") }));
            DurableJson.Write(path, report with { RunId = Guid.NewGuid().ToString("N") });
            Assert.ThrowsExactly<InvalidDataException>(() => AutomatedRun.ReadOwnedReport(request, state));
            Assert.ThrowsExactly<InvalidDataException>(() => AutomatedRun.ReadOwnedReport(request, state with { ResourceRunId = "../other" }));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task FailureToSaveLaunchIdentityPreventsApiExecution()
    {
        await InDirectory(async root =>
        {
            int calls = 0;
            var resources = new InspectionReport("resource", DateTimeOffset.Now, DateTimeOffset.Now, false, "测试", null,
                ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Ready, "已满足"))).ToArray());
            var runner = new VmQueryTestRunner(root, capture: (_, _) => { calls++; throw new AssertFailedException("意外执行"); },
                onStarted: _ => throw new IOException("持久状态写入失败"));
            Assert.ThrowsExactly<IOException>(() => runner.TryStart(resources,
                new(resources.RunId, Guid.NewGuid().ToString("N"), ["vm-list.ids"]), CancellationToken.None, out _));
            await runner.Completion;
            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, runner.Snapshot()!.ApiCalls);
        });
    }

    private static async Task InDirectory(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV.Automation.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); } finally { Directory.Delete(root, true); }
    }
}
