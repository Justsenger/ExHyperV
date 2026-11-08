using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;

namespace ExHyperV.Core.Tests.TestPlanning;

internal sealed record TestOption(string Id, string Group, string Name, string Description, bool Eligible,
    string[] Reasons, string Api, string Expected, string[] Conditions, string[] Recovery, bool Implemented,
    string Operation, string[] ResourceReasons, string[] PendingChecks);
internal sealed record TestPlan(string RunId, bool Available, string? Reason, string Description, TestOption[] Items);
internal sealed record CatalogTest(string Id, string Api, string Group, string Operation, string Name, string Expected,
    string[] Requirements, string[] Recovery, int Order);

internal static class TestPlanner
{
    // A maintained test specification, independent of runtime API discovery.
    internal static readonly CatalogTest[] Catalog = LoadCatalog();
    internal static bool IsVmListCheck(string id) => id is "vm-list.ids" or "vm-list.unique" or "vm-list.names" or "vm-list.states";
    internal static bool HasExecutor(string id) => IsVmListCheck(id) || ApiCaseRegistry.Supports(id);

    internal static TestPlan Analyze(InspectionReport report)
    {
        var reason = report.Running || report.Finished is null || string.IsNullOrEmpty(report.RunId)
            || report.Items.Any(i => i.Result.Status is InspectionStatus.Waiting or InspectionStatus.Running)
            ? "请先完成资源检查"
            : report.Error is not null ? "资源检查中断，请重新检查" : null;
        var items = Catalog.Select(check =>
        {
            var issues = reason is not null ? [new ConditionIssue(reason)] : check.Requirements
                .SelectMany(key => TestConditions.GetIssues(report, key)).Distinct().ToArray();
            if (check.Id == "T-3d0663ec3231e3a0-22" && Environment.OSVersion.Version.Build == 29671
                && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
                issues = [.. issues, new ConditionIssue("已知故障隔离：Build 29671 ARM64 实测启用 CXL 时 vmms.exe 发生访问冲突（0xc0000005）。首轮失败证据已保留，待系统修复后复测。")];
            var implemented = HasExecutor(check.Id);
            var reasons = issues.Select(issue => issue.Detail).Concat(implemented ? [] : new[] { ExecutionGaps.Describe(check) }).ToArray();
            return new TestOption(check.Id, check.Group, check.Name, check.Expected, implemented && reasons.Length == 0,
                reasons, check.Api, check.Expected,
                check.Requirements.Select(TestConditions.Name).Order(StringComparer.CurrentCulture).ToArray(),
                check.Recovery, implemented, check.Operation,
                issues.Where(issue => !issue.CheckPending).Select(issue => issue.Detail).ToArray(),
                issues.Where(issue => issue.CheckPending).Select(issue => issue.Detail).ToArray());
        }).ToArray();
        return new(report.RunId, reason is null, reason, "按第一步检查结果筛选", items);
    }

    private static CatalogTest[] LoadCatalog()
    {
        using var stream = typeof(TestPlanner).Assembly.GetManifestResourceStream("ExHyperV.Core.Tests.TestPlanning.TestCatalog.json")
            ?? throw new InvalidDataException("测试清单未打包。");
        var catalog = JsonSerializer.Deserialize<CatalogTest[]>(stream, InspectionJson.Options)
            ?? throw new InvalidDataException("测试清单为空。");
        if (catalog.Length == 0 || catalog.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Length)
            throw new InvalidDataException("测试清单为空或编号重复。");
        foreach (var entry in catalog)
        {
            if (string.IsNullOrWhiteSpace(entry.Group) || string.IsNullOrWhiteSpace(entry.Operation)
                || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Expected))
                throw new InvalidDataException($"测试项说明缺失：{entry.Id}");
            foreach (var requirement in entry.Requirements) _ = TestConditions.Name(requirement);
        }
        return catalog;
    }
}
