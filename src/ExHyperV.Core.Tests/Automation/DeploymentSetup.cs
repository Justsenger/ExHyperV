using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.Automation;

internal static class DeploymentSetup
{
    private static readonly string[] Required = ["platform", "identity", "dotnet", "msbuild", "powershell", "workspace"];

    internal static string[] GetIssues(InspectionReport report)
    {
        if (!report.Plan.Available) return [report.Plan.Reason ?? "资源检查尚未完成"];
        return Required.Select(id => report.Items.Where(i => i.Check.Id == id).ToArray())
            .Where(items => items.Length != 1 || items[0].Result.Status != InspectionStatus.Ready)
            .Select(items => items.Length != 1 ? "基础环境检查缺失或重复" : items[0].Check.Name + "：" + items[0].Result.Detail).ToArray();
    }

    internal static int AvailableCount(InspectionReport report)
    {
        var reboot = TestPlanner.Catalog.Where(RebootApiCases.Supports).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return report.Plan.Items.Count(i => i.Eligible && !reboot.Contains(i.Id));
    }
}
