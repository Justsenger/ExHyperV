using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddAdditionalRuleCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmBootService.CanChangeBootOrder()", [c =>
        {
            foreach (var (generation, running, expected) in new[] { (1, false, true), (1, true, false), (2, false, true), (2, true, true) })
                c.Equal(expected, c.Call(() => VmBootService.CanChangeBootOrder(generation, running)), $"代数 {generation}，运行={running}");
        }]);
        add("VmConsoleSupportService.CanToggleConsoleSupport()", [c =>
        {
            foreach (var (generation, running, expected) in new[] { (1, false, false), (1, true, false), (2, false, true), (2, true, false) })
                c.Equal(expected, c.Call(() => VmConsoleSupportService.CanToggleConsoleSupport(generation, running)), $"代数 {generation}，运行={running}");
        }]);
        add("VmBootService.CanEditBootNumLock()", [c => CheckOffline(c, VmBootService.CanEditBootNumLock)]);
        add("VmDisplaySettingsService.CanEdit()", [c => CheckOffline(c, VmDisplaySettingsService.CanEdit)]);
        add("VmMmioService.CanEdit()", [c => CheckOffline(c, VmMmioService.CanEdit)]);
        add("VmPowerService.CanEditHibernation()", [c =>
        {
            foreach (var row in new[] { (false, false, false), (false, true, false), (true, false, true), (true, true, false) })
                c.Equal(row.Item3, c.Call(() => VmPowerService.CanEditHibernation(row.Item1, row.Item2)), $"字段存在={row.Item1}，运行={row.Item2}");
        }]);
        add("VmDisplaySettingsService.GetResolutionError()", [c =>
        {
            foreach (var row in new[] { (199, 200, false), (200, 200, true), (7680, 4320, true), (7681, 4320, false), (200, 199, false), (200, 4321, false) })
                c.Equal(row.Item3, c.Call(() => VmDisplaySettingsService.GetResolutionError(3, row.Item1, row.Item2)) is null, $"固定尺寸 {row.Item1}×{row.Item2}");
        }, c =>
        {
            foreach (int mode in new[] { 2, 4 }) foreach (int size in new[] { -1, 0, int.MaxValue })
                c.Equal<string?>(null, c.Call(() => VmDisplaySettingsService.GetResolutionError(mode, size, size)), $"模式 {mode} 忽略尺寸 {size}");
            foreach (int mode in new[] { -1, 0, 1, 5 })
                c.Require(c.Call(() => VmDisplaySettingsService.GetResolutionError(mode, 1024, 768)) is not null, $"未知策略 {mode} 返回错误");
        }]);
        add("VmSpacetimeService.HasOpenWormhole()", [c =>
        {
            c.Equal(false, c.Call(() => VmSpacetimeService.HasOpenWormhole(null)), "null 集合");
            c.Equal(false, c.Call(() => VmSpacetimeService.HasOpenWormhole([])), "空集合");
            c.Equal(false, c.Call(() => VmSpacetimeService.HasOpenWormhole([new()])), "普通节点");
            c.Equal(true, c.Call(() => VmSpacetimeService.HasOpenWormhole([new(), new() { IsWormhole = true }])), "存在历史挂接");
        }]);
        add("VmSpacetimeService.CanOperateHistoricalNode()", [c =>
        {
            c.Equal(false, c.Call(() => VmSpacetimeService.CanOperateHistoricalNode(null)), "空节点");
            foreach (var type in Enum.GetValues<SpacetimeNodeType>())
                c.Equal(type == SpacetimeNodeType.Snapshot, c.Call(() => VmSpacetimeService.CanOperateHistoricalNode(new() { NodeType = type })), type.ToString());
        }]);
        add("VmSpacetimeService.CanCreateMoment()", [c =>
        {
            foreach (var row in new[] { (false, false, false), (false, true, false), (true, false, true), (true, true, false) })
                c.Equal(row.Item3, c.Call(() => VmSpacetimeService.CanCreateMoment(row.Item1, [new() { IsWormhole = row.Item2 }])), $"检查点开启={row.Item1}，历史挂接={row.Item2}");
        }]);
        add("VmSpacetimeService.CanMutateHistoricalNode()", [c =>
        {
            foreach (var type in Enum.GetValues<SpacetimeNodeType>()) foreach (bool open in new[] { false, true })
                c.Equal(type == SpacetimeNodeType.Snapshot && !open, c.Call(() => VmSpacetimeService.CanMutateHistoricalNode(new() { NodeType = type }, [new() { IsWormhole = open }])), $"节点 {type}，历史挂接={open}");
            c.Equal(false, c.Call(() => VmSpacetimeService.CanMutateHistoricalNode(null, [])), "空节点");
        }]);
        var vmId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        add("PcieAssignmentTarget.ForVirtualMachine()", [c =>
        {
            var target = c.Call(() => PcieAssignmentTarget.ForVirtualMachine(vmId, "中文 虚拟机"));
            c.Equal<Guid?>(vmId, target.VirtualMachineId, "目标 GUID");
            c.Equal(PcieAssignmentKind.VirtualMachine, target.Kind, "归属类型");
            c.Equal("中文 虚拟机", target.VirtualMachineName, "显示名称");
            c.Equal(vmId.ToString("D"), c.Call(() => PcieAssignmentTarget.ForVirtualMachine(vmId)).VirtualMachineName, "默认显示名称");
            c.Throws<ArgumentException>(() => c.Call(() => PcieAssignmentTarget.ForVirtualMachine(Guid.Empty)), "拒绝空 GUID");
        }]);
        add("PcieAssignmentTarget.Equals()", [c =>
        {
            var original = PcieAssignmentTarget.ForVirtualMachine(vmId, "原名");
            c.Equal(true, c.Call(() => original.Equals(PcieAssignmentTarget.ForVirtualMachine(vmId, "新名"))), "改名保留身份");
            foreach (var target in new PcieAssignmentTarget?[] { null, PcieAssignmentTarget.Host, PcieAssignmentTarget.Unassigned, PcieAssignmentTarget.ForVirtualMachine(Guid.NewGuid()) })
                c.Equal(false, c.Call(() => original.Equals(target)), "相异目标");
            c.Equal(false, c.Call(() => PcieAssignmentTarget.Host.Equals(PcieAssignmentTarget.Unassigned)), "宿主与未分配分别表示");
        }]);
        add("PcieAssignmentTarget.GetHashCode()", [c =>
        {
            var a = PcieAssignmentTarget.ForVirtualMachine(vmId, "原名");
            var b = PcieAssignmentTarget.ForVirtualMachine(vmId, "改名");
            c.Equal(c.Call(a.GetHashCode), c.Call(b.GetHashCode), "身份相同的哈希相等");
            c.Require(new HashSet<PcieAssignmentTarget> { a }.Contains(b), "改名目标仍可在集合查到");
        }]);
    }

    private static void CheckOffline(ApiCaseContext c, Func<bool, bool> action)
    {
        c.Equal(true, c.Call(() => action(false)), "静止状态允许修改");
        c.Equal(false, c.Call(() => action(true)), "活动状态锁定修改");
    }

    private static void AddSwitchCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        const string defaultId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";
        var wired = new SwitchUpstreamAdapter(Guid.Parse("10000000-0000-0000-0000-000000000001"), "wired", "Ethernet", "LAN", "external-wired", true);
        var mobile = wired with { ConnectionId = Guid.Parse("10000000-0000-0000-0000-000000000002"), DeviceId = "mobile" };
        var spare = wired with { ConnectionId = Guid.Parse("10000000-0000-0000-0000-000000000003"), DeviceId = "spare" };
        SwitchUpstreamAdapter[] physical = [wired, mobile, spare], bridgeable = [wired, spare];
        var owned = new SwitchInfo { Id = "20000000-0000-0000-0000-000000000001", SwitchName = "Existing", SwitchType = SwitchMode.NAT, Upstream = wired };
        add("SwitchConfigurationRules.IsDefaultSwitch()", [c =>
        {
            foreach (var id in new[] { defaultId, defaultId.ToUpperInvariant(), "{" + defaultId.ToUpperInvariant() + "}" })
                c.Equal(true, c.Call(() => SwitchConfigurationRules.IsDefaultSwitch(id)), "默认交换机身份 " + id);
            c.Equal(false, c.Call(() => SwitchConfigurationRules.IsDefaultSwitch(owned.Id)), "普通交换机");
        }]);
        add("SwitchConfigurationRules.GetDeletionError()", [c =>
        {
            c.Require(c.Call(() => SwitchConfigurationRules.GetDeletionError(defaultId)) is not null, "默认交换机受保护");
            c.Equal<string?>(null, c.Call(() => SwitchConfigurationRules.GetDeletionError(owned.Id)), "普通交换机可删除");
        }]);
        add("SwitchConfigurationRules.RequiresUpstream()", [c =>
        {
            foreach (var row in new[] { (SwitchMode.Isolated, false), (SwitchMode.Bridge, true), (SwitchMode.NAT, true) })
                c.Equal(row.Item2, c.Call(() => SwitchConfigurationRules.RequiresUpstream(row.Item1)), row.Item1.ToString());
        }]);
        add("SwitchConfigurationRules.IsConfigurationComplete()", [c =>
        {
            foreach (var mode in Enum.GetValues<SwitchMode>())
            {
                c.Equal(mode == SwitchMode.Isolated, c.Call(() => SwitchConfigurationRules.IsConfigurationComplete(mode, null)), mode + " 上游缺席");
                c.Equal(true, c.Call(() => SwitchConfigurationRules.IsConfigurationComplete(mode, wired)), mode + " 已选上游");
            }
        }]);
        add("SwitchConfigurationRules.GetAvailableCreationAdapters()", [c =>
        {
            c.Sequence(new[] { spare }, c.Call(() => SwitchConfigurationRules.GetAvailableCreationAdapters(SwitchMode.Bridge, [owned], physical, bridgeable)), "桥接筛选空闲可桥接网卡");
            c.Sequence(new[] { mobile, spare }, c.Call(() => SwitchConfigurationRules.GetAvailableCreationAdapters(SwitchMode.NAT, [owned], physical, bridgeable)), "NAT 保留空闲物理网卡");
            c.Sequence(bridgeable, c.Call(() => SwitchConfigurationRules.GetAvailableCreationAdapters(SwitchMode.Bridge, [], physical, bridgeable)), "空占用保留原顺序");
        }]);
        add("SwitchConfigurationRules.GetCreationError()", [c =>
        {
            foreach (var mode in Enum.GetValues<SwitchMode>())
            {
                c.Equal<string?>(null, c.Call(() => SwitchConfigurationRules.GetCreationError("新交换机", mode, spare, [], physical, bridgeable)), mode + " 有效输入");
                c.Equal(mode == SwitchMode.Isolated, c.Call(() => SwitchConfigurationRules.GetCreationError("新交换机", mode, null, [], physical, bridgeable)) is null, mode + " 缺少上游");
            }
            foreach (string name in new[] { " ", "EXISTING" })
                c.Require(c.Call(() => SwitchConfigurationRules.GetCreationError(name, SwitchMode.Isolated, null, [owned], physical, bridgeable)) is not null, "名称冲突或空白：" + name);
            c.Require(c.Call(() => SwitchConfigurationRules.GetCreationError("new", SwitchMode.NAT, spare, [owned], physical, bridgeable)) is not null, "限制第二个自定义 NAT");
            c.Require(c.Call(() => SwitchConfigurationRules.GetCreationError("new", SwitchMode.Bridge, wired, [owned], physical, bridgeable)) is not null, "已占用上游返回错误");
            c.Equal<string?>(null, c.Call(() => SwitchConfigurationRules.GetCreationError("new", SwitchMode.NAT, spare, [new() { Id = defaultId, SwitchName = "默认", SwitchType = SwitchMode.NAT }], physical, bridgeable)), "系统默认 NAT 独立于自定义 NAT 配额");
            c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => SwitchConfigurationRules.GetCreationError("new", (SwitchMode)99, null, [], physical, bridgeable)), "未知模式");
        }]);
        add("SwitchConfigurationRules.GetUpdateConflict()", [c =>
        {
            c.Equal<string?>(null, c.Call(() => SwitchConfigurationRules.GetUpdateConflict(owned.Id, SwitchMode.NAT, wired, [owned])), "保留自身上游");
            c.Equal<string?>(null, c.Call(() => SwitchConfigurationRules.GetUpdateConflict(owned.Id, SwitchMode.Bridge, spare, [owned])), "改为空闲上游");
            c.Require(c.Call(() => SwitchConfigurationRules.GetUpdateConflict("other", SwitchMode.Bridge, wired with { Name = "改名" }, [owned])) is not null, "按身份检测上游占用");
            c.Require(c.Call(() => SwitchConfigurationRules.GetUpdateConflict("other", SwitchMode.NAT, spare, [owned])) is not null, "额外 NAT 冲突");
            c.Require(c.Call(() => SwitchConfigurationRules.GetUpdateConflict(defaultId, SwitchMode.Isolated, null, [])) is not null, "系统默认交换机受保护");
        }]);
        add("SwitchConfigurationConflictException", [c => c.Equal("固定冲突说明", c.Call(() => new SwitchConfigurationConflictException("固定冲突说明")).Message, "异常保留说明")]);
    }
}
