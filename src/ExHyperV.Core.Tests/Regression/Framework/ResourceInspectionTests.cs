using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ResourceInspectionTests
{
    [TestMethod]
    public async Task DuplicateStartDoesNotOverlapAndMissingResourcesAreNotPasses()
    {
        await InDirectory(async directory =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var scanner = new ResourceScanner(directory, null, async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
                return new(InspectionStatus.Missing, "缺少可选设备");
            });
            Assert.IsTrue(scanner.TryStart(CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(scanner.TryStart(CancellationToken.None));
            release.SetResult();
            await scanner.Completion;
            var report = scanner.Snapshot();
            Assert.AreEqual(ResourceProbes.Checks.Length, calls);
            Assert.IsFalse(report.Running);
            Assert.IsTrue(report.Items.All(i => i.Result.Status == InspectionStatus.Missing));
            Assert.IsNull(report.Error);
            var saved = JsonSerializer.Deserialize<InspectionReport>(File.ReadAllText(Path.Combine(directory, report.RunId, "resources.json")), InspectionJson.Options)!;
            Assert.AreEqual(report.RunId, saved.RunId);
            Assert.AreEqual(0, saved.Items.Count(i => i.Result.Status == InspectionStatus.Ready));
            Assert.IsTrue(File.Exists(Path.Combine(directory, report.RunId, "resources.html")));
        });
    }

    [TestMethod]
    public async Task FailedProbeDoesNotHideLaterResults()
    {
        await InDirectory(async directory =>
        {
            var scanner = new ResourceScanner(directory, null, (check, _) => check.Id switch
            {
                "platform" => throw new UnauthorizedAccessException(),
                "identity" => Task.FromResult(new ResourceResult(InspectionStatus.Waiting, "未完成")),
                _ => Task.FromResult(new ResourceResult(InspectionStatus.Ready, "成功"))
            });
            scanner.TryStart(CancellationToken.None);
            await scanner.Completion;
            var report = scanner.Snapshot();
            Assert.AreEqual(InspectionStatus.Unavailable, report.Items[0].Result.Status);
            Assert.AreEqual(InspectionStatus.Unavailable, report.Items[1].Result.Status);
            Assert.IsTrue(report.Items.Skip(2).All(i => i.Result.Status == InspectionStatus.Ready));
        });
    }

    [TestMethod]
    public async Task CancelledInspectionPreservesCompletedItemsAndMarksRemainder()
    {
        await InDirectory(async directory =>
        {
            using var cancellation = new CancellationTokenSource();
            var scanner = new ResourceScanner(directory, null, (check, token) =>
            {
                if (check.Id == "identity") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.FromResult(new ResourceResult(InspectionStatus.Ready, "已读"));
            });
            scanner.TryStart(cancellation.Token);
            await scanner.Completion;
            var report = scanner.Snapshot();
            Assert.AreEqual(InspectionStatus.Ready, report.Items[0].Result.Status);
            Assert.IsTrue(report.Items.Skip(1).All(i => i.Result.Status == InspectionStatus.Unavailable));
            Assert.IsNotNull(report.Error);
            Assert.IsFalse(report.Running);
        });
    }

    [TestMethod]
    public void ReportTreatsDeviceNamesAsTextAndDistinguishesPermissionErrors()
    {
        var json = JsonSerializer.Serialize(new ResourceResult(InspectionStatus.Pending, "</script><script>alert(1)</script>"), InspectionJson.Options);
        Assert.IsFalse(json.Contains("</script>", StringComparison.OrdinalIgnoreCase));
        var html = InspectionPage.Render(json);
        Assert.IsFalse(html.Contains("<script>alert(1)</script>", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("__REPORT__", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("__SESSION__", StringComparison.Ordinal));
        Assert.AreEqual(InspectionStatus.Missing, ResourceProbes.Failure(new FileNotFoundException()).Status);
        Assert.AreEqual(InspectionStatus.Unavailable, ResourceProbes.Failure(new UnauthorizedAccessException()).Status);
        Assert.AreEqual(InspectionStatus.Unavailable, ResourceProbes.Failure(new IOException()).Status);
        Assert.AreEqual(ResourceProbes.Checks.Length, ResourceProbes.Checks.Select(c => c.Id).Distinct().Count());
    }

    [TestMethod]
    public async Task LivePageTracksChangesAndRetainsLastVersionDuringSave()
    {
        await InDirectory(directory =>
        {
            var path = Path.Combine(directory, "Page.html");
            var original = InspectionPage.ReadTemplate();
            File.WriteAllText(path, original);
            var source = new InspectionPageSource(path);
            var first = source.Read();
            File.WriteAllText(path, original.Replace("第二步：选择测试项目", "第二步：选择检查点", StringComparison.Ordinal));
            var changed = source.Read();
            Assert.AreNotEqual(first.Revision, changed.Revision);
            Assert.IsTrue(changed.Html.Contains("第二步：选择检查点", StringComparison.Ordinal));

            File.WriteAllText(path, "<html>");
            var partial = source.Read();
            Assert.AreEqual(changed.Revision, partial.Revision);
            Assert.AreEqual(changed.Html, partial.Html);
            Assert.IsNotNull(partial.Error);
            File.Delete(path);
            Assert.AreEqual(changed.Revision, source.Read().Revision);
            File.WriteAllText(path, original);
            Assert.AreEqual(first.Revision, source.Read().Revision);
            Assert.IsNull(source.Read().Error);
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void PackagedPageAndSavedReportsKeepIndependentTemplates()
    {
        var packaged = new InspectionPageSource(null).Read();
        Assert.IsNull(packaged.Error);
        var live = InspectionPage.Render(token: "token", template: packaged.Html, sessionJson: "{\"instanceId\":\"sample\"}");
        Assert.IsTrue(live.Contains("loadedSession = {\"instanceId\":\"sample\"}", StringComparison.Ordinal));
        var saved = InspectionPage.Render("{}");
        Assert.IsTrue(saved.Contains("loadedSession = null", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("__SESSION__", StringComparison.Ordinal));
        Assert.IsFalse(live.Contains("__TABLER_CSS__", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("__TABLER_CSS__", StringComparison.Ordinal));
        Assert.IsTrue(live.Contains("--tblr-", StringComparison.Ordinal));
        Assert.IsTrue(saved.Contains("--tblr-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GpuInventoryKeepsFourChipVendorsAndEveryDevice()
    {
        var result = GpuInventory.Describe([
            (@"PCI\VEN_10DE&DEV_1234\ONE", "NVIDIA 卡一", "ASUS", "1.0"),
            (@"pci\ven_10de&dev_1234\TWO", "NVIDIA 卡二", "MSI", "1.1"),
            (@"PCI\VEN_8086&DEV_1234\ONE", "Intel 显卡", "OEM", "2.0"),
            (@"PCI\VEN_1002&DEV_1234\ONE", "AMD 显卡", "OEM", "3.0"),
            (@"ACPI\QCOM0C36\ONE", "Adreno 显卡", "OEM", "4.0"),
            (@"ROOT\RDPIDD\0000", "Microsoft Remote Display Adapter", "Microsoft", "10.0"),
            (@"PCI\VEN_102B&DEV_1234\ONE", "其他厂商显卡", "Other", "5.0")
        ]);
        Assert.AreEqual(InspectionStatus.Ready, result.Status);
        Assert.HasCount(5, result.Items!);
        Assert.IsTrue(result.Items![0].StartsWith("NVIDIA ·", StringComparison.Ordinal));
        Assert.IsTrue(result.Items[1].Contains("NVIDIA 卡二", StringComparison.Ordinal));
        Assert.IsTrue(result.Items[2].StartsWith("Intel ·", StringComparison.Ordinal));
        Assert.IsTrue(result.Items[3].StartsWith("AMD ·", StringComparison.Ordinal));
        Assert.IsTrue(result.Items[4].StartsWith("Qualcomm ·", StringComparison.Ordinal));
        Assert.IsTrue(result.Items.All(item => item.Contains("设备 ID", StringComparison.Ordinal)));
        Assert.IsFalse(result.Items.Any(item => item.Contains("Microsoft", StringComparison.Ordinal) || item.Contains("其他厂商", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GpuInventorySupportsCoreManufacturerFallbacksAndQualcommPci()
    {
        var result = GpuInventory.Describe([
            (@"ACPI\GPU0\ONE", "卡一", "NVIDIA Corporation", "1"),
            (@"ACPI\GPU1\ONE", "卡二", "Intel Corporation", "2"),
            (@"ACPI\GPU2\ONE", "卡三", "Advanced Micro Devices, Inc.", "3"),
            (@"ACPI\GPU3\ONE", "卡四", "AMD", "4"),
            (@"ACPI\GPU4\ONE", "卡五", "Qualcomm Incorporated", "5"),
            (@"PCI\VEN_17CB&DEV_1234\ONE", "卡六", "OEM", "6")
        ]);
        Assert.HasCount(6, result.Items!);
        Assert.AreEqual(2, result.Items!.Count(item => item.StartsWith("AMD ·", StringComparison.Ordinal)));
        Assert.AreEqual(2, result.Items!.Count(item => item.StartsWith("Qualcomm ·", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GpuInventoryDoesNotPassForOnlyRemoteAdaptersOrMissingDriver()
    {
        Assert.AreEqual(InspectionStatus.Missing, GpuInventory.Describe([
            (@"ROOT\RDPIDD\0000", "Microsoft Remote Display Adapter", "Microsoft", "10.0")
        ]).Status);
        Assert.AreEqual(InspectionStatus.Missing, GpuInventory.Describe([]).Status);
        var result = GpuInventory.Describe([(@"ACPI\QCOM0C36\ONE", "Adreno", "Qualcomm", "")]);
        Assert.AreEqual(InspectionStatus.Pending, result.Status);
        Assert.IsTrue(result.Items![0].Contains("驱动 未取得", StringComparison.Ordinal));
    }

    private static async Task InDirectory(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExHyperV-ResourceInspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
