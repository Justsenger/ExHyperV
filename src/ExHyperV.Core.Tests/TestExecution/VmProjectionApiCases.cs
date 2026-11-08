using System.Management;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmProjectionApiCases
{
    internal static bool Supports(string id) => id is "api.query-targets" or "api.query-list" or "api.query-dispose";

    internal static async Task RunAsync(string id, ApiCaseContext c)
    {
        await using var service = new VmQueryService();
        if (id == "api.query-dispose")
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await c.CallAsync(() => service.QueryAsync(new([VmIdentityFields.Name]), cancelled.Token));
                c.Require(false, "提交前取消应抛出 OperationCanceledException");
            }
            catch (OperationCanceledException) { c.Evidence.Add("提交前取消抛出 OperationCanceledException。"); }
            var inFlight = c.CallAsync(() => service.QueryAsync(new([VmIdentityFields.Name])));
            await service.DisposeAsync();
            c.Require(inFlight.IsCompleted, "释放完成时已接纳的查询已结束");
            var result = await inFlight;
            c.Require(result.Success, "已接纳的查询完成：" + result.Error);
            try
            {
                await c.CallAsync(() => service.QueryAsync(new([VmIdentityFields.Name])));
                c.Require(false, "释放后调用应抛出 ObjectDisposedException");
            }
            catch (ObjectDisposedException) { c.Evidence.Add("释放后新查询明确拒绝。"); }
            return;
        }

        var before = ReadBaseline();
        if (id == "api.query-targets")
        {
            var empty = await c.CallAsync(() => service.QueryAsync(new([VmIdentityFields.Name], [])));
            c.Require(empty.HasData, "空目标查询成功：" + empty.Error);
            c.Equal(0, empty.Data!.VirtualMachines.Count, "显式空目标返回零台");
            c.Equal(0, empty.Data.MissingVmIds.Count, "空目标无缺席身份");
            var selected = before.Keys.Order().Take(2).ToArray();
            Guid missing; do { missing = Guid.NewGuid(); } while (before.ContainsKey(missing));
            var result = await c.CallAsync(() => service.QueryAsync(new([VmIdentityFields.Name], selected.Concat([missing]))));
            EnsureStable(before, ReadBaseline());
            c.Require(result.HasData, "指定目标查询成功：" + result.Error);
            c.Sequence(selected.Order(), result.Data!.VirtualMachines.Select(row => Guid.Parse(row.Key)).Order(), "仅返回指定的现有目标");
            c.Sequence(new[] { missing }, result.Data.MissingVmIds, "缺席 GUID 单独返回");
            return;
        }

        var response = await c.CallAsync(() => service.QueryAsync(new([VmIdentityFields.Name, VmIdentityFields.State])));
        EnsureStable(before, ReadBaseline());
        c.Require(response.HasData, "字段查询成功：" + response.Error);
        var data = response.Data!;
        c.Sequence(before.Keys.Order(), data.VirtualMachines.Select(row => Guid.Parse(row.Key)).Order(), "完整身份集合");
        c.Require(data.CompletedAtUtc >= data.StartedAtUtc, "采样起止时间顺序正确");
        foreach (var row in data.VirtualMachines)
        {
            var baseline = before[Guid.Parse(row.Key)];
            c.Sequence(new[] { VmIdentityFields.Name.Id, VmIdentityFields.State.Id }.Order(), row.Values.Keys.Order(), "仅返回所选字段 " + row.Key);
            c.Require(row.Get(VmIdentityFields.Name).HasValue && row.Get(VmIdentityFields.State).HasValue, "名称与状态均为有效采样 " + row.Key);
            c.Equal(baseline.Name, row.Get(VmIdentityFields.Name).Value, "名称 " + row.Key);
            c.Equal(MapBaselineState(baseline.State), row.Get(VmIdentityFields.State).Value, "状态 " + row.Key);
            c.Equal(0, row.Collections.Count, "未请求设备集合 " + row.Key);
        }
    }

    private sealed record Baseline(string Name, ushort State);
    private static Dictionary<Guid, Baseline> ReadBaseline()
    {
        // Caption is localized. The host uses a machine name; VM identities are GUIDs.
        using var query = new ManagementObjectSearcher("root/virtualization/v2", "SELECT Name,ElementName,EnabledState FROM Msvm_ComputerSystem");
        using var rows = query.Get(); var result = new Dictionary<Guid, Baseline>();
        foreach (ManagementObject row in rows) using (row)
            if (Guid.TryParse(row["Name"]?.ToString(), out var id))
                result.Add(id, new((string)row["ElementName"], Convert.ToUInt16(row["EnabledState"])));
        return result;
    }
    private static void EnsureStable(Dictionary<Guid, Baseline> before, Dictionary<Guid, Baseline> after)
    {
        if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var item) || item != pair.Value))
            throw new ApiCheckSkippedException("虚拟机清单或状态在采样期间变化，留待复测。");
    }
    private static VmState MapBaselineState(ushort value) => value switch
    {
        2 => VmState.Running, 3 => VmState.PoweredOff, 6 or 32769 => VmState.Saved,
        9 or 32768 => VmState.Suspended, 32783 => VmState.Hibernated,
        _ => throw new ApiCheckSkippedException($"系统当前状态码 {value} 的独立基准待补齐。")
    };
}
