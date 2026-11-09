using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class DeviceResourceTests
{
    private static HostDiskInfo Disk(int number = 1, string id = "disk-1", bool available = true, long bytes = 1024) => new()
    { Number = number, UniqueId = id, SerialNumber = "serial", FriendlyName = "测试盘", SizeBytes = bytes, CanPassthrough = available, Status = available ? "可用" : "系统盘" };

    [TestMethod]
    public void DiskSelectionFollowsIdentityAcrossNumberChangesAndRejectsReplacements()
    {
        var original = TestDeviceResources.DiskChoices([Disk()], new HashSet<int>()).Single();
        var saved = new RegisteredDevice(original.Id, original.Fingerprint!, original.Name);
        var moved = TestDeviceResources.DiskChoices([Disk(number: 3)], new HashSet<int>());
        var result = TestDeviceResources.DescribeSelection("物理测试盘", saved, moved, "空", "受保护");
        Assert.AreEqual(InspectionStatus.Ready, result.Status);
        StringAssert.Contains(result.Detail, "磁盘 3");
        var replaced = TestDeviceResources.DiskChoices([Disk(bytes: 2048)], new HashSet<int>());
        result = TestDeviceResources.DescribeSelection("物理测试盘", saved, replaced, "空", "受保护");
        Assert.AreEqual(InspectionStatus.Missing, result.Status);
        StringAssert.Contains(result.Detail, "身份发生变化");
    }

    [TestMethod]
    public void ProtectedDisksAndAmbiguousIdentitiesStayDisabled()
    {
        var choices = TestDeviceResources.DiskChoices([Disk(0, "system", false), Disk(1, "cache"), Disk(2, "free")], new HashSet<int> { 1 });
        Assert.IsNotNull(choices[0].UnavailableReason);
        StringAssert.Contains(choices[1].UnavailableReason!, "缓存");
        Assert.IsNull(choices[2].UnavailableReason);
        choices = TestDeviceResources.DiskChoices([Disk(1), Disk(2)], new HashSet<int>());
        Assert.IsTrue(choices.All(c => c.UnavailableReason == "设备身份重复"));
    }

    [TestMethod]
    public void SelectionIsActionableAndAbsentHardwareIsMissing()
    {
        var choices = TestDeviceResources.DiskChoices([Disk()], new HashSet<int>());
        var result = TestDeviceResources.DescribeSelection("物理测试盘", null, choices, "未发现磁盘", "受保护");
        Assert.AreEqual(InspectionStatus.Pending, result.Status);
        StringAssert.Contains(result.Detail, "上方");
        StringAssert.Contains(result.Detail, "保存并检查");
        result = TestDeviceResources.DescribeSelection("测试光驱", null, [], "未发现光驱，相关测试跳过。", "受保护");
        Assert.AreEqual(InspectionStatus.Missing, result.Status);
        StringAssert.Contains(result.Detail, "未发现光驱");
    }

    [TestMethod]
    public void DiskAndOpticalSelectionsPersistAndReachThePlan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExHyperV-DeviceResources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "resources.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new InspectionConfiguration { WorkDirectory = Path.Combine(directory, "cache") }, InspectionJson.Options));
            var disk = TestDeviceResources.DiskChoices([Disk()], new HashSet<int>()).Single();
            var optical = new ResourceChoice("optical-1", "测试光驱", Fingerprint: "optical-fingerprint");
            var report = new InspectionReport("devices", DateTimeOffset.Now, DateTimeOffset.Now, false, "样例", null,
                ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Ready, "已读", Choices:
                    c.Id == "disks" ? [disk] : c.Id == "optical" ? [optical] : null))).ToArray());
            var store = new ResourceRegistration(path);
            var request = new SaveRegistrationRequest(store.Read().Revision, report.RunId, null, [], disk.Id, optical.Id);
            store.Save(request, report);
            var config = new ResourceRegistration(path).Read().Configuration;
            Assert.AreEqual(disk.Id, config.PhysicalDisk!.Id);
            Assert.AreEqual(optical.Id, config.OpticalDrive!.Id);
            Assert.IsEmpty(TestConditions.GetIssues(report, "physical").ToArray());
            Assert.IsEmpty(TestConditions.GetIssues(report, "optical").ToArray());
            request = request with { Revision = store.Read().Revision };
            Assert.ThrowsExactly<ArgumentException>(() => store.Save(request with { PhysicalDiskId = "invented" }, report));
            var index = Array.FindIndex(report.Items, i => i.Check.Id == "disks");
            report.Items[index] = report.Items[index] with { Result = new(InspectionStatus.Ready, "已读", Choices: [disk with { UnavailableReason = "系统盘" }]) };
            Assert.ThrowsExactly<ArgumentException>(() => store.Save(request, report));
            store.Save(request with { PhysicalDiskId = null, OpticalDriveId = null }, report);
            config = new ResourceRegistration(path).Read().Configuration;
            Assert.IsNull(config.PhysicalDisk); Assert.IsNull(config.OpticalDrive);
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            Assert.IsTrue(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            Directory.Delete(resolved, recursive: true);
        }
    }

    [TestMethod]
    public void LoginConditionsUseTheCredentialProbeResult()
    {
        Assert.IsFalse(ResourceProbes.Checks.Any(c => c.Id == "guest-tools"));
        var report = new InspectionReport("devices", DateTimeOffset.Now, DateTimeOffset.Now, false, "样例", null, []);
        var issue = TestConditions.GetIssues(report, "guest").Single();
        Assert.IsFalse(issue.CheckPending);
        StringAssert.Contains(issue.Detail, "检查结果缺失");
        var check = ResourceProbes.Checks.Single(c => c.Id == "vm-credentials");
        report = report with { Items = [new(check, new(InspectionStatus.Missing, "凭据文件缺失"))] };
        var missing = TestConditions.GetIssues(report, "guest").Single();
        Assert.IsFalse(missing.CheckPending);
        StringAssert.Contains(missing.Detail, "凭据文件缺失");
        report = report with { Items = [new(check, new(InspectionStatus.Ready, "凭据可读取"))] };
        Assert.IsEmpty(TestConditions.GetIssues(report, "guest").ToArray());
        Assert.AreEqual("待选择", InspectionPage.Label(InspectionStatus.Pending));
    }
}
