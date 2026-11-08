using System.Management;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmGroupApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmGroupService.ReadAsync()" or "VmGroupService.CreateAsync()" or "VmGroupService.MoveAsync()" or "VmGroupService.RenameAsync()" or "VmGroupService.DeleteAsync()";

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var parent = await TemporaryGroup.CreateAsync(c, VmGroupKind.Groups, targetCall: p.Api == "VmGroupService.CreateAsync()");
        await using var child = await TemporaryGroup.CreateAsync(c, VmGroupKind.VirtualMachines, parent.Id, p.Api == "VmGroupService.CreateAsync()");
        if (p.Api == "VmGroupService.CreateAsync()")
        {
            c.Equal(parent.Name, Native(parent.Id)["ElementName"]?.ToString(), "管理组名称");
            c.Equal(child.Name, Native(child.Id)["ElementName"]?.ToString(), "虚拟机组名称");
            c.Equal("Msvm_ManagementCollection", NativeClass(parent.Id), "管理组类型");
            c.Equal("Msvm_VirtualSystemCollection", NativeClass(child.Id), "虚拟机组类型");
            c.Sequence(new[] { child.Id }, Members(parent.Id, false), "新建子分组归属");
            c.Equal(0, Members(child.Id, true).Length, "新组虚拟机成员为空"); return;
        }
        await using var vm = await TemporaryVm.CreateAsync(c);
        await VmGroupService.MoveAsync([new(vm.Id, false, null)], child.Id);
        if (p.Api == "VmGroupService.ReadAsync()")
        {
            var before = NativeIds(); var snapshot = await c.CallAsync(VmGroupService.ReadAsync); var after = NativeIds();
            if (!before.SequenceEqual(after)) throw new ApiCheckSkippedException("采样期间系统分组清单发生变化，请重试本项。");
            c.Sequence(before, snapshot.Groups.Select(g => g.Id).Order(), "所有分组身份集合");
            foreach (var group in snapshot.Groups)
            {
                var native = Native(group.Id);
                c.Equal(native["ElementName"]?.ToString(), group.Name, "分组名称 " + group.Id);
                c.Equal(NativeClass(group.Id) == "Msvm_ManagementCollection" ? VmGroupKind.Groups : VmGroupKind.VirtualMachines, group.Kind, "分组类别");
                c.Sequence(Members(group.Id, true), group.VmIds.Order(), "虚拟机成员 " + group.Id);
                c.Sequence(Members(group.Id, false), group.ChildIds.Order(), "子分组成员 " + group.Id);
            }
        }
        else if (p.Api == "VmGroupService.DeleteAsync()")
        {
            await c.CallAsync(async () => { await VmGroupService.DeleteAsync(child.Id); return true; });
            c.Require(!NativeIds().Contains(child.Id), "系统分组已删除");
            c.Equal(vm.Id, Guid.Parse(Convert.ToString(SystemBaseline.Vm(vm.Id)["Name"])!), "成员虚拟机保留");
            c.Equal(0, Members(parent.Id, false).Length, "父组关联已清理");
        }
        else if (p.Api == "VmGroupService.RenameAsync()")
        {
            if (p.Id.EndsWith("-3", StringComparison.Ordinal))
            {
                await child.DisposeAsync(); await MustFailAsync(c, () => VmGroupService.RenameAsync(child.Id, "已删除"), "消失目标拒绝重命名");
                c.Equal(parent.Name, Native(parent.Id)["ElementName"]?.ToString(), "父组保持名称"); return;
            }
            string name = "中文 名称 AbC " + child.Id.ToString("N");
            for (int i = 0; i < (p.Id.EndsWith("-2", StringComparison.Ordinal) ? 2 : 1); i++)
                await c.CallAsync(async () => { await VmGroupService.RenameAsync(child.Id, name); return true; });
            c.Equal(name, Native(child.Id)["ElementName"]?.ToString(), "逐字核对新组名");
            c.Sequence(new[] { vm.Id }, Members(child.Id, true), "重命名后虚拟机成员保留");
            c.Sequence(new[] { child.Id }, Members(parent.Id, false), "重命名后父组关系保留");
        }
        else
        {
            await using var other = await TemporaryGroup.CreateAsync(c, VmGroupKind.VirtualMachines);
            await using var container = await TemporaryGroup.CreateAsync(c, VmGroupKind.Groups);
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                await MustFailAsync(c, () => VmGroupService.MoveAsync([new(parent.Id, true, null)], parent.Id), "拒绝自环");
                await VmGroupService.MoveAsync([new(container.Id, true, null)], parent.Id);
                var withParent = Edges();
                await MustFailAsync(c, () => VmGroupService.MoveAsync([new(parent.Id, true, null)], container.Id), "拒绝祖先循环");
                c.Sequence(withParent, Edges(), "循环失败保留关系");
                await other.DisposeAsync();
                await MustFailAsync(c, () => VmGroupService.MoveAsync([new(vm.Id, false, child.Id)], other.Id), "拒绝消失目标");
                c.Sequence(new[] { vm.Id }, Members(child.Id, true), "消失目标保留原成员");
            }
            else
            {
                var move = await c.CallAsync(() => VmGroupService.MoveAsync([new(vm.Id, false, child.Id)], other.Id));
                c.Equal<Guid?>(other.Id, move.TargetId, "实际接收组身份");
                c.Sequence(new[] { vm.Id }, Members(other.Id, true), "目标组包含虚拟机");
                c.Equal(0, Members(child.Id, true).Length, "原组移除虚拟机");
                await c.CallAsync(() => VmGroupService.MoveAsync([new(child.Id, true, parent.Id)], container.Id));
                c.Sequence(new[] { child.Id }, Members(container.Id, false), "目标管理组包含子组");
                c.Equal(0, Members(parent.Id, false).Length, "原管理组移除子组");
                c.Sequence(new[] { vm.Id }, Members(other.Id, true), "其他组成员保持");
            }
        }
    }

    private static async Task MustFailAsync(ApiCaseContext c, Func<Task> action, string label)
    {
        Exception? error = null; try { await c.CallAsync(async () => { await action(); return true; }); } catch (InvalidOperationException ex) { error = ex; } catch (ArgumentException ex) { error = ex; }
        c.Require(error is not null, label + "：" + error?.Message);
    }
    private static Guid[] NativeIds() => new[] { "Msvm_VirtualSystemCollection", "Msvm_ManagementCollection" }.SelectMany(type => SystemBaseline.Query("SELECT * FROM " + type)).Select(r => Guid.Parse(r["CollectionID"]!.ToString()!)).Order().ToArray();
    private static string NativeClass(Guid id) => SystemBaseline.Query($"SELECT * FROM Msvm_ManagementCollection WHERE CollectionID='{id:D}'").Count == 1 ? "Msvm_ManagementCollection" : "Msvm_VirtualSystemCollection";
    private static Dictionary<string, object?> Native(Guid id) => SystemBaseline.Query($"SELECT * FROM {NativeClass(id)} WHERE CollectionID='{id:D}'").Single();
    private static Guid[] Members(Guid id, bool vms)
    {
        var values = new List<Guid>();
        foreach (var row in SystemBaseline.Query("SELECT * FROM " + (vms ? "Msvm_CollectedVirtualSystems" : "Msvm_CollectedCollections")))
        {
            using var parent = new ManagementObject(row["Collection"]!.ToString()!); parent.Get();
            if (Guid.Parse(parent["CollectionID"].ToString()!) != id) continue;
            using var member = new ManagementObject(row["Member"]!.ToString()!); member.Get();
            values.Add(Guid.Parse(member[vms ? "Name" : "CollectionID"].ToString()!));
        }
        return values.Order().ToArray();
    }
    private static string[] Edges() => NativeIds().SelectMany(id => Members(id, true).Select(member => id + ":VM:" + member).Concat(Members(id, false).Select(member => id + ":GROUP:" + member))).Order(StringComparer.Ordinal).ToArray();
}
