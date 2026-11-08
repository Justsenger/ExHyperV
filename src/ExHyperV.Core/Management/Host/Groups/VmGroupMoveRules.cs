using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>仅根据分组快照判断移动是否合法，不访问或修改宿主。</summary>
public static class VmGroupMoveRules
{
    /// <summary>在分组快照上检查一批虚拟机或分组是否能移动到目标组。</summary>
    /// <param name="snapshot">查询时的分组和成员快照。</param>
    /// <param name="items">准备移动的虚拟机或分组身份，不应混合两种类型。</param>
    /// <param name="targetId">目标分组 GUID；null 表示移出当前分组。</param>
    /// <returns>当前快照满足类型、层级和循环限制时为 true；不锁定主机状态。</returns>
    public static bool CanMove(VmGroupSnapshot snapshot, IReadOnlyList<VmGroupMove> items, Guid? targetId)
    {
        if (items.Count == 0 || items.Select(i => i.IsGroup).Distinct().Count() != 1) return false;
        var groups = snapshot.Groups.ToDictionary(g => g.Id);
        if (items.Any(i => i.IsGroup && !groups.ContainsKey(i.Id))) return false;
        foreach (var item in items)
            if (item.SourceParentId is Guid parent && (!groups.TryGetValue(parent, out var source)
                || !(item.IsGroup ? source.ChildIds : source.VmIds).Contains(item.Id))) return false;
        if (targetId == null) return items.Any(i => i.SourceParentId.HasValue);
        if (!groups.TryGetValue(targetId.Value, out var target)) return false;
        bool groupMembers = items[0].IsGroup;
        if (!target.IsEmpty && (target.Kind == VmGroupKind.Groups) != groupMembers) return false;
        if (items.All(i => i.SourceParentId == targetId)) return false;
        foreach (var item in items.Where(i => i.IsGroup))
        {
            var pending = new Stack<Guid>(); var visited = new HashSet<Guid>(); pending.Push(item.Id);
            while (pending.TryPop(out var id))
            {
                if (id == targetId) return false;
                if (visited.Add(id) && groups.TryGetValue(id, out var group))
                    foreach (var child in group.ChildIds) pending.Push(child);
            }
        }
        return true;
    }
}
