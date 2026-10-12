using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels;

/// <summary>A visible tree row. Multiple paths may refer to the same native group or VM.</summary>
public sealed record VmNavigationItem(string Key, int Depth, VmGroup? Group, VmInstanceViewModel? Vm,
    bool IsExpanded = false, bool HasChildren = false, Guid? ParentId = null)
{
    public VmGroupNameEdit NameEdit { get; init; } = new();
    public bool IsGroup => Group != null;
    public bool CanContainGroups => Group != null && (Group.Kind == VmGroupKind.Groups || Group.IsEmpty);
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string Name => Group?.Name ?? Vm?.Name ?? string.Empty;
}

public partial class VmGroupNameEdit : ObservableObject
{
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editedName = string.Empty;
}

internal static class VmGroupNavigation
{
    public static IReadOnlyList<VmNavigationItem> Build(VmGroupSnapshot snapshot,
        IEnumerable<VmInstanceViewModel> machines, ISet<string> collapsed, string search)
    {
        var vms = machines.OrderByDescending(v => v.IsRunning).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var groups = snapshot.Groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id).ToArray();
        var children = groups.SelectMany(g => g.ChildIds).ToHashSet();
        var assigned = groups.SelectMany(g => g.VmIds).ToHashSet();
        var visited = new HashSet<Guid>();
        var result = new List<VmNavigationItem>();
        string query = search.Trim();
        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        List<VmNavigationItem> Visit(VmGroup group, string prefix, int depth, HashSet<Guid> ancestors, bool parentMatches, Guid? parentId = null)
        {
            string key = prefix + "/g:" + group.Id;
            visited.Add(group.Id);
            ancestors.Add(group.Id);
            bool matches = parentMatches || Matches(group.Name);
            var descendants = new List<VmNavigationItem>();
            foreach (var child in groups.Where(g => group.ChildIds.Contains(g.Id) && !ancestors.Contains(g.Id)))
                descendants.AddRange(Visit(child, key, depth + 1, new(ancestors), matches, group.Id));
            foreach (var vm in vms.Where(v => group.VmIds.Contains(v.Id) && (matches || Matches(v.Name))))
                descendants.Add(new(key + "/v:" + vm.Id, depth + 1, null, vm, ParentId: group.Id));
            if (!matches && descendants.Count == 0) return [];
            bool expanded = query.Length > 0 || !collapsed.Contains(key);
            var rows = new List<VmNavigationItem> { new(key, depth, group, null, expanded, descendants.Count > 0, parentId) };
            if (expanded) rows.AddRange(descendants);
            return rows;
        }
        foreach (var vm in vms.Where(v => !assigned.Contains(v.Id) && Matches(v.Name)))
            result.Add(new("v:" + vm.Id, 0, null, vm));
        foreach (var group in groups.Where(g => !children.Contains(g.Id)))
            result.AddRange(Visit(group, string.Empty, 0, [], false));
        // Recover roots for externally-created orphan/cyclic graphs without recursive loops.
        foreach (var group in groups)
            if (!visited.Contains(group.Id)) result.AddRange(Visit(group, string.Empty, 0, [], false));
        return result;
    }
}
