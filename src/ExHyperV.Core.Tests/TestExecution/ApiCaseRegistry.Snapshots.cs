using System.Reflection;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddSnapshotCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmMemorySettings.构造()", [c =>
        {
            var model = c.Call(() => new VmMemorySettings());
            c.Equal(false, model.SupportsBackingPageSize, "读取前未声明宿主页大小能力");
            foreach (var property in typeof(VmMemorySettings).GetProperties().Where(p => Nullable.GetUnderlyingType(p.PropertyType) is not null))
                c.Equal<object?>(null, property.GetValue(model), property.Name + " 初始缺测值");
        }]);
        add("VmMemorySettings.Clone()", [c =>
        {
            var source = CreateMemorySample();
            var clone = c.Call(source.Clone);
            CompareSnapshot(c, source, clone);
            c.Equal(2048L, source.Startup, "源启动内存独立");
        }]);
        add("VmMemorySettings.Restore()", [c =>
        {
            var backup = CreateMemorySample();
            var target = new VmMemorySettings { Startup = 8192, Minimum = 256, Weight = 100 };
            c.Call(() => target.Restore(backup));
            CompareSnapshot(c, backup, target);
        }]);
        add("VmMmioSettings.Clone()", [c =>
        {
            var source = new VmMmioSettings { LowSizeMiB = 512, HighSizeMiB = 32768, HighBaseMiB = 65536 };
            var clone = c.Call(source.Clone); CompareSnapshot(c, source, clone);
            clone.LowSizeMiB = 1024;
            c.Equal<ulong?>(512, source.LowSizeMiB, "源 MMIO 值独立");
            source.HighBaseMiB = null;
            CompareSnapshot(c, source, c.Call(source.Clone));
        }]);
        add("VmMmioSettings.Restore()", [c =>
        {
            var backup = new VmMmioSettings { LowSizeMiB = 512, HighSizeMiB = null, HighBaseMiB = 65536 };
            var target = new VmMmioSettings { LowSizeMiB = 1024, HighSizeMiB = 32768, HighBaseMiB = 131072 };
            c.Call(() => target.Restore(backup)); CompareSnapshot(c, backup, target);
            c.Call(() => target.Restore(null!)); CompareSnapshot(c, backup, target);
        }]);
        add("VmMemoryService 动态内存能力规则", [c =>
        {
            foreach (var row in new[] {
                (false, false, new VmMemoryService.Capabilities(true, true, false)),
                (false, true, new VmMemoryService.Capabilities(true, true, true)),
                (true, false, new VmMemoryService.Capabilities(false, true, false)),
                (true, true, new VmMemoryService.Capabilities(false, false, true)) })
                c.Equal(row.Item3, c.Call(() => VmMemoryService.GetCapabilities(row.Item1, row.Item2)), $"运行={row.Item1}，动态内存={row.Item2}");
        }]);
        add("VmMemoryService 配置校验规则", [c =>
        {
            foreach (bool dynamic in new[] { false, true })
            {
                var current = CreateMemorySample(); current.DynamicMemoryEnabled = dynamic;
                foreach (var (name, value, allowed) in new (string, object, bool)[] {
                    (nameof(VmMemorySettings.Weight), 6000u, true),
                    (nameof(VmMemorySettings.Startup), 4096L, !dynamic),
                    (nameof(VmMemorySettings.Minimum), 768L, dynamic),
                    (nameof(VmMemorySettings.Maximum), 16384L, dynamic),
                    (nameof(VmMemorySettings.Buffer), 30, dynamic),
                    (nameof(VmMemorySettings.DynamicMemoryEnabled), !dynamic, false) })
                {
                    var requested = current.Clone(); typeof(VmMemorySettings).GetProperty(name)!.SetValue(requested, value);
                    c.Equal(allowed, c.Call(() => VmMemoryService.GetChangeError(current, requested, true, name)) is null, $"运行时 {name}，动态内存={dynamic}");
                    c.Equal<string?>(null, c.Call(() => VmMemoryService.GetChangeError(current, requested, false, name)), "关机时 " + name);
                }
            }
        }]);
        add("VmGroupSnapshot.GetVmIds()", [c =>
        {
            var (snapshot, parent, child, leaf, vm) = CreateGroupSample();
            c.Sequence(new[] { vm }, c.Call(() => snapshot.GetVmIds(parent.Id)), "多层分组成员去重");
            c.Sequence(new[] { vm }, c.Call(() => snapshot.GetVmIds(leaf.Id)), "直接成员");
            c.Equal(0, c.Call(() => snapshot.GetVmIds(Guid.Empty)).Count, "缺席分组返回空集合");
            child.ChildIds.Add(parent.Id);
            c.Sequence(new[] { vm }, c.Call(() => snapshot.GetVmIds(parent.Id)), "循环关系仍能结束遍历");
        }]);
        add("VmGroupSnapshot.CanContain()", [c =>
        {
            var (snapshot, parent, child, leaf, _) = CreateGroupSample();
            c.Equal(true, c.Call(() => snapshot.CanContain(parent.Id, child.Id)), "正常父子");
            c.Equal(false, c.Call(() => snapshot.CanContain(parent.Id, parent.Id)), "拒绝自身包含");
            c.Equal(false, c.Call(() => snapshot.CanContain(child.Id, parent.Id)), "拒绝祖先循环");
            c.Equal(false, c.Call(() => snapshot.CanContain(leaf.Id, child.Id)), "虚拟机组拒绝子组");
            c.Equal(false, c.Call(() => snapshot.CanContain(parent.Id, Guid.Empty)), "缺席子组");
        }]);
        add("VmGroupMoveRules.CanMove()", [c =>
        {
            var (snapshot, parent, child, leaf, vm) = CreateGroupSample();
            c.Equal(true, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [new(vm, false, null)], leaf.Id)), "虚拟机入组");
            c.Equal(false, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [new(vm, false, leaf.Id)], leaf.Id)), "已在目标组");
            c.Equal(true, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [new(vm, false, leaf.Id)], null)), "虚拟机移出组");
            c.Equal(false, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [new(parent.Id, true, null)], child.Id)), "拒绝祖先移入后代");
            c.Equal(false, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [new(parent.Id, true, null)], parent.Id)), "拒绝自环");
            c.Equal(false, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [], leaf.Id)), "空移动清单");
            c.Equal(false, c.Call(() => VmGroupMoveRules.CanMove(snapshot, [new(vm, false, null), new(child.Id, true, parent.Id)], parent.Id)), "混合成员类型");
        }]);
    }

    private static VmMemorySettings CreateMemorySample() => new()
    {
        Startup = 2048, Minimum = 512, Maximum = 8192, DynamicMemoryEnabled = true, Buffer = 20, Weight = 5000,
        BackingPageSize = 1, BackingType = 0, SgxEnabled = false, SgxSize = 0, EnableGpaPinning = true,
        CxlEnabled = false, MaxMemoryBlocksPerNumaNode = 16, SgxLaunchControlDefault = "测试值",
        DynMemOperationAlignment = 4, MemoryAccessTrackingPolicy = 1, MemoryAccessTrackingState = 2,
        SgxLaunchControlMode = 1, EnableColdHint = true, EnableHotHint = false, EnableEpf = true,
        EnablePrivateCompressionStore = false, MemoryEncryptionPolicy = 1
    };

    private static void CompareSnapshot<T>(ApiCaseContext c, T expected, T actual) where T : class
    {
        c.Require(!ReferenceEquals(expected, actual), "快照对象各自独立");
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            c.Equal(p.GetValue(expected), p.GetValue(actual), p.Name);
        }
    }

    private static (VmGroupSnapshot, VmGroup, VmGroup, VmGroup, Guid) CreateGroupSample()
    {
        var parent = new VmGroup(Guid.NewGuid(), "父组", VmGroupKind.Groups);
        var child = new VmGroup(Guid.NewGuid(), "子组", VmGroupKind.Groups);
        var leaf = new VmGroup(Guid.NewGuid(), "虚拟机组", VmGroupKind.VirtualMachines);
        var vm = Guid.NewGuid(); leaf.VmIds.Add(vm); child.ChildIds.Add(leaf.Id); parent.ChildIds.UnionWith([child.Id, leaf.Id]);
        return (new VmGroupSnapshot([parent, child, leaf]), parent, child, leaf, vm);
    }
}
