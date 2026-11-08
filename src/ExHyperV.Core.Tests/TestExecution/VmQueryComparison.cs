using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal enum ApiTestStatus { Waiting, Running, Passed, Failed, Skipped, Cancelled, PendingImplementation }
internal sealed record VmListRow(Guid Id, string Name, ushort State);
internal sealed record VmQueryCapture(VmListRow[] Before, VmListRow[] Actual, VmListRow[] After, double ApiMilliseconds, VmQueryIssue[]? Issues = null);
internal sealed record CheckVerdict(ApiTestStatus Status, string Detail, string[] Evidence);

internal static class VmQueryComparison
{
    internal static CheckVerdict Evaluate(string id, VmQueryCapture capture)
    {
        var verdict = EvaluateRows(id, capture);
        return capture.Issues is { Length: > 0 } issues
            ? verdict with { Evidence = verdict.Evidence.Concat(issues.Select(issue =>
                $"可选信息读取问题 · {issue.Component} · {issue.ErrorKind} / {issue.ErrorSource}:{issue.Code} · {issue.Error}")).ToArray() }
            : verdict;
    }

    private static CheckVerdict EvaluateRows(string id, VmQueryCapture capture)
    {
        var before = capture.Before;
        var actual = capture.Actual;
        var after = capture.After;
        if (before.Length == 0 || after.Length == 0)
            return new(ApiTestStatus.Skipped, "本轮系统基准为空，当前检查需要至少一台虚拟机。", []);
        if (before.Select(v => v.Id).Distinct().Count() != before.Length || after.Select(v => v.Id).Distinct().Count() != after.Length)
            return new(ApiTestStatus.Skipped, "系统基准出现重复 ID，请重新采样。", []);

        var duplicates = actual.GroupBy(v => v.Id).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} · 出现 {g.Count()} 次").ToArray();
        if (id == "vm-list.unique")
            return actual.Length == 0 ? new(ApiTestStatus.Skipped, "API 返回 0 条，唯一性检查缺少样本。", [])
                : duplicates.Length > 0 ? new(ApiTestStatus.Failed, $"发现 {duplicates.Length} 个重复 ID。", duplicates)
                : new(ApiTestStatus.Passed, $"已检查 {actual.Length} 个 ID，每个 ID 出现一次。", actual.Select(v => v.Id.ToString()).ToArray());

        var expected = before.ToDictionary(v => v.Id);
        var later = after.ToDictionary(v => v.Id);
        if (!expected.Keys.ToHashSet().SetEquals(later.Keys))
            return new(ApiTestStatus.Skipped, "两次系统采样的虚拟机清单发生变化，请重新执行。", []);
        var missing = expected.Keys.Except(actual.Select(v => v.Id)).Select(key => $"缺少：{expected[key].Name} · {key}");
        var extra = actual.Select(v => v.Id).Except(expected.Keys).Select(key => $"额外：{key}");
        var membershipErrors = missing.Concat(extra).ToArray();
        if (id == "vm-list.ids")
            return membershipErrors.Length > 0 ? new(ApiTestStatus.Failed, "API 与系统基准的 ID 集合存在差异。", membershipErrors)
                : new(ApiTestStatus.Passed, $"API 与系统基准均包含 {expected.Count} 台虚拟机，ID 集合一致。", before.Select(v => $"{v.Name} · {v.Id}").ToArray());

        if (id is not ("vm-list.names" or "vm-list.states")) throw new ArgumentException("测试项标识无效。", nameof(id));
        var names = id == "vm-list.names";
        if (before.Any(v => names ? v.Name != later[v.Id].Name : v.State != later[v.Id].State))
            return new(ApiTestStatus.Skipped, names ? "两次系统采样之间发生了改名，请重新执行。" : "两次系统采样之间状态发生变化，请重新执行。", []);
        if (membershipErrors.Length > 0 || duplicates.Length > 0)
            return new(ApiTestStatus.Failed, "按 ID 配对时发现缺失、额外或重复对象。", membershipErrors.Concat(duplicates).ToArray());
        var differences = actual.Where(v => names ? v.Name != expected[v.Id].Name : v.State != expected[v.Id].State)
            .Select(v => names ? $"{v.Id} · 系统：{expected[v.Id].Name} · API：{v.Name}"
                : $"{v.Name} · {v.Id} · 系统状态码：{expected[v.Id].State} · API 状态码：{v.State}").ToArray();
        return differences.Length > 0 ? new(ApiTestStatus.Failed, $"发现 {differences.Length} 项{(names ? "名称" : "状态码")}差异。", differences)
            : new(ApiTestStatus.Passed, $"已核对 {actual.Length} 台虚拟机，{(names ? "名称逐字一致" : "原始状态码一致")}。",
                actual.Select(v => names ? $"{v.Id} · {v.Name}" : $"{v.Id} · {v.Name} · 状态码 {v.State}").ToArray());
    }
}
