namespace ExHyperV.Models;

/// <summary>Hyper-V 原生分组允许包含的成员类别。</summary>
public enum VmGroupKind : ushort
{
    /// <summary>包含虚拟机的分组。</summary>
    VirtualMachines = 0,
    /// <summary>包含其他分组的分组。</summary>
    Groups = 1
}

/// <summary>Hyper-V 分组及其成员快照。成员集合仍可修改；修改模型集合不会写回 Hyper-V。</summary>
/// <param name="Id">分组 GUID。</param>
/// <param name="Name">分组名称。</param>
/// <param name="Kind">分组允许包含的成员类别。</param>
public sealed record VmGroup(Guid Id, string Name, VmGroupKind Kind)
{
    internal string Path { get; init; } = string.Empty;

    internal VmGroup(Guid id, string name, VmGroupKind kind, string path)
        : this(id, name, kind) => Path = path;

    /// <summary>分组中是否没有虚拟机或子分组。</summary>
    /// <fielddoc>
    /// <meaning>分组中是否没有虚拟机或子分组。</meaning>
    /// <values kind="options">true：两类成员均为空；false：至少包含一项成员。</values>
    /// </fielddoc>
    public bool IsEmpty => VmIds.Count == 0 && ChildIds.Count == 0;
    /// <summary>直接属于该组的虚拟机标识集合。</summary>
    /// <fielddoc>
    /// <meaning>直接属于该组的虚拟机标识集合。</meaning>
    /// <values kind="format">GUID 集合；修改集合仅改变本地快照。</values>
    /// </fielddoc>
    public HashSet<Guid> VmIds { get; } = [];
    /// <summary>直接属于该组的子分组标识集合。</summary>
    /// <fielddoc>
    /// <meaning>直接属于该组的子分组标识集合。</meaning>
    /// <values kind="format">GUID 集合；修改集合仅改变本地快照。</values>
    /// </fielddoc>
    public HashSet<Guid> ChildIds { get; } = [];
}

/// <summary>一次分组成员移动所需的源成员及原父组信息。</summary>
/// <param name="Id">待移动虚拟机或分组的 GUID。</param>
/// <param name="IsGroup">true 表示分组，false 表示虚拟机。</param>
/// <param name="SourceParentId">原父组 GUID；null 表示没有指定原父组。</param>
public sealed record VmGroupMove(Guid Id, bool IsGroup, Guid? SourceParentId);

/// <summary>原生分组的成员关系构成图结构；同一虚拟机或分组可以属于多个父分组。</summary>
/// <param name="groups">分组列表；保留此引用而非深复制，分组 GUID 应唯一。</param>
public sealed class VmGroupSnapshot(IReadOnlyList<VmGroup> groups)
{
    /// <summary>构造时传入的分组列表引用；与调用方共享列表及元素。</summary>
    public IReadOnlyList<VmGroup> Groups { get; } = groups;

    /// <summary>沿子分组收集所有虚拟机 GUID，去重且避免循环遍历。</summary>
    /// <param name="groupId">起始分组 GUID。</param>
    /// <returns>新建的 GUID 集合；找不到起始分组时为空。</returns>
    public HashSet<Guid> GetVmIds(Guid groupId)
    {
        var result = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(groupId);
        var lookup = Groups.ToDictionary(g => g.Id);
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id) || !lookup.TryGetValue(id, out var group)) continue;
            result.UnionWith(group.VmIds);
            foreach (var child in group.ChildIds) pending.Push(child);
        }
        return result;
    }

    /// <summary>检查父组能否包含指定子组，包括成员类型和循环关系检查。</summary>
    /// <param name="parentId">候选父分组 GUID。</param>
    /// <param name="childId">候选子分组 GUID。</param>
    /// <returns>两个组均存在、父组允许包含分组且不会形成循环时为 true。</returns>
    public bool CanContain(Guid parentId, Guid childId)
    {
        var lookup = Groups.ToDictionary(g => g.Id);
        if (parentId == childId || !lookup.TryGetValue(parentId, out var parent)
            || parent.Kind != VmGroupKind.Groups || !lookup.ContainsKey(childId)) return false;
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(childId);
        while (pending.TryPop(out var id))
        {
            if (id == parentId) return false;
            if (!visited.Add(id) || !lookup.TryGetValue(id, out var group)) continue;
            foreach (var child in group.ChildIds) pending.Push(child);
        }
        return true;
    }
}
