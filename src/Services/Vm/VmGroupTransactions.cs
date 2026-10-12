using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>Compensating group operations. The caller serializes writes; the backend owns WMI access.</summary>
internal sealed class VmGroupTransactions(
    Func<Task<VmGroupSnapshot>> read,
    Func<string, IReadOnlyDictionary<string, object>, Task> invoke,
    Func<Guid, VmGroupKind, Task<VmGroup>> retype)
{
    private static VmGroup Require(VmGroupSnapshot snapshot, Guid id) => snapshot.Groups.FirstOrDefault(g => g.Id == id)
        ?? throw new InvalidOperationException(Properties.Resources.VmGroup_NoLongerExists);

    private Task Member(string method, string parent, string child) =>
        invoke(method, new Dictionary<string, object> { ["Collection"] = parent, ["Member"] = child });

    private static Exception RecoveryFailed(Exception error, Exception recovery) =>
        new AggregateException(Properties.Resources.VmGroup_RecoveryFailed, error, recovery);

    // A native collection cannot change type. Stage a replacement with a fresh identity before
    // removing the old empty collection; never rely on reusing a GUID the provider has just deleted.
    public async Task<VmGroup> RetypeEmptyAsync(Guid id, VmGroupKind kind)
    {
        var before = await read();
        var original = Require(before, id);
        if (original.Kind == kind) return original;
        if (!original.IsEmpty) throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
        var parents = before.Groups.Where(g => g.ChildIds.Contains(id)).Select(g => g.Id).ToArray();
        var replacementId = Guid.NewGuid();
        async Task<VmGroup> VerifyReplacement()
        {
            var current = await read();
            var replacement = Require(current, replacementId);
            if (replacement.Kind != kind || replacement.Name != original.Name ||
                parents.Any(p => !Require(current, p).ChildIds.Contains(replacementId)))
                throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
            return replacement;
        }
        try
        {
            await invoke("DefineCollection", new Dictionary<string, object>
                { ["Id"] = replacementId.ToString(), ["Name"] = original.Name, ["Type"] = (ushort)kind });
            var replacement = Require(await read(), replacementId);
            foreach (var parent in parents)
                await Member("AddMember", Require(await read(), parent).Path, replacement.Path);
            await VerifyReplacement();
            foreach (var parent in parents)
                await Member("RemoveMember", Require(await read(), parent).Path, original.Path);
            var ready = await read();
            // Refuse to destroy a group changed concurrently by another management tool.
            if (!Require(ready, id).IsEmpty || Require(ready, id).Name != original.Name ||
                ready.Groups.Any(g => g.ChildIds.Contains(id)))
                throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
            await VerifyReplacement();
            await invoke("DestroyCollection", new Dictionary<string, object> { ["Collection"] = original.Path });
            if ((await read()).Groups.Any(g => g.Id == id || g.ChildIds.Contains(id)))
                throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
            return await VerifyReplacement();
        }
        catch (Exception error)
        {
            try
            {
                var current = await read();
                // Destroy may have succeeded despite a failed response. Keep the verified replacement.
                if (!current.Groups.Any(g => g.Id == id))
                {
                    if (current.Groups.Any(g => g.ChildIds.Contains(id)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    return await VerifyReplacement();
                }
                var failures = new List<Exception>();
                foreach (var parent in parents)
                {
                    try
                    {
                        current = await read();
                        var owner = Require(current, parent);
                        if (!owner.ChildIds.Contains(id))
                            await Member("AddMember", owner.Path, Require(current, id).Path);
                    }
                    catch (Exception ex) { failures.Add(ex); }
                }
                current = await read();
                if (parents.Any(p => !Require(current, p).ChildIds.Contains(id)))
                    throw new AggregateException(Properties.Resources.VmGroup_VerifyFailed, failures);
                // Only discard the staged group once the original's relationships are confirmed.
                if (current.Groups.FirstOrDefault(g => g.Id == replacementId) is { } staged)
                {
                    if (!staged.IsEmpty || current.Groups.Any(g => g.ChildIds.Contains(replacementId) && !parents.Contains(g.Id)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    foreach (var parent in parents)
                    {
                        current = await read();
                        var owner = Require(current, parent);
                        if (owner.ChildIds.Contains(replacementId))
                            await Member("RemoveMember", owner.Path, staged.Path);
                    }
                    current = await read();
                    if (!Require(current, replacementId).IsEmpty || current.Groups.Any(g => g.ChildIds.Contains(replacementId)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    try { await invoke("DestroyCollection", new Dictionary<string, object> { ["Collection"] = staged.Path }); }
                    catch { if ((await read()).Groups.Any(g => g.Id == replacementId)) throw; }
                }
                current = await read();
                Require(current, id);
                if (current.Groups.Any(g => g.Id == replacementId || g.ChildIds.Contains(replacementId)) ||
                    parents.Any(p => !Require(current, p).ChildIds.Contains(id)))
                    throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
            }
            catch (Exception recovery) { throw RecoveryFailed(error, recovery); }
            throw;
        }
    }

    public async Task RestoreMoveAsync(IReadOnlyList<VmGroupMove> removed, IReadOnlyList<VmGroupMove> added,
        IReadOnlyDictionary<(Guid, bool), string> paths, Guid? targetId)
    {
        var failures = new List<Exception>();
        bool HasEdge(VmGroupSnapshot snapshot, VmGroupMove item) =>
            snapshot.Groups.FirstOrDefault(g => g.Id == item.SourceParentId) is { } source &&
            (item.IsGroup ? source.ChildIds : source.VmIds).Contains(item.Id);
        foreach (var item in removed)
        {
            try
            {
                var current = await read();
                var source = Require(current, item.SourceParentId!.Value);
                if (!HasEdge(current, item))
                    await Member("AddMember", source.Path, paths[(item.Id, item.IsGroup)]);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        // An unrestored member keeps its destination link. Other members can still be rolled back.
        if (targetId is Guid target)
            foreach (var item in added)
            {
                try
                {
                    var current = await read();
                    if (removed.Any(r => r.Id == item.Id && r.IsGroup == item.IsGroup && !HasEdge(current, r))) continue;
                    var destination = Require(current, target);
                    if ((item.IsGroup ? destination.ChildIds : destination.VmIds).Contains(item.Id))
                        await Member("RemoveMember", destination.Path, paths[(item.Id, item.IsGroup)]);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
        var restored = await read();
        if (removed.Any(i => !HasEdge(restored, i)) || (targetId is Guid dest &&
            added.Any(i => (i.IsGroup ? Require(restored, dest).ChildIds : Require(restored, dest).VmIds).Contains(i.Id))))
            throw new AggregateException(Properties.Resources.VmGroup_VerifyFailed, failures);
    }

    public async Task<Guid> CreateAsync(string name, VmGroupKind kind, Guid? parentId)
    {
        var before = await read();
        var parent = parentId is Guid pid ? Require(before, pid) : null;
        // Conversion has its own recovery. Subsequent create/cleanup must use the replacement ID.
        if (parent != null && parent.Kind != VmGroupKind.Groups)
        {
            parent = await retype(parent.Id, VmGroupKind.Groups);
            parentId = parent.Id;
            before = await read();
        }
        var id = Guid.NewGuid();
        try
        {
            await invoke("DefineCollection", new Dictionary<string, object> { ["Id"] = id.ToString(), ["Name"] = name.Trim(), ["Type"] = (ushort)kind });
            var created = Require(await read(), id);
            if (parent != null) await Member("AddMember", parent.Path, created.Path);
            var confirmed = await read();
            Require(confirmed, id);
            if (parent != null && !Require(confirmed, parent.Id).ChildIds.Contains(id))
                throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
            return id;
        }
        catch (Exception error)
        {
            try
            {
                var current = await read();
                var created = current.Groups.FirstOrDefault(g => g.Id == id);
                if (created != null)
                {
                    // Never delete contents/links another tool added while this operation was in flight.
                    if (!created.IsEmpty || current.Groups.Any(g => g.ChildIds.Contains(id) && g.Id != parentId))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    if (parentId is Guid destination && current.Groups.FirstOrDefault(g => g.Id == destination) is { } owner && owner.ChildIds.Contains(id))
                        await Member("RemoveMember", owner.Path, created.Path);
                    current = await read();
                    if (!Require(current, id).IsEmpty || current.Groups.Any(g => g.ChildIds.Contains(id)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    try { await invoke("DestroyCollection", new Dictionary<string, object> { ["Collection"] = created.Path }); }
                    catch { if ((await read()).Groups.Any(g => g.Id == id)) throw; }
                }
                var cleaned = await read();
                if (cleaned.Groups.Any(g => g.Id == id || g.ChildIds.Contains(id)))
                    throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                if (parentId is Guid originalParentId)
                {
                    var original = Require(before, originalParentId);
                    var restored = Require(cleaned, originalParentId);
                    if (!original.ChildIds.IsSubsetOf(restored.ChildIds) || !original.VmIds.IsSubsetOf(restored.VmIds)
                        || before.Groups.Where(g => g.ChildIds.Contains(originalParentId))
                            .Any(g => !Require(cleaned, g.Id).ChildIds.Contains(originalParentId)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                }
                // Empty collections accept either member type on the next operation. Do not destroy/recreate
                // an existing empty parent again merely to restore its invisible type after a failed create.
            }
            catch (Exception recovery) { throw RecoveryFailed(error, recovery); }
            throw;
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        var before = await read();
        var group = Require(before, id);
        var parents = before.Groups.Where(g => g.ChildIds.Contains(id)).Select(g => g.Id).ToArray();
        // Resolve every child before changing relationships. VM references retain the native namespace.
        var children = group.ChildIds.ToDictionary(child => child, child => Require(before, child).Path);
        string scope = group.Path[..(group.Path.LastIndexOf(':') + 1)];
        var machines = group.VmIds.ToDictionary(vm => vm, vm =>
            $"{scope}Msvm_ComputerSystem.CreationClassName=\"Msvm_ComputerSystem\",Name=\"{vm:D}\"");
        try
        {
            foreach (var parent in parents) await Member("RemoveMember", Require(before, parent).Path, group.Path);
            foreach (var child in children.Values) await Member("RemoveMember", group.Path, child);
            foreach (var vm in machines.Values)
                await invoke("RemoveMemberById", new Dictionary<string, object> { ["CollectionId"] = id.ToString(), ["Member"] = vm });
            await invoke("DestroyCollection", new Dictionary<string, object> { ["Collection"] = group.Path });
            var after = await read();
            if (after.Groups.Any(g => g.Id == id || g.ChildIds.Contains(id)))
                throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
        }
        catch (Exception error)
        {
            try
            {
                var current = await read();
                // A failed response does not prove DestroyCollection failed. Do not resurrect a deleted group.
                if (!current.Groups.Any(g => g.Id == id))
                {
                    if (current.Groups.Any(g => g.ChildIds.Contains(id)))
                        throw new InvalidOperationException(Properties.Resources.VmGroup_VerifyFailed);
                    return;
                }
                var failures = new List<Exception>();
                async Task Restore(Func<Task> action)
                {
                    try { await action(); }
                    catch (Exception ex) { failures.Add(ex); }
                }
                // Attempt all restorations even if one parent/member has disappeared externally.
                foreach (var child in children.Keys)
                    await Restore(async () =>
                    {
                        var snapshot = await read(); var owner = Require(snapshot, id);
                        if (!owner.ChildIds.Contains(child)) await Member("AddMember", owner.Path, Require(snapshot, child).Path);
                    });
                foreach (var vm in machines)
                    await Restore(async () =>
                    {
                        var owner = Require(await read(), id);
                        if (!owner.VmIds.Contains(vm.Key)) await Member("AddMember", owner.Path, vm.Value);
                    });
                foreach (var parent in parents)
                    await Restore(async () =>
                    {
                        var snapshot = await read(); var owner = Require(snapshot, parent);
                        if (!owner.ChildIds.Contains(id)) await Member("AddMember", owner.Path, Require(snapshot, id).Path);
                    });
                var restored = await read();
                var result = Require(restored, id);
                if (!group.VmIds.IsSubsetOf(result.VmIds) || !group.ChildIds.IsSubsetOf(result.ChildIds)
                    || parents.Any(p => !Require(restored, p).ChildIds.Contains(id)))
                    throw new AggregateException(Properties.Resources.VmGroup_VerifyFailed, failures);
            }
            catch (Exception recovery) { throw RecoveryFailed(error, recovery); }
            throw;
        }
    }
}
