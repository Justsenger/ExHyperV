namespace ExHyperV.Models;

public enum VmGroupKind : ushort { VirtualMachines = 0, Groups = 1 }

public sealed record VmGroup(Guid Id, string Name, VmGroupKind Kind, string Path)
{
    public bool IsEmpty => VmIds.Count == 0 && ChildIds.Count == 0;
    public HashSet<Guid> VmIds { get; } = [];
    public HashSet<Guid> ChildIds { get; } = [];
}

public sealed record VmGroupMove(Guid Id, bool IsGroup, Guid? SourceParentId);

public static class VmGroupMoveRules
{
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

/// <summary>Native memberships form a graph: a VM or group may have multiple parents.</summary>
public sealed class VmGroupSnapshot(IReadOnlyList<VmGroup> groups)
{
    public IReadOnlyList<VmGroup> Groups { get; } = groups;

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
