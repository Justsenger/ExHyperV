using System.Text.Json;
using System.Text.Json.Nodes;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Core.Tests.TestPlanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class HostRestartTests
{
    private static RebootContinuation.Batch NewBatch(string root)
    {
        var resources = new InspectionReport("resource", DateTimeOffset.Now, DateTimeOffset.Now, false, "测试", null, []);
        var report = new ApiTestReport("run", resources.RunId, "request", "重启测试", DateTimeOffset.Now, null, true, "执行中", 0, null,
            [new("case", "测试", "", true, ApiTestStatus.Running, "", [])]);
        return RebootContinuation.CreateBatch(root, resources, report, ["case"], "fixture-sid", "fixture-hash", 5187);
    }

    [TestMethod]
    public void NewBatchesPersistAutomaticPolicyWhileLegacyRecordsRemainManual()
    {
        var batch = NewBatch(Path.GetTempPath());
        Assert.IsTrue(batch.AutomaticReboot);
        var json = JsonSerializer.SerializeToNode(batch, InspectionJson.Options)!.AsObject();
        Assert.IsTrue(json.Deserialize<RebootContinuation.Batch>(InspectionJson.Options)!.AutomaticReboot);
        json.Remove("automaticReboot");
        Assert.IsFalse(json.Deserialize<RebootContinuation.Batch>(InspectionJson.Options)!.AutomaticReboot);
        json["automaticReboot"] = false;
        Assert.IsFalse(json.Deserialize<RebootContinuation.Batch>(InspectionJson.Options)!.AutomaticReboot);
    }

    [TestMethod]
    public async Task TestBootAndRestoreBootKeepTheSameRunAndPersistBeforeRequest()
    {
        await InDirectory(async root =>
        {
            var original = NewBatch(root);
            var batch = original;
            string path = Path.Combine(root, "state.json");
            var calls = new List<string>();
            foreach (bool restoring in new[] { false, true })
            {
                batch = RebootContinuation.PrepareRestart(batch, restoring ? "boot-2" : "boot-1", restoring);
                await RebootContinuation.SubmitRestartAsync(batch,
                    () => { DurableJson.Write(path, batch); calls.Add("save"); return Task.CompletedTask; },
                    () =>
                    {
                        var saved = DurableJson.Read<RebootContinuation.Batch>(path);
                        Assert.AreEqual(batch, saved with { Report = batch.Report, Resources = batch.Resources, CheckIds = batch.CheckIds });
                        Assert.AreEqual(original.Report.RunId, saved.Report.RunId);
                        Assert.AreEqual(original.CoreHash, saved.CoreHash);
                        Assert.AreEqual(original.TaskName, saved.TaskName);
                        Assert.AreEqual(0, saved.Index);
                        calls.Add("restart"); return Task.CompletedTask;
                    });
                batch = DurableJson.Read<RebootContinuation.Batch>(path);
                StringAssert.Contains(batch.Report.Phase, restoring ? "原设置已恢复" : "测试设置");
            }
            Assert.AreEqual(2, batch.Restarts);
            CollectionAssert.AreEqual(new[] { "save", "restart", "save", "restart" }, calls);
        });
    }

    [TestMethod]
    public async Task FailedProgressWritePreventsRestart()
    {
        int requests = 0;
        await Assert.ThrowsExactlyAsync<IOException>(() => RebootContinuation.SubmitRestartAsync(NewBatch(Path.GetTempPath()),
            () => throw new IOException("fixture save failure"), () => { requests++; return Task.CompletedTask; }));
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task LegacyBatchSavesItsWaitWithoutRequestingRestart()
    {
        bool saved = false;
        var batch = RebootContinuation.PrepareRestart(NewBatch(Path.GetTempPath()) with { AutomaticReboot = false }, "boot", false);
        await RebootContinuation.SubmitRestartAsync(batch, () => { saved = true; return Task.CompletedTask; },
            () => throw new AssertFailedException("Legacy batch requested a host restart"));
        Assert.IsTrue(saved);
        StringAssert.Contains(batch.Report.Phase, "等待你确认");
    }

    [TestMethod]
    public async Task FailedRestartPreservesTheSavedIdentityWithoutRetry()
    {
        await InDirectory(async root =>
        {
            string path = Path.Combine(root, "state.json"); int requests = 0;
            var batch = RebootContinuation.PrepareRestart(NewBatch(root), "boot", false);
            await Assert.ThrowsExactlyAsync<IOException>(() => RebootContinuation.SubmitRestartAsync(batch,
                () => { DurableJson.Write(path, batch); return Task.CompletedTask; },
                () => { requests++; throw new IOException("fixture request failure"); }));
            Assert.AreEqual(1, requests);
            var saved = DurableJson.Read<RebootContinuation.Batch>(path);
            Assert.AreEqual(batch.Report.RunId, saved.Report.RunId);
            Assert.AreEqual("boot", saved.WaitingForBoot);
            Assert.AreEqual(1, saved.Restarts);
        });
    }

    [TestMethod]
    public void RestartLimitStopsTheBatch()
        => Assert.ThrowsExactly<IOException>(() => RebootContinuation.PrepareRestart(NewBatch(Path.GetTempPath()) with { Restarts = 40 }, "boot", true));

    [TestMethod]
    [DataRow("Applying")]
    [DataRow("Restoring")]
    public async Task InterruptedNativeSubmissionsPreserveRecoveryAndMakeZeroApiCalls(string phase)
    {
        await InDirectory(async root =>
        {
            var plan = TestPlanner.Catalog.First(RebootApiCases.Supports);
            var state = new RebootApiCases.State(plan.Id, phase, "boot", 0, [0], new(true, 1), new(false, 0), "WinNT", 0, []);
            DurableJson.Write(Path.Combine(root, "reboot-case.json"), state);
            File.WriteAllText(Path.Combine(root, "reboot-owner.json"), "{}");
            var context = new ApiCaseContext(root, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<IOException>(() => RebootApiCases.RunAsync(plan, context));
            Assert.AreEqual(0, context.Calls);
            Assert.IsTrue(File.Exists(Path.Combine(root, "recovery-hold.txt")));
        });
    }

    [TestMethod]
    [DataRow("ready", 0, 1)]
    [DataRow("ready", 5, 1)]
    [DataRow("disabled", 0, 0)]
    [DataRow("settings-disabled", 0, 0)]
    [DataRow("missing-trigger", 0, 0)]
    [DataRow("disabled-trigger", 0, 0)]
    [DataRow("missing-task", 0, 0)]
    public async Task ProductionPowerShellUsesOneNonForcedRestartWithAUsableStartupTask(string taskState, int exitCode, int expectedCalls)
    {
        int commands = 0, delays = 0;
        async Task<JsonElement> Command(string script, object? args, int timeout, bool mutates)
        {
            commands++; Assert.IsTrue(mutates);
            var data = JsonSerializer.SerializeToNode(args)!.AsObject();
            data["fixtureState"] = taskState; data["fixtureExit"] = exitCode; data["expectedCalls"] = expectedCalls;
            // Execute the actual production PowerShell with locally shadowed commands.
            // No scheduled task is changed and no shutdown executable is launched.
            return await WindowsTestCommand.RunAsync("""
                $script:requests=0
                function Get-ScheduledTask {
                    [CmdletBinding()]param($TaskName)
                    if($a.fixtureState -eq 'missing-task'){throw 'fixture task absent'}
                    if($TaskName -ne 'fixture-task'){throw 'wrong task identity'}
                    $trigger=[pscustomobject]@{CimClass=[pscustomobject]@{CimClassName='MSFT_TaskBootTrigger'};Enabled=($a.fixtureState -ne 'disabled-trigger')}
                    [pscustomobject]@{State=$(if($a.fixtureState -eq 'disabled'){'Disabled'}else{'Running'});Settings=[pscustomobject]@{Enabled=($a.fixtureState -ne 'settings-disabled')};Triggers=@($trigger|Where-Object {$a.fixtureState -ne 'missing-trigger'})}
                }
                function Start-Process {
                    param($FilePath,$ArgumentList,$WindowStyle,[switch]$Wait,[switch]$PassThru)
                    $script:requests++
                    if($FilePath -ne (Join-Path $env:SystemRoot 'System32\shutdown.exe') -or ($ArgumentList -join ' ') -ne '/r /t 0 /d p:4:1' -or $WindowStyle -ne 'Hidden' -or !$Wait -or !$PassThru){throw 'unexpected restart command'}
                    [pscustomobject]@{ExitCode=$a.fixtureExit}
                }
                try {
                """ + "\n" + script + "\n" + """
                } finally {
                    if($script:requests -ne $a.expectedCalls){throw ('Unexpected restart calls: '+$script:requests)}
                }
                """, data, timeout, mayMutate: false);
        }
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => HostRestart.RequestAsync("fixture-task", Command,
            duration => { Assert.AreEqual(TimeSpan.FromMinutes(2), duration); delays++; return Task.CompletedTask; }));
        Assert.AreEqual(1, commands);
        Assert.AreEqual(1, delays);
        StringAssert.Contains(failure.Message, "两分钟");
        if (taskState != "ready" || exitCode != 0) Assert.IsNotNull(failure.InnerException);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShutdownEndingTheCommandPipeWaitsForBootWithoutAnEarlyRecoveryHold(bool timeout)
    {
        await InDirectory(async root =>
        {
            string? prior = WindowsTestCommand.RecoveryDirectory.Value;
            WindowsTestCommand.RecoveryDirectory.Value = root;
            try
            {
                int requests = 0;
                await Assert.ThrowsExactlyAsync<IOException>(() => HostRestart.RequestAsync("fixture",
                    (_, _, _, _) =>
                    {
                        requests++;
                        Assert.IsNull(WindowsTestCommand.RecoveryDirectory.Value);
                        throw timeout ? new TimeoutException("fixture interrupted command") : new JsonException("fixture ended pipe");
                    }, _ =>
                    {
                        Assert.IsFalse(File.Exists(Path.Combine(root, "recovery-hold.txt")));
                        Assert.AreEqual(root, WindowsTestCommand.RecoveryDirectory.Value);
                        return Task.CompletedTask;
                    }));
                Assert.AreEqual(1, requests);
                Assert.AreEqual(root, WindowsTestCommand.RecoveryDirectory.Value);
            }
            finally { WindowsTestCommand.RecoveryDirectory.Value = prior; }
        });
    }

    private static async Task InDirectory(Func<string, Task> test)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExHyperV-HostRestart-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally { Directory.Delete(root, true); }
    }
}
