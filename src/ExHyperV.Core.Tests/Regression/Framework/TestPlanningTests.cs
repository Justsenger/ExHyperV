using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestPlanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class TestPlanningTests
{
    [TestMethod]
    [DataRow("running")]
    [DataRow("not-started")]
    [DataRow("interrupted")]
    public void IncompleteScansCannotOpenSelection(string state)
    {
        var report = ReadyReport();
        report = state switch
        {
            "running" => report with { Running = true },
            "not-started" => report with { RunId = "", Finished = null },
            _ => report with { Error = "检查已停止" }
        };
        Assert.IsFalse(report.Plan.Available);
        Assert.IsTrue(report.Plan.Items.All(i => !i.Eligible && i.Reasons.Length > 0));
    }

    [TestMethod]
    public void UnreadableHyperVBlocksEveryQueryCheck()
    {
        var report = WithStatuses(("hyperv", InspectionStatus.Unavailable), ("identity", InspectionStatus.Pending));
        Assert.IsTrue(report.Plan.Available);
        Assert.IsTrue(QueryChecks(report).All(i => !i.Eligible));
        Assert.IsTrue(QueryChecks(report).All(i => i.Reasons.Any(r => r.Contains("无法检查"))));
        Assert.IsTrue(report.Plan.Items.Where(i => i.Implemented && TestPlanner.Catalog.Single(c => c.Id == i.Id).Requirements.Length == 0).All(i => i.Eligible));
    }

    [TestMethod]
    public void QueryChecksDoNotRequireDevicesTemplatesOrWriteAccess()
    {
        var report = WithStatuses(("pcie", InspectionStatus.Pending),
            ("templates", InspectionStatus.Pending), ("disks", InspectionStatus.Pending),
            ("workspace", InspectionStatus.Missing), ("identity", InspectionStatus.Pending),
            ("memory", InspectionStatus.Pending), ("volumes", InspectionStatus.Pending));
        Assert.IsTrue(QueryChecks(report).All(i => i.Eligible));
    }

    [TestMethod]
    public void NoVmMeansTheReadbackComparisonHasNoFixture()
    {
        var report = WithStatuses(("vms", InspectionStatus.Pending));
        Assert.IsTrue(QueryChecks(report).All(i => !i.Eligible));
        Assert.IsTrue(QueryChecks(report).All(i => i.Reasons.Any(r => r.Contains("现有虚拟机"))));
    }

    [TestMethod]
    public void AbsentAndDuplicateCheckResultsCannotBeTreatedAsReady()
    {
        var report = ReadyReport();
        var missing = report with { Items = report.Items.Where(i => i.Check.Id != "platform").ToArray() };
        Assert.IsTrue(QueryChecks(missing).All(i => !i.Eligible));
        var duplicate = report with { Items = [.. report.Items, report.Items.Single(i => i.Check.Id == "hyperv")] };
        Assert.IsTrue(QueryChecks(duplicate).All(i => !i.Eligible));
    }

    [TestMethod]
    public void FullCatalogKeepsStableExecutableIdsAndExplicitPassCriteria()
    {
        var report = ReadyReport();
        CollectionAssert.AreEqual(new[] { "vm-list.ids", "vm-list.unique", "vm-list.names", "vm-list.states" },
            QueryChecks(report).Select(i => i.Id).ToArray());
        Assert.HasCount(TestPlanner.Catalog.Length, report.Plan.Items);
        Assert.AreEqual(report.Plan.Items.Length, report.Plan.Items.Select(i => i.Id).Distinct().Count());
        Assert.IsTrue(report.Plan.Items.All(i => !string.IsNullOrWhiteSpace(i.Expected) && !string.IsNullOrWhiteSpace(i.Api)));
        Assert.IsTrue(report.Plan.Items.All(i => i.Implemented), "计划中的每项都应有显式执行器");
        Assert.IsTrue(report.Plan.Items.All(i => i.PendingChecks.Length == 0), "全部条件检查均须登记");
        Assert.IsTrue(report.Plan.Items.Where(i => i.Eligible).All(i => i.Implemented));
        Assert.IsTrue(report.Plan.Items.Where(i => !i.Implemented).All(i => i.Reasons.Contains(
            ExecutionGaps.Describe(TestPlanner.Catalog.Single(c => c.Id == i.Id)))));
        Assert.IsTrue(report.Plan.Items.All(i => i.Group != "测试框架"));
        Assert.IsTrue(report.Plan.Items.Any(i => i.Api.StartsWith("VmcxEditSession.Commit")));
    }

    [TestMethod]
    public void EnumeratedDevicesNeverStandInForSelectedValidatedFixtures()
    {
        var report = WithStatuses(("test-pcie", InspectionStatus.Pending), ("disks", InspectionStatus.Pending), ("reboot-resume", InspectionStatus.Missing), ("config-isolation", InspectionStatus.Missing));
        Assert.IsTrue(TestConditions.GetBlockers(report, "pcie").Any());
        Assert.IsTrue(TestConditions.GetBlockers(report, "physical").Any());
        Assert.IsTrue(TestConditions.GetBlockers(report, "reboot").Any());
        Assert.IsTrue(TestConditions.GetBlockers(report, "config").Any());
        Assert.IsFalse(TestConditions.GetBlockers(report, "gpupv").Any());
        Assert.IsTrue(report.Plan.Items.Where(i => i.Api.StartsWith("HostPcieService.ExecutePCIeOperationAsync")).All(i => !i.Eligible));
    }

    [TestMethod]
    public void EveryRequiredCheckMustBeReadyAndUnknownConditionsStayBlocked()
    {
        var report = ReadyReport();
        var host = new ResourceCheck("pcie-host", "样例", "宿主准入", "", "");
        var device = new ResourceCheck("test-pcie", "样例", "已指定设备", "", "");
        report = report with { Items = report.Items.Where(i => i.Check.Id is not ("pcie-host" or "test-pcie")).ToArray() };
        report = report with { Items = [.. report.Items, new(host, new(InspectionStatus.Ready, "Server")), new(device, new(InspectionStatus.Pending, "设备变化"))] };
        Assert.AreEqual(1, TestConditions.GetBlockers(report, "pcie").Count());
        report.Items[^1] = new(device, new(InspectionStatus.Ready, "身份与能力已核验"));
        Assert.IsFalse(TestConditions.GetBlockers(report, "pcie").Any());
        Assert.IsTrue(TestConditions.GetBlockers(report, "future-condition").Any());
    }

    private static TestOption[] QueryChecks(InspectionReport report) => report.Plan.Items.Where(i => TestPlanner.IsVmListCheck(i.Id)).ToArray();

    [TestMethod]
    public void ChineseFunctionNamesAndTechnicalApiNamesHaveSeparateFields()
    {
        var items = ReadyReport().Plan.Items;
        CollectionAssert.AreEqual(new[] { "管理层面", "业务层面", "使用说明" },
            items.Select(i => i.Group.Split(" / ")[0]).Distinct().ToArray());
        Assert.IsTrue(items.All(i => i.Group.Any(c => c is >= '\u4e00' and <= '\u9fff')
            && i.Operation.Any(c => c is >= '\u4e00' and <= '\u9fff')));
        Assert.IsTrue(items.All(i => i.Group != i.Api && i.Operation != i.Api));
        Assert.IsTrue(QueryChecks(ReadyReport()).All(i => i.Operation == "获取虚拟机列表"));
        var memory = items.Where(i => i.Api.StartsWith("VmMemoryService.SetVmMemorySettingsAsync")).ToArray();
        Assert.IsTrue(memory.Any(i => i.Name.Contains("启动内存")));
        Assert.IsTrue(memory.All(i => i.Operation == "修改虚拟机内存设置"));
    }

    [TestMethod]
    [DataRow("VmQueryService.GetVmListAsync(", "管理层面 / 虚拟机 / 查询")]
    [DataRow("VmMemoryService.SetVmMemorySettingsAsync(", "管理层面 / 虚拟机 / 修改 / 内存")]
    [DataRow("HostDiskService.GetHostDisksAsync(", "管理层面 / 宿主 / 查询 / 存储")]
    [DataRow("HostDiskService.SetDefaultVirtualHardDiskPathAsync(", "管理层面 / 宿主 / 修改 / 存储")]
    [DataRow("VmcxEditSession.", "管理层面 / 工具 / VMCX 文件")]
    [DataRow("IConsoleInput.SendKey(", "业务层面 / 控制 / 键盘")]
    [DataRow("IConsoleInput.SendPointer(", "业务层面 / 控制 / 鼠标")]
    [DataRow("IConsoleClipboard.", "业务层面 / 剪贴板")]
    [DataRow("ApiResponse<T>.", "使用说明 / 结果、取消和资源释放")]
    [DataRow("SharedBackgroundServices.", "使用说明 / 结果、取消和资源释放")]
    public void PlanGroupsFollowCoreCapabilityOwnership(string apiPrefix, string category)
    {
        var items = ReadyReport().Plan.Items.Where(item => item.Api.StartsWith(apiPrefix, StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(items, apiPrefix);
        Assert.IsTrue(items.All(item => item.Group == category), apiPrefix);
    }

    [TestMethod]
    public void MissingProgramChecksAreDistinctFromMissingMachineResources()
    {
        var report = WithStatuses(("optical", InspectionStatus.Missing));
        Assert.IsEmpty(TestConditions.GetIssues(report, "vnet").ToArray());
        Assert.IsEmpty(TestConditions.GetIssues(report, "vmstate").ToArray());
        var optical = TestConditions.GetIssues(report, "optical").Single();
        Assert.IsFalse(optical.CheckPending);
        StringAssert.Contains(optical.Detail, "条件待补齐");
        var missingResult = report with { Items = report.Items.Where(i => i.Check.Id != "platform").ToArray() };
        Assert.IsFalse(TestConditions.GetIssues(missingResult, "windows").Single().CheckPending);
        var items = WithStatuses(("optical", InspectionStatus.Missing), ("test-vm", InspectionStatus.Missing)).Plan.Items;
        Assert.IsTrue(items.Any(i => i.ResourceReasons.Length > 0));
        Assert.IsTrue(items.All(i => i.PendingChecks.Length == 0));
        Assert.IsTrue(items.All(i => i.Eligible == (i.Implemented && i.ResourceReasons.Length == 0 && i.PendingChecks.Length == 0)));
        Assert.IsTrue(items.All(i => i.Reasons.Length == i.ResourceReasons.Length + i.PendingChecks.Length + (i.Implemented ? 0 : 1)));
    }

    [TestMethod]
    public void SavedAndLiveReportsUseTheSamePlanAndNeverStoreTestVerdicts()
    {
        var report = WithStatuses(("hyperv", InspectionStatus.Unavailable));
        var json = JsonSerializer.Serialize(report, InspectionJson.Options);
        var restored = JsonSerializer.Deserialize<InspectionReport>(json, InspectionJson.Options)!;
        Assert.AreEqual(JsonSerializer.Serialize(report.Plan), JsonSerializer.Serialize(restored.Plan));
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(report.RunId, document.RootElement.GetProperty("plan").GetProperty("runId").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("plan").TryGetProperty("passed", out _));
        Assert.IsTrue(report.Plan.Items.All(i => i.Eligible == (i.Reasons.Length == 0)));
    }

    private static InspectionReport ReadyReport() => new("sample-run", DateTimeOffset.Now, DateTimeOffset.Now,
        false, "离线样例", null, ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Ready, "满足"))).ToArray());

    private static InspectionReport WithStatuses(params (string Id, InspectionStatus Status)[] changes)
    {
        var report = ReadyReport();
        foreach (var (id, status) in changes)
        {
            var index = Array.FindIndex(report.Items, i => i.Check.Id == id);
            report.Items[index] = report.Items[index] with { Result = new(status, "样例") };
        }
        return report;
    }
}
