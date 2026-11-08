using System.Management;
using System.Xml.Linq;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>通过 Hyper-V 分组接口读取及修改分组；本进程内串行执行写入。</summary>
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

    /// <summary>读取当前虚拟机分组、成员及层级关系。</summary>
    /// <returns>分组快照；底层查询失败以异常报告。</returns>
    /// <queryentry label="分组及成员关系" category="分组"/>
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
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    private static VmGroup Require(VmGroupSnapshot snapshot, Guid id) =>
        snapshot.Groups.FirstOrDefault(g => g.Id == id)
        ?? throw new InvalidOperationException(Properties.Resources.VmGroup_NoLongerExists);

    private static Task MemberAsync(string method, string groupPath, string memberPath) =>
        InvokeAsync(method, p => { p["Collection"] = groupPath; p["Member"] = memberPath; });

    /// <summary>创建虚拟机分组或管理分组，并按需加入父分组。</summary>
    /// <param name="name">分组显示名称。</param>
    /// <param name="kind">组类型：容纳虚拟机的集合组或容纳子组的管理组。</param>
    /// <param name="parentId">可选父分组 GUID；null 表示创建顶层分组。</param>
    /// <returns>新分组 GUID；失败抛出异常，回滚失败时可能包含聚合错误。底层结果尚未确认时保留现状，不自动补偿或重试。</returns>
    /// <remarks>指定的父组若为空的虚拟机组，会先重建为容纳子组的管理组，父组 GUID 随之改变；本返回值只有新建子组的 GUID，调用后须重新读取父组关系。后续创建失败也不保证恢复原父组身份。</remarks>
    /// <exception cref="CoreOperationException">创建或补偿操作的结果尚未确认；Result.ErrorKind 为 OutcomeUnknown，保留现状。</exception>
    /// <editentry label="创建分组" category="分组"/>
    public static async Task<Guid> CreateAsync(string name, VmGroupKind kind, Guid? parentId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException(nameof(name));
        await Writes.WaitAsync();
        try { return await Transactions.CreateAsync(name, kind, parentId); }
        finally { Writes.Release(); }
    }

    // Callers hold Writes; conversion stages a new native identity before retiring the old one.
    private static Task<VmGroup> RetypeEmptyAsync(Guid id, VmGroupKind kind) => Transactions.RetypeEmptyAsync(id, kind);

    /// <summary>串行修改虚拟机或子分组归属，并在失败时尝试恢复已执行的变更。</summary>
    /// <param name="moves">待移动的虚拟机或分组身份列表。</param>
    /// <param name="targetId">目标分组 GUID；null 表示移出当前分组。</param>
    /// <returns>调用时选择的目标 GUID 与实际目标 GUID；目标空组被重建时两者不同，后续操作应使用 TargetId。移出当前分组时两者均为 null。</returns>
    /// <remarks>只移除每项 SourceParentId 指定的源关系，未指定的其他父组关系保留。移动进入类型不匹配的空组时会重建该组并改变 GUID；成功结果返回此次身份变化，成员列表仍需重读。成员移动失败也可能已完成空组重建；异常不会返回该身份映射。尽力恢复失败可形成 AggregateException，底层操作或恢复结果未知时停止后续补偿。</remarks>
    /// <exception cref="CoreOperationException">成员修改或补偿操作的结果尚未确认；停止后续修改，需先核对实际归属。</exception>
    /// <editentry label="移动成员或分组" category="分组"/>
    public static async Task<VmGroupMoveResult> MoveAsync(IReadOnlyList<VmGroupMove> moves, Guid? targetId)
    {
        Guid? requestedTargetId = targetId;
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
            catch (Exception error) when (error is not OperationOutcomeUnknownException)
            {
                try
                {
                    await Transactions.RestoreMoveAsync(removed, added, paths, target?.Id);
                }
                catch (OperationOutcomeUnknownException recovery)
                {
                    throw new OperationOutcomeUnknownException(ApiResponse.Fail(error.Message + Environment.NewLine + recovery.Message,
                        recovery.Result.Code, recovery.Result.ErrorSource, new AggregateException(error, recovery), ApiErrorKind.OutcomeUnknown));
                }
                catch (Exception recovery) { throw new AggregateException(Properties.Resources.VmGroup_RecoveryFailed, error, recovery); }
                throw;
            }
        }
        finally { Writes.Release(); }
        return new(requestedTargetId, targetId);
    }

    /// <summary>按 GUID 重命名分组；失败以异常报告。</summary>
    /// <param name="id">目标分组 GUID。</param>
    /// <param name="name">分组显示名称。</param>
    /// <exception cref="CoreOperationException">重命名结果尚未确认；不能将其视为没有修改。</exception>
    /// <editentry label="分组名称" category="分组"/>
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

    /// <summary>删除指定分组及其父子成员关系，保留成员虚拟机和子分组本身。</summary>
    /// <param name="id">目标分组 GUID。</param>
    /// <exception cref="CoreOperationException">删除或恢复成员关系的结果尚未确认；停止后续补偿，需先核对实际状态。</exception>
    /// <editentry label="删除分组" category="分组"/>
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
            try
            {
                await InvokeAsync("RemoveMemberById", p => { p["CollectionId"] = id.ToString(); p["Member"] = plannedPath; });
            }
            catch (CoreOperationException ex) when (ex.Result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            {
                // 此包装未保留原生作业身份，不能凭单次查询推定任务结束并销毁计划系统。
                guard.BlockUnknown();
                throw;
            }
            removed++;
        }
        return removed;
    }, cancellationToken);
}
