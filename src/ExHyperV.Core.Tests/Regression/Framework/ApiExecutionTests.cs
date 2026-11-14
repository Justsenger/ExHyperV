using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Services;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ApiExecutionTests
{
    private static readonly Guid One = Guid.Parse("a8aa68a6-8215-4b10-97a5-57f82468b857");
    private static readonly Guid Two = Guid.Parse("63038f9e-5224-4b43-a080-dcd65582039e");
    private static VmListRow[] Baseline() => [new(One, "中文 VM", 2), new(Two, "同名样例", 3)];
    private static VmQueryCapture Capture(VmListRow[]? actual = null, VmListRow[]? after = null) => new(Baseline(), actual ?? Baseline(), after ?? Baseline(), 12.5);

    [TestMethod]
    public void InternalOsTypeNormalizationRetainsItsRegressionCoverage()
    {
        foreach (var (input, expected) in new[] { ("windows", "Windows"), ("LINUX", "Linux"), ("ubuntu", "Ubuntu"), ("未知分类", "Windows"), ((string?)null, "Windows") })
            Assert.AreEqual(expected, OsImages.Canonical(input));
        Assert.IsFalse(TestPlanner.Catalog.Any(item => item.Api == "OsImages.Canonical()"));
    }

    [TestMethod]
    public void InternalVmNameValidationRetainsItsRegressionCoverage()
    {
        foreach (var name in new string?[] { null, "", " \t\r\n" }) Assert.IsNotNull(VmEditService.GetNameError(name));
        foreach (var name in new[] { "中文 虚拟机", "A'B", "<样例>" }) Assert.IsNull(VmEditService.GetNameError(name));
        Assert.IsFalse(TestPlanner.Catalog.Any(item => item.Api == "VmEditService.GetNameError()"));
    }

    [TestMethod]
    public async Task NetworkSamplingSeparatesZeroMissingAndReplacementIdentity()
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            var definition = ApiCaseRegistry.Get("T-de71f96836494602-2");
            Assert.IsFalse(definition.UsesHostResources);
            await definition.Run(context);
            Assert.IsTrue(context.Calls >= 6);
            Assert.IsTrue(context.Assertions >= 12);
        });
    }

    [TestMethod]
    public void NativeHandlesProduceEvidenceWithoutBreakingTheAssertion()
    {
        var context = new ApiCaseContext(Path.GetTempPath(), CancellationToken.None);
        context.Equal<object>((nint)17, (nint)17, "同一捕获句柄");
        context.Equal((nuint)18, (nuint)18, "同一无符号句柄");
        Assert.ThrowsExactly<ApiCheckFailureException>(() => context.Equal<object>((nint)17, (nint)19, "更换捕获句柄"));
        Assert.AreEqual(3, context.Assertions);
        StringAssert.Contains(context.Evidence[^1], "0x13");
    }

    [TestMethod]
    public async Task CapturedConsoleFramesOwnIndependentLeases()
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            await ApiCaseRegistry.Get("api.video-capture-contract").Run(context);
            Assert.IsTrue(context.Assertions >= 9);
        });
    }

    [TestMethod]
    public void ThumbnailAcceptsRawAndLengthPrefixedPixels()
    {
        byte[] pixels = [0, 248, 224, 7];
        CollectionAssert.AreEqual(pixels, VmScreenshotImage.FromHyperVThumbnail(pixels, 2, 1)!.CopyBytes());
        CollectionAssert.AreEqual(pixels, VmScreenshotImage.FromHyperVThumbnail([0, 0, 0, 8, .. pixels], 2, 1)!.CopyBytes());
        Assert.IsNull(VmScreenshotImage.FromHyperVThumbnail([0, 0, 0, 7, .. pixels], 2, 1));
        Assert.IsNull(VmScreenshotImage.FromHyperVThumbnail([0, 248], 2, 1));
    }

    [TestMethod]
    public async Task ProgressSnapshotSurvivesAReaderHoldingThePreviousFile()
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "state.json");
            DurableJson.Write(path, "before reboot");
            Task publish;
            using (var viewer = File.OpenRead(path))
            {
                publish = Task.Run(() => DurableJson.Write(path, "ready to reboot"));
                Assert.IsTrue(SpinWait.SpinUntil(() => Directory.EnumerateFiles(directory, "*.new").Any() || publish.IsCompleted, TimeSpan.FromSeconds(5)));
                Assert.AreEqual("before reboot", DurableJson.Read<string>(path));
            }
            await publish;
            Assert.AreEqual("ready to reboot", DurableJson.Read<string>(path));
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*.new").Any());
        });
    }

    [TestMethod]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, false)]
    public async Task OnlyActiveRebootWorkRestoresItsResourceSnapshot(bool completed, bool running, bool restoreResources)
    {
        await InDirectory(directory =>
        {
            var now = DateTimeOffset.Now;
            var resources = new InspectionReport("resources", now, now, false, "检查完成", null, []);
            var report = new ApiTestReport("run", resources.RunId, "request", "reboot", now,
                running ? null : now, running, "fixture", 0, null, []);
            var state = Path.Combine(directory, "state.json");
            var marker = Path.Combine(directory, "reboot-active.json");
            DurableJson.Write(state, new { directory, completed, report, resources });
            DurableJson.Write(marker, state);
            var stateBytes = File.ReadAllBytes(state);
            var markerBytes = File.ReadAllBytes(marker);

            Assert.AreEqual(restoreResources, RebootContinuation.LatestResources(directory) is not null);
            Assert.AreEqual(report.RunId, RebootContinuation.LatestReport(directory)!.RunId);
            CollectionAssert.AreEqual(stateBytes, File.ReadAllBytes(state));
            CollectionAssert.AreEqual(markerBytes, File.ReadAllBytes(marker));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public async Task WaitingForRebootCannotAdvanceOnTheSameBoot(int number, bool automaticReboot)
    {
        await InDirectory(async directory =>
        {
            string state = Path.Combine(directory, "state.json");
            string boot = await RebootApiCases.BootStampAsync();
            var report = new ApiTestReport("run", "resources", "request", "reboot", DateTimeOffset.Now, null, true,
                $"第 {number} 次宿主重启等待你确认", 0, null, []);
            DurableJson.Write(state, new { directory, report, started = true, restarts = number, waitingForBoot = boot, automaticReboot });
            DurableJson.Write(Path.Combine(directory, "reboot-active.json"), state);
            var original = File.ReadAllBytes(state);
            Assert.AreEqual(0, await RebootContinuation.RunAsync(state));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(state));
            var shown = RebootContinuation.LatestReport(directory)!;
            Assert.IsTrue(shown.Running);
            Assert.AreEqual(report.Phase, shown.Phase);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RebootFailureMarkerStopsAnOlderRunningSnapshot(bool automaticReboot)
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "state.json");
            var now = DateTimeOffset.Now;
            var report = new ApiTestReport("run", "resources", "request", "reboot", now, null, true, "执行中", 0, null,
                [new("first", "", "", true, ApiTestStatus.Running, "", []), new("next", "", "", true, ApiTestStatus.Waiting, "", [])]);
            DurableJson.Write(path, new { directory, report, completed = false, automaticReboot });
            DurableJson.Write(Path.Combine(directory, "reboot-active.json"), path);
            File.WriteAllText(Path.Combine(directory, "recovery-hold.txt"), "模拟状态文件写入失败");

            var shown = RebootContinuation.LatestReport(directory)!;
            Assert.IsFalse(shown.Running);
            Assert.AreEqual(ApiTestStatus.Failed, shown.Items[0].Status);
            Assert.AreEqual(ApiTestStatus.Skipped, shown.Items[1].Status);
            // The startup entry must exit before opening a host lease or issuing any commands.
            Assert.AreEqual(0, await RebootContinuation.RunAsync(path));
        });
    }

    [TestMethod]
    [DataRow("T-b6e5e2765e6b0344-1")]
    [DataRow("F005")]
    [DataRow("api.input-release")]
    [DataRow("T-59566efd7e5c5a5c-1")]
    public async Task ControlledCatalogSamplesFollowTheCoreContracts(string id)
    {
        var definition = ApiCaseRegistry.Get(id);
        Assert.IsFalse(definition.Exclusive || definition.UsesHostResources, "此回归仅运行受控替身。");
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            await definition.Run(context);
            Assert.IsTrue(context.Calls > 0 && context.Assertions > 0);
        });
    }

    [TestMethod]
    public async Task UncertainNativeReplyAlsoRetainsItsSwitchWithoutMaskingTheFailure()
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None) { GuardNativeOutcomes = true };
            var failure = await Assert.ThrowsAsync<ApiCheckFailureException>(async () =>
            {
                // The lease is constructed without creating a host switch.
                await using var network = new TemporarySwitch(context);
                await context.CallAsync(() => Task.FromResult(ApiResponse.Fail("original-native-failure", kind: ApiErrorKind.OutcomeUnknown)));
            });
            StringAssert.Contains(failure.Message, "original-native-failure");
            Assert.IsTrue(context.Evidence.Any(e => e.Contains("交换机随虚拟机保留")));
            await Assert.ThrowsAsync<IOException>(() => TemporarySwitch.CleanupAllAsync(directory));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "recovery-hold.txt")));
        });
    }

    [TestMethod]
    [DataRow("中文 &amp; &lt;xml&gt;\r\n", "中文 & <xml>\r\n")]
    [DataRow("one\rline\nend", "one\rline\nend")]
    [DataRow("one&#13;&#10;two", "one\r\ntwo")]
    public void NativeMetadataBaselinePreservesCimValueLineEndings(string xmlValue, string expected)
    {
        string xml = "<INSTANCE><PROPERTY NAME=\"Name\"><VALUE>fixture</VALUE></PROPERTY><PROPERTY NAME=\"Data\"><VALUE>" + xmlValue + "</VALUE></PROPERTY></INSTANCE>";
        Assert.AreEqual(expected, VmMetadataApiCases.ParseNativeItems([xml])["fixture"]);
    }

    [TestMethod]
    public async Task DiskSizeApiReportsFileLengthAndOmitsMissingPaths()
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "length-sample.bin"), missing = Path.Combine(directory, "missing.bin");
            await File.WriteAllBytesAsync(path, new byte[137]);
            var result = await VmStorageService.GetVirtualDiskSizesAsync([path, path, missing]);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(137L, result[path]);
            Assert.IsFalse(result.ContainsKey(missing));
        });
    }

    [TestMethod]
    public async Task UncertainNativeReplyRetainsEvidenceAndStopsTheFollowingOperation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var context = new ApiCaseContext(directory, CancellationToken.None) { GuardNativeOutcomes = true };
            bool followingOperation = false;
            await Assert.ThrowsAsync<ApiCheckFailureException>(async () =>
            {
                await context.CallAsync(() => Task.FromResult(ApiResponse.Fail("fixture-uncertain", kind: ApiErrorKind.OutcomeUnknown)));
                followingOperation = true;
            });
            Assert.IsFalse(followingOperation);
            Assert.AreEqual(1, context.Calls);
            StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "recovery-hold.txt")), "fixture-uncertain");
            Assert.IsTrue(File.Exists(Path.Combine(directory, "native-uncertain-result.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ControlledErrorSamplesRemainAvailableWithoutCreatingARecoveryHold()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV.Core.Tests", Guid.NewGuid().ToString("N"));
        var context = new ApiCaseContext(directory, CancellationToken.None);
        var result = context.Call(() => ApiResponse.Fail("controlled", kind: ApiErrorKind.OutcomeUnknown));
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void QueryResponseDistinguishesFailureMissingSnapshotAndEmptyInventory()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => VmQueryWorker.ReadResponse(
            ApiResponse<VmInventorySnapshot>.Fail("denied", 5, ApiErrorSource.Wmi, kind: ApiErrorKind.AccessDenied)));
        StringAssert.Contains(failure.Message, "AccessDenied / Wmi:5");
        Assert.Throws<InvalidDataException>(() => VmQueryWorker.ReadResponse(ApiResponse<VmInventorySnapshot>.Empty()));
        var empty = VmQueryWorker.ReadResponse(ApiResponse<VmInventorySnapshot>.Ok(new([], [])));
        Assert.AreEqual(0, empty.Rows.Length);
        Assert.AreEqual(0, empty.Issues.Length);
    }

    [TestMethod]
    public void OptionalQueryIssuesRemainEvidenceAlongsideIdentityChecks()
    {
        var issue = new VmQueryIssue("Network", "query denied", ApiErrorKind.AccessDenied, ApiErrorSource.Wmi, 5);
        var response = ApiResponse<VmInventorySnapshot>.Ok(new(
            [new VmInstance { Id = One, Name = "中文 VM", StateCode = 2 }, new VmInstance { Id = Two, Name = "同名样例", StateCode = 3 }], [issue]));
        var (rows, issues) = VmQueryWorker.ReadResponse(response);
        var capture = Capture(rows) with { Issues = issues };
        var verdict = VmQueryComparison.Evaluate("vm-list.ids", capture);
        Assert.AreEqual(ApiTestStatus.Passed, verdict.Status);
        StringAssert.Contains(verdict.Evidence[^1], "Network");
        StringAssert.Contains(verdict.Evidence[^1], "AccessDenied / Wmi:5");
        var roundTrip = JsonSerializer.Deserialize<VmQueryCapture>(JsonSerializer.Serialize(capture, InspectionJson.Options), InspectionJson.Options)!;
        Assert.AreEqual(issue, roundTrip.Issues!.Single());
    }

    [TestMethod]
    public void ComparisonFindsMembershipDuplicatesNamesAndStatesSeparately()
    {
        var capture = Capture([new(One, "改名", 3), new(One, "改名", 3)]);
        Assert.AreEqual(ApiTestStatus.Failed, VmQueryComparison.Evaluate("vm-list.ids", capture).Status);
        Assert.AreEqual(ApiTestStatus.Failed, VmQueryComparison.Evaluate("vm-list.unique", capture).Status);
        var fields = Capture([new(One, "改名", 3), new(Two, "同名样例", 3)]);
        Assert.AreEqual(ApiTestStatus.Passed, VmQueryComparison.Evaluate("vm-list.ids", fields).Status);
        var name = VmQueryComparison.Evaluate("vm-list.names", fields);
        var state = VmQueryComparison.Evaluate("vm-list.states", fields);
        Assert.AreEqual(ApiTestStatus.Failed, name.Status);
        Assert.AreEqual(ApiTestStatus.Failed, state.Status);
        StringAssert.Contains(name.Evidence[0], "系统：中文 VM");
        StringAssert.Contains(state.Evidence[0], "API 状态码：3");
    }

    [TestMethod]
    public void EnvironmentChangesOnlySkipAffectedChecksAndEmptyCoreIsNeverAllPassed()
    {
        var changed = Capture(after: [new(One, "中文 VM", 3), new(Two, "同名样例", 3)]);
        Assert.AreEqual(ApiTestStatus.Skipped, VmQueryComparison.Evaluate("vm-list.states", changed).Status);
        Assert.AreEqual(ApiTestStatus.Passed, VmQueryComparison.Evaluate("vm-list.names", changed).Status);
        Assert.AreEqual(ApiTestStatus.Passed, VmQueryComparison.Evaluate("vm-list.ids", changed).Status);
        var empty = Capture([]);
        Assert.AreEqual(ApiTestStatus.Failed, VmQueryComparison.Evaluate("vm-list.ids", empty).Status);
        Assert.AreEqual(ApiTestStatus.Skipped, VmQueryComparison.Evaluate("vm-list.unique", empty).Status);
        Assert.AreEqual(ApiTestStatus.Failed, VmQueryComparison.Evaluate("vm-list.names", empty).Status);
    }

    [TestMethod]
    public async Task SharedCallHasLiveStatusPersistsResultsAndDeduplicatesRequests()
    {
        await InDirectory(async directory =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var runner = new VmQueryTestRunner(directory, async (progress, token) =>
            {
                Interlocked.Increment(ref calls); progress("api"); entered.SetResult();
                await release.Task.WaitAsync(token); return Capture();
            });
            var resources = Resources();
            var request = Request(resources, "vm-list.ids", "vm-list.names");
            Assert.IsTrue(runner.TryStart(resources, request, CancellationToken.None, out _));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = runner.Snapshot()!;
            var progressPath = Path.Combine(directory, resources.RunId, "api-tests", running.RunId, "results.json");
            var persisted = DurableJson.Read<ApiTestReport>(progressPath);
            Assert.IsTrue(persisted.Running);
            Assert.AreEqual(running.RunId, persisted.RunId);
            Assert.AreEqual(2, persisted.Items.Count(i => i.Status == ApiTestStatus.Running));
            Assert.AreEqual(2, running.Items.Count(i => i.Status == ApiTestStatus.Running));
            Assert.AreEqual(2, running.Items.Count(i => i.Status == ApiTestStatus.Skipped));
            Assert.IsTrue(runner.TryStart(resources, request, CancellationToken.None, out _));
            Assert.IsFalse(runner.TryStart(resources, request with { RequestId = Guid.NewGuid().ToString() }, CancellationToken.None, out _));
            release.SetResult(); await runner.Completion;
            var done = runner.Snapshot()!;
            Assert.IsFalse(done.Running); Assert.IsNull(done.Error);
            Assert.AreEqual(1, calls); Assert.AreEqual(1, done.ApiCalls);
            Assert.AreEqual(2, done.Items.Count(i => i.Status == ApiTestStatus.Passed));
            Assert.IsTrue(runner.TryStart(resources, request, CancellationToken.None, out _));
            Assert.AreEqual(done.RunId, runner.Snapshot()!.RunId);
            var saved = Path.Combine(directory, resources.RunId, "api-tests", done.RunId);
            var report = JsonSerializer.Deserialize<ApiTestReport>(File.ReadAllText(Path.Combine(saved, "results.json")), InspectionJson.Options)!;
            Assert.AreEqual(done.RunId, report.RunId);
            Assert.IsTrue(File.Exists(Path.Combine(saved, "snapshots.json")));
            StringAssert.Contains(File.ReadAllText(Path.Combine(saved, "results.html")), "const archivedReport =");
            Assert.IsFalse(File.ReadAllText(Path.Combine(saved, "results.html")).Contains("__TEST_REPORT__", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public async Task StaleUnknownEmptyAndIneligibleSelectionsNeverCallCore()
    {
        await InDirectory(directory =>
        {
            var runner = new VmQueryTestRunner(directory, (_, _) => throw new AssertFailedException("执行入口被意外调用。"));
            var resources = Resources();
            var request = Request(resources, "vm-list.ids");
            Assert.IsFalse(runner.TryStart(resources, request with { ResourceRunId = "older" }, CancellationToken.None, out _));
            Assert.IsFalse(runner.TryStart(resources, request with { CheckIds = [] }, CancellationToken.None, out _));
            Assert.IsFalse(runner.TryStart(resources, request with { CheckIds = ["unknown"] }, CancellationToken.None, out _));
            Assert.IsFalse(runner.TryStart(resources, request with { CheckIds = ["vm-list.ids", "vm-list.ids"] }, CancellationToken.None, out _));
            var index = Array.FindIndex(resources.Items, i => i.Check.Id == "hyperv");
            resources.Items[index] = resources.Items[index] with { Result = new(InspectionStatus.Unavailable, "权限待检查") };
            Assert.IsFalse(runner.TryStart(resources, request, CancellationToken.None, out _));
            Assert.IsNull(runner.Snapshot());
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task CancellationFinalizesSelectedItemsAndAllowsAnotherRun()
    {
        await InDirectory(async directory =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = 0;
            var runner = new VmQueryTestRunner(directory, async (progress, token) =>
            {
                if (Interlocked.Increment(ref call) == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
                progress("api"); return Capture();
            });
            var resources = Resources();
            runner.TryStart(resources, Request(resources, "vm-list.ids"), CancellationToken.None, out _);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(runner.Cancel("other-run"));
            Assert.IsTrue(runner.Cancel(runner.Snapshot()!.RunId));
            await runner.Completion;
            Assert.AreEqual(ApiTestStatus.Cancelled, runner.Snapshot()!.Items.Single(i => i.Id == "vm-list.ids").Status);
            Assert.IsTrue(runner.TryStart(resources, Request(resources, "vm-list.ids"), CancellationToken.None, out _));
            await runner.Completion;
            Assert.AreEqual(ApiTestStatus.Passed, runner.Snapshot()!.Items.Single(i => i.Id == "vm-list.ids").Status);
        });
    }

    [TestMethod]
    public async Task CaptureFailureProducesExplicitFailureInsteadOfEmptySuccess()
    {
        await InDirectory(async directory =>
        {
            var runner = new VmQueryTestRunner(directory, (_, _) => throw new UnauthorizedAccessException("权限测试样例"));
            var resources = Resources();
            runner.TryStart(resources, Request(resources, "vm-list.names"), CancellationToken.None, out _);
            await runner.Completion;
            var report = runner.Snapshot()!;
            Assert.IsFalse(report.Running);
            Assert.AreEqual(ApiTestStatus.Failed, report.Items.Single(i => i.Id == "vm-list.names").Status);
            StringAssert.Contains(report.Error!, "权限测试样例");
            Assert.AreEqual(0, report.Items.Count(i => i.Status == ApiTestStatus.Passed));
        });
    }

    private static InspectionReport Resources() => new("resource-run", DateTimeOffset.Now, DateTimeOffset.Now, false, "离线样例", null,
        ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Ready, "满足"))).ToArray());

    [TestMethod]
    public async Task UnregisteredSelectionIsRejectedBeforeStarting()
    {
        await InDirectory(async directory =>
        {
            var resources = Resources();
            const string id = "future-unregistered-test";
            var runner = new VmQueryTestRunner(directory, (_, _) => throw new AssertFailedException("待接入项目调用了执行器。"));
            Assert.IsFalse(runner.TryStart(resources, Request(resources, id), CancellationToken.None, out _));
            await runner.Completion;
            Assert.IsNull(runner.Snapshot());
        });
    }

    [TestMethod]
    public async Task MixedApiSelectionExecutesEachSelectedCaseAndContinuesAfterFailure()
    {
        await InDirectory(async directory =>
        {
            var resources = Resources();
            var cases = resources.Plan.Items.Where(i => i.Eligible && !TestPlanner.IsVmListCheck(i.Id)).Take(2).ToArray();
            var calls = 0;
            var runner = new VmQueryTestRunner(directory, (progress, _) =>
            { calls++; progress("api"); return Task.FromResult(Capture()); }, execute: (id, _, _) => Task.FromResult(
                new ApiCaseResult(id == cases[0].Id ? ApiTestStatus.Failed : ApiTestStatus.Passed, "受控结果", ["固定输入"], 1, 2)));
            Assert.IsTrue(runner.TryStart(resources, Request(resources, "vm-list.ids", cases[0].Id, cases[1].Id), CancellationToken.None, out _));
            await runner.Completion;
            Assert.AreEqual(1, calls);
            Assert.AreEqual(ApiTestStatus.Passed, runner.Snapshot()!.Items.Single(i => i.Id == "vm-list.ids").Status);
            Assert.AreEqual(ApiTestStatus.Failed, runner.Snapshot()!.Items.Single(i => i.Id == cases[0].Id).Status);
            Assert.AreEqual(ApiTestStatus.Passed, runner.Snapshot()!.Items.Single(i => i.Id == cases[1].Id).Status);
            Assert.AreEqual(3, runner.Snapshot()!.ApiCalls);
        });
    }
    private static StartApiTestRequest Request(InspectionReport resources, params string[] ids) => new(resources.RunId, Guid.NewGuid().ToString(), ids);
    [TestMethod]
    public async Task ParallelCasesAreBoundedAndNativeCasesRunExclusively()
    {
        await InDirectory(async directory =>
        {
            var resources = Resources();
            var pure = ApiCaseRegistry.All.Where(c => !c.Exclusive).Take(8).Select(c => c.Id).ToArray();
            var native = ApiCaseRegistry.All.Where(c => c.Exclusive && resources.Plan.Items.Any(i => i.Id == c.Id && i.Eligible)).Select(c => c.Id).ToArray();
            int active = 0, peak = 0, completed = 0;
            var runner = new VmQueryTestRunner(directory, execute: async (id, _, token) =>
            {
                int current = Interlocked.Increment(ref active);
                int seen;
                do { seen = peak; } while (current > seen && Interlocked.CompareExchange(ref peak, current, seen) != seen);
                try
                {
                    if (native.Contains(id)) { Assert.AreEqual(1, current); Assert.IsTrue(completed >= pure.Length); }
                    await Task.Delay(30, token);
                    Interlocked.Increment(ref completed);
                    return new(ApiTestStatus.Passed, "受控调度结果", [], 1, 1);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            var request = Request(resources, [.. pure, .. native]) with { Trial = true };
            Assert.IsTrue(runner.TryStart(resources, request, CancellationToken.None, out _));
            Assert.IsFalse(runner.TryStart(resources, request with { Trial = false }, CancellationToken.None, out _));
            await runner.Completion;
            Assert.IsTrue(peak is > 1 and <= 4);
            Assert.AreEqual(pure.Length + native.Length, completed);
            Assert.IsTrue(runner.Snapshot()!.Trial);
            Assert.IsTrue(runner.Snapshot()!.Items.Where(i => i.Selected).All(i => i.Status == ApiTestStatus.Passed));
        });
    }

    [TestMethod]
    public async Task RecoveryFailureStopsLaterMutationsAndLeavesTheirStatusSkipped()
    {
        await InDirectory(async directory =>
        {
            var resources = Resources();
            var ids = TestPlanner.Catalog.Where(c => c.Api == "IsoBuilderService.BuildUdfIso()").Select(c => c.Id).ToArray();
            int calls = 0;
            var runner = new VmQueryTestRunner(directory, execute: (_, _, _) =>
            {
                calls++;
                return Task.FromResult(new ApiCaseResult(ApiTestStatus.Failed, "原操作失败；恢复记录已保留", ["归属记录"], 1, 3, RecoveryRequired: true));
            });
            Assert.IsTrue(runner.TryStart(resources, Request(resources, ids), CancellationToken.None, out _));
            await runner.Completion;
            var result = runner.Snapshot()!;
            Assert.AreEqual(1, calls);
            Assert.AreEqual(ApiTestStatus.Failed, result.Items.Single(i => i.Id == ids[0]).Status);
            Assert.AreEqual(ApiTestStatus.Skipped, result.Items.Single(i => i.Id == ids[1]).Status);
            StringAssert.Contains(result.Items.Single(i => i.Id == ids[1]).Detail, "尚未调用");
            Assert.IsNotNull(result.Error);
            StringAssert.Contains(result.Error, "IsoBuilderService.BuildUdfIso()");
            StringAssert.Contains(result.Error, "原操作失败；恢复记录已保留");
            Assert.IsFalse(result.Running);
        });
    }

    [TestMethod]
    public async Task CancellationFinalizesQueuedCasesWithoutStartingNativeWork()
    {
        await InDirectory(async directory =>
        {
            var resources = Resources();
            var cases = ApiCaseRegistry.All.Where(c => !c.Exclusive).Take(8).Select(c => c.Id)
                .Concat(ApiCaseRegistry.All.Where(c => c.Exclusive && resources.Plan.Items.Any(i => i.Id == c.Id && i.Eligible)).Select(c => c.Id)).ToArray();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new VmQueryTestRunner(directory, execute: async (id, _, token) =>
            {
                Assert.IsFalse(ApiCaseRegistry.Get(id).Exclusive);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertFailedException("取消后工作仍在继续");
            });
            Assert.IsTrue(runner.TryStart(resources, Request(resources, cases), CancellationToken.None, out _));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(runner.Cancel(runner.Snapshot()!.RunId));
            await runner.Completion;
            Assert.IsTrue(runner.Snapshot()!.Items.Where(i => i.Selected).All(i => i.Status == ApiTestStatus.Cancelled));
        });
    }

    private static async Task InDirectory(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExHyperV-ApiExecution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
