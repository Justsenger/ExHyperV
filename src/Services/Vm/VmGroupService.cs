using System.Management;
using System.Xml.Linq;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>Local Hyper-V native groups. No application-owned copy of memberships.</summary>
public static class VmGroupService
{
    private const string Service = "SELECT * FROM Msvm_CollectionManagementService";
    private static readonly SemaphoreSlim Writes = new(1, 1);
    private static readonly VmGroupTransactions Transactions = new(ReadAsync,
        (method, args) => InvokeAsync(method, p => { foreach (var arg in args) p[arg.Key] = arg.Value; }), RetypeEmptyAsync);

    private static List<T> Query<T>(string wql, Func<ManagementObject, T> map)
    {
        using var searcher = new ManagementObjectSearcher(WmiScope.HyperV, wql);
        using var objects = searcher.Get();
        var values = new List<T>();
        foreach (ManagementObject obj in objects)
            using (obj) values.Add(map(obj));
        return values;
    }

    private static Guid ReferenceId(string path, string property)
    {
        // Extract a reference key without dereferencing an object deleted between queries.
        var relative = new ManagementPath(path).RelativePath;
        var match = System.Text.RegularExpressions.Regex.Match(relative,
            $@"\b{property}=""(?<id>[0-9a-fA-F-]{{36}})""");
        return match.Success && Guid.TryParse(match.Groups["id"].Value, out var id) ? id : Guid.Empty;
    }

    public static Task<VmGroupSnapshot> ReadAsync() => Task.Run(() =>
    {
        var groups = new List<VmGroup>();
        foreach (var (cls, kind) in new[] { ("Msvm_VirtualSystemCollection", VmGroupKind.VirtualMachines),
                     ("Msvm_ManagementCollection", VmGroupKind.Groups) })
            groups.AddRange(Query($"SELECT * FROM {cls}", o => new VmGroup(
                Guid.Parse((string)o["CollectionID"]), (string)o["ElementName"], kind, o.Path.Path)));
        var lookup = groups.ToDictionary(g => g.Id);
        foreach (var cls in new[] { "Msvm_CollectedVirtualSystems", "Msvm_CollectedCollections" })
        {
            bool vmMember = cls == "Msvm_CollectedVirtualSystems";
            foreach (var edge in Query($"SELECT * FROM {cls}", o => (
                         Parent: ReferenceId((string)o["Collection"], "CollectionID"),
                         Member: ReferenceId((string)o["Member"], vmMember ? "Name" : "CollectionID"))))
                if (edge.Member != Guid.Empty && lookup.TryGetValue(edge.Parent, out var group))
                    (vmMember ? group.VmIds : group.ChildIds).Add(edge.Member);
        }
        return new VmGroupSnapshot(groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    });

    private static async Task InvokeAsync(string method, Action<ManagementBaseObject> set)
    {
        var result = await WmiApi.InvokeAsync(Service, method, set);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    private static VmGroup Require(VmGroupSnapshot snapshot, Guid id) =>
        snapshot.Groups.FirstOrDefault(g => g.Id == id)
        ?? throw new InvalidOperationException(Properties.Resources.VmGroup_NoLongerExists);

    private static Task MemberAsync(string method, string groupPath, string memberPath) =>
        InvokeAsync(method, p => { p["Collection"] = groupPath; p["Member"] = memberPath; });

    public static async Task<Guid> CreateAsync(string name, VmGroupKind kind, Guid? parentId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(nameof(name));
        await Writes.WaitAsync();
        try { return await Transactions.CreateAsync(name, kind, parentId); }
        finally { Writes.Release(); }
    }

    // Callers hold Writes; conversion stages a new native identity before retiring the old one.
    private static Task<VmGroup> RetypeEmptyAsync(Guid id, VmGroupKind kind) => Transactions.RetypeEmptyAsync(id, kind);

    public static async Task MoveAsync(IReadOnlyList<VmGroupMove> moves, Guid? targetId)
    {
        await Writes.WaitAsync();
        try
        {
            var items = moves.Distinct().ToArray();
            var before = await ReadAsync();
            if (!VmGroupMoveRules.CanMove(before, items, targetId))
                throw new InvalidOperationException(Properties.Resources.VmGroup_Cycle);
            var paths = new Dictionary<(Guid, bool), string>();
            foreach (var item in items)
            {
                if (item.IsGroup) paths[(item.Id, true)] = Require(before, item.Id).Path;
                else
                {
                    var vm = await WmiApi.QueryFirstAsync($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{item.Id:D}'", o => o.Path.Path);
                    if (!vm.Success || string.IsNullOrEmpty(vm.Data)) throw new InvalidOperationException(vm.Error);
                    paths[(item.Id, false)] = vm.Data;
                }
            }
            VmGroup? target = null;
            if (targetId is Guid targetGuid)
            {
                target = await RetypeEmptyAsync(targetGuid, items[0].IsGroup ? VmGroupKind.Groups : VmGroupKind.VirtualMachines);
                targetId = target.Id;
            }
            var added = new List<VmGroupMove>();
            var removed = new List<VmGroupMove>();
            try
            {
                // Establish every destination edge first. Never leave a VM/group unassigned on a failed add.
                foreach (var item in items.DistinctBy(i => (i.Id, i.IsGroup)))
                    if (target != null && !(item.IsGroup ? target.ChildIds : target.VmIds).Contains(item.Id))
                    {
                        added.Add(item);
                        await MemberAsync("AddMember", target.Path, paths[(item.Id, item.IsGroup)]);
                    }
                if (target != null)
                {
                    var current = Require(await ReadAsync(), target.Id);
                    if (items.Any(i => !(i.IsGroup ? current.ChildIds : current.VmIds).Contains(i.Id)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                }
                foreach (var item in items)
                    if (item.SourceParentId is Guid source && source != targetId)
                    {
                        removed.Add(item);
                        await MemberAsync("RemoveMember", Require(before, source).Path, paths[(item.Id, item.IsGroup)]);
                    }
                var after = await ReadAsync();
                foreach (var item in items)
                {
                    if (item.SourceParentId is Guid source && source != targetId &&
                        (item.IsGroup ? Require(after, source).ChildIds : Require(after, source).VmIds).Contains(item.Id))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    if (targetId is Guid destination && !(item.IsGroup ? Require(after, destination).ChildIds : Require(after, destination).VmIds).Contains(item.Id))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                }
            }
            catch (Exception error)
            {
                try
                {
                    await Transactions.RestoreMoveAsync(removed, added, paths, target?.Id);
                }
                catch (Exception recovery) { throw new AggregateException(Properties.Resources.VmGroup_RecoveryFailed, error, recovery); }
                throw;
            }
        }
        finally { Writes.Release(); }
    }

    public static async Task RenameAsync(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(nameof(name));
        await Writes.WaitAsync();
        try
        {
            var group = Require(await ReadAsync(), id);
            await InvokeAsync("RenameCollection", p => { p["Collection"] = group.Path; p["NewName"] = name.Trim(); });
            if (Require(await ReadAsync(), id).Name != name.Trim()) throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
        }
        finally { Writes.Release(); }
    }

    public static async Task SetMembershipAsync(Guid groupId, Guid memberId, bool isGroup, bool add)
    {
        await Writes.WaitAsync();
        try
        {
            var snapshot = await ReadAsync();
            var parent = Require(snapshot, groupId);
            string memberPath;
            if (isGroup)
            {
                var child = Require(snapshot, memberId);
                if (parent.Kind != VmGroupKind.Groups || (add && !snapshot.CanContain(groupId, memberId)))
                    throw new InvalidOperationException(Properties.Resources.VmGroup_Cycle);
                memberPath = child.Path;
            }
            else
            {
                if (parent.Kind != VmGroupKind.VirtualMachines) throw new InvalidOperationException(Properties.Resources.VmGroup_OnlyVms);
                var vm = await WmiApi.QueryFirstAsync($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{memberId:D}'", o => o.Path.Path);
                if (!vm.Success || string.IsNullOrEmpty(vm.Data)) throw new InvalidOperationException(vm.Error);
                memberPath = vm.Data;
            }
            await MemberAsync(add ? "AddMember" : "RemoveMember", parent.Path, memberPath);
            var current = Require(await ReadAsync(), groupId);
            if ((isGroup ? current.ChildIds : current.VmIds).Contains(memberId) != add)
                throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
        }
        finally { Writes.Release(); }
    }

    public static async Task DeleteAsync(Guid id)
    {
        await Writes.WaitAsync();
        try { await Transactions.DeleteAsync(id); }
        finally { Writes.Release(); }
    }

    /// <summary>40046 identifies a missing collection. Never parse localized error prose.</summary>
    internal static IReadOnlyList<Guid> ParseMissingGroups(IEnumerable<string> errors, Guid plannedId)
    {
        var ids = new HashSet<Guid>();
        foreach (string error in errors)
        {
            var root = XElement.Parse(error);
            string? messageId = root.Elements("PROPERTY").FirstOrDefault(p => (string?)p.Attribute("NAME") == "MessageID")?.Element("VALUE")?.Value;
            if (messageId != "40046") continue;
            var args = root.Elements("PROPERTY.ARRAY").FirstOrDefault(p => (string?)p.Attribute("NAME") == "MessageArguments")
                ?.Element("VALUE.ARRAY")?.Elements("VALUE").Select(v => v.Value).ToArray();
            if (args?.Length != 4 || !Guid.TryParse(args[1], out var vmId) || vmId != plannedId || !Guid.TryParse(args[3], out var groupId))
                throw new InvalidOperationException(Properties.Resources.VmGroup_InvalidImportReference);
            ids.Add(groupId);
        }
        return ids.ToList();
    }

    internal static Task<int> RemoveMissingImportGroupsAsync(string plannedPath, VmImportCleanupGuard guard,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var path = new ManagementPath(plannedPath);
        if (path.ClassName != "Msvm_PlannedComputerSystem") throw new ArgumentException(nameof(plannedPath));
        using var planned = new ManagementObject(plannedPath);
        planned.Get();
        var plannedId = Guid.Parse((string)planned["Name"]);
        var errors = await VmImportService.ValidatePlannedSystemAsync(plannedPath, guard, cancellationToken,
            allowValidationErrors: true, timeout: TimeSpan.FromMinutes(2));
        var missing = ParseMissingGroups(errors, plannedId);
        int removed = 0;
        foreach (var id in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The original group may have been recreated since validation. Preserve it if present.
            if ((await ReadAsync()).Groups.Any(g => g.Id == id)) continue;
            await InvokeAsync("RemoveMemberById", p => { p["CollectionId"] = id.ToString(); p["Member"] = plannedPath; });
            removed++;
        }
        return removed;
    }, cancellationToken);
}
