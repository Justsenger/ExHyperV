using System.Text.Json;
using ExHyperV.Core.Tests.Automation;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class TemplateCopyPreparationTests
{
    [TestMethod]
    [DataRow("Running", false)]
    [DataRow("Running", true)]
    [DataRow("Off", false)]
    public async Task KnownCopyOutcomeRestoresTheOriginalPowerState(string original, bool copyFails)
    {
        await InDirectory(async root =>
        {
            var calls = new List<string>();
            async Task<int> Copy()
            {
                calls.Add("copy");
                Assert.AreEqual("prepared", Phase(root));
                await Task.CompletedTask;
                if (copyFails) throw new InvalidDataException("copy failed");
                return 42;
            }
            var action = () => TemplatePowerLease.CopyAsync(root, Configuration(), Copy, Backend(root, original, calls));
            if (copyFails) await Assert.ThrowsExactlyAsync<InvalidDataException>(action);
            else Assert.AreEqual(42, await action());
            CollectionAssert.AreEqual(original == "Running" ? new[] { "read", "shutdown", "copy", "restore" } : new[] { "read", "copy" }, calls);
            Assert.AreEqual("restored", Phase(root));
            Assert.IsFalse(TemplatePowerLease.HasPendingChange(root));
        });
    }

    [TestMethod]
    public async Task UnknownNativeOutcomeRetainsPowerJournalAndSkipsAutomaticRestore()
    {
        await InDirectory(async root =>
        {
            var calls = new List<string>();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TemplatePowerLease.CopyAsync<int>(root, Configuration(), () =>
            {
                File.WriteAllText(Path.Combine(root, "recovery-hold.txt"), "unknown native job");
                throw new InvalidDataException("unknown");
            }, Backend(root, "Running", calls)));
            CollectionAssert.AreEqual(new[] { "read", "shutdown" }, calls);
            Assert.IsTrue(TemplatePowerLease.HasPendingChange(root));
            Assert.AreEqual("prepared", Phase(root));
        });
    }

    [TestMethod]
    [DataRow("shutdown")]
    [DataRow("restore")]
    public async Task InterruptedPowerCommandPreservesRecoveryHold(string failAt)
    {
        await InDirectory(async root =>
        {
            var calls = new List<string>();
            await Assert.ThrowsExactlyAsync<IOException>(() => TemplatePowerLease.CopyAsync(root, Configuration(), () => Task.FromResult(1), Backend(root, "Running", calls, failAt)));
            Assert.IsTrue(File.Exists(Path.Combine(root, "recovery-hold.txt")));
            Assert.IsTrue(TemplatePowerLease.HasPendingChange(root));
            Assert.AreEqual(failAt == "shutdown" ? "shutdown-requested" : "restore-requested", Phase(root));
        });
    }

    [TestMethod]
    public async Task RejectedShutdownSkipsCopyAndLeavesRunningTemplateAlone()
    {
        await InDirectory(async root =>
        {
            var calls = new List<string>();
            await Assert.ThrowsExactlyAsync<ApiCheckSkippedException>(() => TemplatePowerLease.CopyAsync<int>(root, Configuration(), () => throw new AssertFailedException("copy must not run"), Backend(root, "Running", calls, reject: true)));
            CollectionAssert.AreEqual(new[] { "read", "shutdown" }, calls);
            Assert.IsFalse(TemplatePowerLease.HasPendingChange(root));
            Assert.IsFalse(File.Exists(Path.Combine(root, "recovery-hold.txt")));
        });
    }

    private static InspectionConfiguration Configuration() => new() { TemplateVmId = "3c6723c1-e5d0-4a69-bb25-2f5177fe33ce" };
    private static string Phase(string root) => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "template-power.json"))).RootElement.GetProperty("phase").GetString()!;
    private static Func<string, object?, int, bool, Task<JsonElement>> Backend(string root, string original, List<string> calls, string? failAt = null, bool reject = false)
        => (script, args, timeout, mutates) =>
        {
            string step = script.Contains("InitiateShutdown") ? "shutdown" : script.Contains("Start-VM") ? "restore" : "read";
            calls.Add(step);
            if (mutates) Assert.AreEqual(step == "shutdown" ? "shutdown-requested" : "restore-requested", Phase(root));
            if (step == failAt) throw new IOException("unknown outcome");
            object result = step == "read" ? new { name = "fixture", state = original }
                : step == "shutdown" && reject ? new { off = false, rejected = true, unchanged = true, reason = "正常关机接口尚未就绪" }
                : step == "shutdown" ? new { off = true } : new { running = true };
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        };
    private static async Task InDirectory(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV-TemplatePreparation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { Directory.Delete(root, true); }
    }
}
