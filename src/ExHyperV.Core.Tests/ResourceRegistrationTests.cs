using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestPlanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ResourceRegistrationTests
{
    private const string VmId = "f40c210f-9102-40c7-aec0-31fbb6d9c409";
    private const string SwitchId = "123d2882-734e-4157-ae0c-a27717c5c579";
    private static InspectionReport Report() => new("registration-fixture", DateTimeOffset.Now, DateTimeOffset.Now,
        false, "离线样例", null, ResourceProbes.Checks.Select(check => new InspectionItem(check,
            new(InspectionStatus.Ready, "已读", Choices: check.Id switch
            {
                "vms" => [new(VmId, "模板甲", "已关机")],
                "switches" => [new(SwitchId, "专用网络")],
                "pcie" => [new("PCI\\TEST", "测试设备", LocationPath: "PCIROOT(0)#PCI(0100)")],
                _ => null
            }))).ToArray());

    private static SaveRegistrationRequest Request(ResourceRegistration store, string directory) => new(
        store.Read().Revision, Report().RunId, VmId, ["PCI\\TEST"]);

    private static ResourceRegistration Store(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(
            new InspectionConfiguration { WorkDirectory = Path.Combine(directory, "work") }, InspectionJson.Options));
        return new ResourceRegistration(path);
    }

    [TestMethod]
    public void SavedSelectionsSurviveNewStoreAndKeepStableIdentities()
    {
        InDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = Store(directory);
            var saved = store.Save(Request(store, directory), Report());
            var reopened = new ResourceRegistration(path).Read();
            Assert.AreEqual(saved.Revision, reopened.Revision);
            Assert.AreEqual(VmId, reopened.Configuration.TemplateVmId);
            Assert.AreEqual(VmId, reopened.Configuration.GpuTemplateVmId);
            Assert.IsNull(reopened.Configuration.TestSwitchId);
            Assert.AreEqual("PCIROOT(0)#PCI(0100)", reopened.Configuration.PcieDevices.Single().LocationPath);
            Assert.IsTrue(Directory.Exists(reopened.Configuration.WorkDirectory));
            Assert.IsEmpty(Directory.GetFiles(reopened.Configuration.WorkDirectory!));
            var result = ResourceProbes.DescribeTemplate(VmId, "普通虚拟机模板", [new(VmId, "已改名", "运行中")]);
            Assert.AreEqual(InspectionStatus.Ready, result.Status);
            StringAssert.Contains(result.Detail, "已改名");
        });
    }

    [TestMethod]
    public void StaleUpdatesAndInventedDevicesLeaveSavedChoicesIntact()
    {
        InDirectory(directory =>
        {
            var store = Store(directory);
            var stale = Request(store, directory);
            var saved = store.Save(stale, Report());
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(stale with { TemplateVmId = null }, Report()));
            var request = Request(store, directory);
            Assert.ThrowsExactly<ArgumentException>(() => store.Save(request with { TemplateVmId = Guid.NewGuid().ToString() }, Report()));
            Assert.ThrowsExactly<ArgumentException>(() => store.Save(request with { PcieIds = ["PCI\\OTHER"] }, Report()));
            Assert.AreEqual(saved.Revision, store.Read().Revision);
        });
    }

    [TestMethod]
    public void DirectoryFailureAndInterruptedScansPreserveConfiguration()
    {
        InDirectory(directory =>
        {
            var file = Path.Combine(directory, "existing.txt");
            File.WriteAllText(file, "保持原内容");
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new InspectionConfiguration { WorkDirectory = file }, InspectionJson.Options));
            var store = Store(directory);
            var request = Request(store, directory);
            var revision = store.Read().Revision;
            Assert.ThrowsExactly<IOException>(() => store.Save(request, Report()));
            Assert.AreEqual("保持原内容", File.ReadAllText(file));
            Assert.AreEqual(revision, store.Read().Revision);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(request, Report() with { Running = true }));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(request with { ResourceRunId = "old" }, Report()));
            Assert.ThrowsExactly<ArgumentException>(() => ResourceRegistration.NormalizeDirectory("relative"));
            Assert.ThrowsExactly<ArgumentException>(() => ResourceRegistration.NormalizeDirectory(Path.GetPathRoot(directory)));
        });
    }

    [TestMethod]
    public void MissingSelectionAndRemovedTemplateExplainTheNextAction()
    {
        var missing = ResourceProbes.DescribeTemplate(null, "GPU 测试模板", []);
        Assert.AreEqual(InspectionStatus.Pending, missing.Status);
        StringAssert.Contains(missing.Detail, "上方选择");
        StringAssert.Contains(missing.Detail, "保存并检查");
        var removed = ResourceProbes.DescribeTemplate(VmId, "GPU 测试模板", []);
        Assert.AreEqual(InspectionStatus.Missing, removed.Status);
        StringAssert.Contains(removed.Detail, "重新选择");
    }

    [TestMethod]
    public void ClearingSelectionIsSavedAndLegacySampleSettingsAreRetained()
    {
        InDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new InspectionConfiguration { WorkDirectory = Path.Combine(directory, "work"), GuestTemplatePaths = ["legacy.vhdx"], SampleFiles = ["old.iso"] }, InspectionJson.Options));
            var store = new ResourceRegistration(path);
            store.Save(Request(store, directory), Report());
            store.Save(Request(store, directory) with { TemplateVmId = null, PcieIds = [] }, Report());
            var config = store.Read().Configuration;
            Assert.IsNull(config.TemplateVmId); Assert.IsNull(config.GpuTemplateVmId);
            Assert.IsEmpty(config.PcieDevices); Assert.IsNull(config.TestSwitchId);
            CollectionAssert.AreEqual(new[] { "legacy.vhdx" }, config.GuestTemplatePaths);
            CollectionAssert.AreEqual(new[] { "old.iso" }, config.SampleFiles);
        });
    }

    [TestMethod]
    public void CapacityUsesMeasuredResourcesAndTemplateChoiceReachesPlan()
    {
        var report = Report();
        Assert.IsEmpty(TestConditions.GetIssues(report, "capacity").ToArray());
        Assert.IsEmpty(TestConditions.GetIssues(report, "template").ToArray());
        var index = Array.FindIndex(report.Items, i => i.Check.Id == "workspace");
        report.Items[index] = report.Items[index] with { Result = new(InspectionStatus.Unavailable, "缓存目录读写失败。") };
        var issue = TestConditions.GetIssues(report, "capacity").Single();
        Assert.IsFalse(issue.CheckPending);
        StringAssert.Contains(issue.Detail, "缓存目录读写失败");
    }

    [TestMethod]
    public void DefaultCacheAndLegacyGpuTemplateNeedNoExtraUserInput()
    {
        InDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            Assert.AreEqual(InspectionConfiguration.DefaultWorkDirectory, new ResourceRegistration(path).Read().Configuration.WorkDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new InspectionConfiguration { WorkDirectory = null, GpuTemplateVmId = VmId }, InspectionJson.Options));
            var config = InspectionConfiguration.Read(path);
            Assert.AreEqual(InspectionConfiguration.DefaultWorkDirectory, config.WorkDirectory);
            Assert.AreEqual(VmId, config.TemplateVmId);
            Assert.AreEqual(config.TemplateVmId, config.GpuTemplateVmId);
            Assert.AreEqual(config, new ResourceRegistration(path).Read().Configuration);
        });
    }

    [TestMethod]
    public void AutomaticFixturesBecomeEligibleWhenTheirCreationResourcesAreReady()
    {
        var report = Report();
        Assert.IsEmpty(TestConditions.GetIssues(report, "vnet").ToArray());
        Assert.IsEmpty(TestConditions.GetIssues(report, "media").ToArray());
        Assert.IsEmpty(TestConditions.GetIssues(report, "template").ToArray());
        foreach (var (condition, check) in new[] { ("vnet", "test-vm"), ("media", "workspace") })
        {
            var missing = report with { Items = report.Items.Select(i => i.Check.Id == check
                ? i with { Result = new(InspectionStatus.Missing, "创建资源缺失") } : i).ToArray() };
            var issue = TestConditions.GetIssues(missing, condition).Single();
            Assert.IsFalse(issue.CheckPending);
            StringAssert.Contains(issue.Detail, "创建资源缺失");
        }
    }

    [TestMethod]
    public async Task ScanKeepsOneConfigurationSnapshotThroughoutRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExHyperV-Registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new InspectionConfiguration { TemplateVmId = VmId }, InspectionJson.Options));
            var scanner = new ResourceScanner(Path.Combine(directory, "reports"), path, (_, _) =>
            {
                File.WriteAllText(path, "{}");
                return Task.FromResult(new ResourceResult(InspectionStatus.Ready, "样例"));
            });
            scanner.TryStart(CancellationToken.None); await scanner.Completion;
            var frozen = InspectionConfiguration.Read(Path.Combine(directory, "reports", scanner.Snapshot().RunId, "resource-settings.json"));
            Assert.AreEqual(VmId, frozen.TemplateVmId);
            Assert.IsNull(InspectionConfiguration.Read(path).TemplateVmId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void InDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExHyperV-Registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
