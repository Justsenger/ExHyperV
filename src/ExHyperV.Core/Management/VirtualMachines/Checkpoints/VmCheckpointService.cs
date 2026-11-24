using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>管理快照、应用、合并、删除及历史磁盘临时挂接，相关操作可能修改配置和磁盘文件。</summary>
public static class VmCheckpointService
{
    private const string SnapshotServiceWql = "SELECT * FROM Msvm_VirtualSystemSnapshotService";
    private const string ManagementServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    private static string? GetCheckpointId(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        const string prefix = "Microsoft:";
        string value = instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? instanceId[prefix.Length..] : instanceId;
        return Guid.TryParse(value, out var id) ? id.ToString("D") : null;
    }

    private static CheckpointNode? ResolveHistoricalNode(IEnumerable<CheckpointNode> nodes, CheckpointNode node)
    {
        if (node.NodeType != CheckpointNodeType.Snapshot || !Guid.TryParse(node.Id, out var id)) return null;
        return nodes.FirstOrDefault(candidate => candidate.NodeType == CheckpointNodeType.Snapshot &&
            Guid.TryParse(candidate.Id, out var candidateId) && candidateId == id);
    }

    // 身份与临时磁盘信息来自本 VM 的当前查询，不能使用调用方提供的路径执行操作。
    private static void RefreshNodeOperationState(CheckpointNode destination, CheckpointNode source)
    {
        destination.InstanceId = source.InstanceId;
        destination.VmId = source.VmId;
        destination.Path = source.Path;
        destination.VirtualSystemType = source.VirtualSystemType;
        destination.VhdPath = source.VhdPath;
        CopyHistoricalDiskCleanupState(destination, source);
    }

    private static void CopyHistoricalDiskCleanupState(CheckpointNode destination, CheckpointNode source)
    {
        destination.IsHistoricalDiskMounted = source.IsHistoricalDiskMounted;
        destination.HistoricalDiskTempPath = source.HistoricalDiskTempPath;
        destination.HistoricalDiskRenamedPath = source.HistoricalDiskRenamedPath;
        destination.HistoricalDiskDriveInstanceId = source.HistoricalDiskDriveInstanceId;
        destination.HistoricalDiskCtrlType = source.HistoricalDiskCtrlType;
        destination.HistoricalDiskCtrlNum = source.HistoricalDiskCtrlNum;
        destination.HistoricalDiskCtrlLoc = source.HistoricalDiskCtrlLoc;
    }

    internal static bool PrepareHistoricalDiskDismount(CheckpointNode node, CheckpointNode currentNode)
    {
        if (currentNode.IsHistoricalDiskMounted)
        {
            RefreshNodeOperationState(node, currentNode);
            return true;
        }

        // 已卸载后，删除临时盘或恢复名称仍可能失败。只恢复同 VM、同快照的 Core 内部清理记录。
        bool sameIdentity = !string.IsNullOrEmpty(node.VmId) && !string.IsNullOrEmpty(node.InstanceId) &&
            string.Equals(node.VmId, currentNode.VmId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.InstanceId, currentNode.InstanceId, StringComparison.OrdinalIgnoreCase);
        return sameIdentity && node.IsHistoricalDiskMounted &&
            !string.IsNullOrEmpty(node.HistoricalDiskTempPath) && !string.IsNullOrEmpty(node.HistoricalDiskRenamedPath);
    }

    internal static void RestoreKnownHistoricalDiskCleanup(
        IEnumerable<CheckpointNode> currentNodes, IEnumerable<CheckpointNode> knownNodes)
    {
        var known = knownNodes.ToList();
        foreach (var current in currentNodes.Where(candidate => candidate.NodeType == CheckpointNodeType.Snapshot))
        {
            if (current.IsHistoricalDiskMounted) continue;
            var pending = known.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
                PrepareHistoricalDiskDismount(candidate, current));
            if (pending != null) CopyHistoricalDiskCleanupState(current, pending);
        }
    }

    /// <summary>检查节点集合中是否存在已打开的历史磁盘挂接。</summary>
    /// <param name="nodes">快照和当前状态节点列表，允许 null 的规则入口按没有节点处理。</param>
    /// <returns>存在打开的历史磁盘挂接时为 true，null 集合为 false。</returns>
    public static bool HasMountedHistoricalDisk(IEnumerable<CheckpointNode>? nodes) =>
        nodes?.Any(node => node.IsHistoricalDiskMounted) == true;

    /// <summary>判断节点是否代表可操作的历史快照。</summary>
    /// <param name="node">目标历史快照或已打开的历史磁盘挂接节点。</param>
    /// <returns>仅非空且 NodeType 为 Snapshot 时返回 true，Origin 和 Current 均不满足。</returns>
    public static bool CanOperateHistoricalNode(CheckpointNode? node) =>
        node?.NodeType == CheckpointNodeType.Snapshot;

    /// <summary>检查是否允许创建快照，要求已启用快照且没有打开的历史磁盘挂接。</summary>
    /// <param name="checkpointsEnabled">当前是否允许使用快照。</param>
    /// <param name="nodes">快照和当前状态节点列表，允许 null 的规则入口按没有节点处理。</param>
    /// <returns>当前输入满足条件时为 true。</returns>
    public static bool CanCreateSnapshot(bool checkpointsEnabled, IEnumerable<CheckpointNode>? nodes) =>
        checkpointsEnabled && !HasMountedHistoricalDisk(nodes);

    /// <summary>判断历史快照是否能执行修改，要求节点有效且没有打开的历史磁盘挂接。</summary>
    /// <param name="node">目标历史快照或已打开的历史磁盘挂接节点。</param>
    /// <param name="nodes">快照和当前状态节点列表，允许 null 的规则入口按没有节点处理。</param>
    /// <returns>当前输入满足条件时为 true。</returns>
    public static bool CanMutateHistoricalNode(CheckpointNode? node, IEnumerable<CheckpointNode>? nodes) =>
        CanOperateHistoricalNode(node) && !HasMountedHistoricalDisk(nodes);

    /// <summary>判断历史快照是否允许向左合并到父快照，仅当父节点为历史快照且目标节点是父节点的唯一直接子节点时允许合并，避免影响初始根节点、分叉节点或当前运行状态直接挂靠的父节点。</summary>
    /// <param name="node">目标历史快照。</param>
    /// <param name="nodes">快照和当前状态节点列表，null 集合按没有节点处理。</param>
    /// <returns>满足合并拓扑与无历史磁盘挂接条件时为 true。</returns>
    public static bool CanMergeHistoricalNode(CheckpointNode? node, IEnumerable<CheckpointNode>? nodes)
    {
        if (!CanMutateHistoricalNode(node, nodes) || nodes == null) return false;
        var nodeList = nodes as IReadOnlyCollection<CheckpointNode> ?? nodes.ToList();
        if (string.IsNullOrEmpty(node!.ParentId)) return false;

        var parent = nodeList.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, node.ParentId, StringComparison.OrdinalIgnoreCase));
        if (parent?.NodeType != CheckpointNodeType.Snapshot) return false;

        var siblings = nodeList.Where(candidate =>
            string.Equals(candidate.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        return siblings.Count == 1 &&
            string.Equals(siblings[0].Id, node.Id, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<(bool Success, string Message)> RunCheckpointMutationAsync(
        Func<Task<List<CheckpointNode>>> getNodes,
        Func<Task<(bool Success, string Message)>> mutate)
    {
        if (HasMountedHistoricalDisk(await getNodes()))
            return (false, Properties.Resources.VmCheckpointService_HintDismountHistoricalDiskFirst);
        return await mutate();
    }

    /// <summary>按稳定身份读取快照树、当前状态及历史磁盘挂接信息。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <returns>完整节点列表，失败时返回原因。</returns>
    public static async Task<ApiResponse<List<CheckpointNode>>> GetCheckpointNodesAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<List<CheckpointNode>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try { return ApiResponse<List<CheckpointNode>>.Ok(await GetCheckpointNodesCoreAsync(vmId)); }
        catch (CheckpointReadException ex) { return ApiResponse<List<CheckpointNode>>.Fail(ex.Result.Error, ex.Result.Code, ex.Result.ErrorSource, ex.Result.Exception, ex.Result.ErrorKind); }
        catch (Exception ex) { return ApiResponse<List<CheckpointNode>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    /// <summary>将指定历史节点的磁盘临时挂接到同一虚拟机。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="targetNode">来自该虚拟机的历史节点，执行前重新核对身份和当前磁盘关系。</param>
    /// <returns>挂接结果，失败可能发生在部分文件和配置变更之后。</returns>
    /// <remarks>
    /// <para>只处理节点 VhdPath 指定的一个磁盘，不挂接该快照的全部磁盘。流程可能将 .avhdx 改名为 _renamed.vhdx，再创建同目录 _historical_disk_tmp.vhdx 差分盘并挂到 SCSI。卸载时移除临时盘并恢复文件名。当前运行磁盘父链上的节点不能这样挂接。</para>
    /// <para>临时盘或改名目标已存在时返回冲突并保留文件，不凭固定文件名认定旧文件属于本次操作。创建结果尚未确认时保留现场，失败清理只删除本次已确认创建的临时盘。</para>
    /// </remarks>
    /// <editentry label="临时挂接历史磁盘" category="快照"/>
    public static Task<ApiResponse> MountHistoricalDiskAsync(Guid vmId, CheckpointNode targetNode) =>
        RunCheckpointOperationAsync(vmId, () => MountHistoricalDiskOperationAsync(vmId, targetNode));

    /// <summary>移除历史磁盘挂接、删除临时盘并恢复历史磁盘名称。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">要关闭挂接的历史节点，执行前重新核对当前实际挂接。</param>
    /// <returns>清理结果，失败时保留可供重试的节点状态。</returns>
    /// <editentry label="结束历史磁盘挂接" category="快照"/>
    public static Task<ApiResponse> DismountHistoricalDiskAsync(Guid vmId, CheckpointNode node) =>
        RunCheckpointOperationAsync(vmId, () => DismountHistoricalDiskOperationAsync(vmId, node));

    /// <summary>修改属于目标虚拟机的快照名称。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">目标历史节点，执行前重新核对归属。</param>
    /// <param name="newName">非空的新显示名称。</param>
    /// <returns>重命名结果。</returns>
    public static Task<ApiResponse> RenameSnapshotAsync(Guid vmId, CheckpointNode node, string newName) =>
        RunCheckpointOperationAsync(vmId, () => RenameSnapshotOperationAsync(vmId, node, newName));

    /// <summary>卸载当前历史磁盘挂接，应用快照，并完成流程要求的重新启动。</summary>
    /// <param name="node">要应用的历史节点。</param>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="nodes">可选的调用方节点列表，用于同步本次清理后的挂接状态。</param>
    /// <returns>整个应用及必要重启结果，Steps 记录 DismountHistoricalDisk（ResourceId 为节点 ID）、Save、Apply、Start 及失败的状态检查 StateBeforeApply/StateBeforeRestart。未执行的步骤不列出，后续失败不会撤销已经完成的步骤。</returns>
    /// <remarks>初始为运行或暂停的虚拟机，在应用后启动。</remarks>
    /// <editentry label="应用快照" category="快照"/>
    public static Task<ApiResponse> ApplySnapshotAsync(CheckpointNode node, Guid vmId, IEnumerable<CheckpointNode>? nodes = null) =>
        RunCheckpointResponseAsync(vmId, () => ApplySnapshotOperationAsync(node, vmId, nodes ?? []));

    private static async Task<ApiResponse> RunCheckpointResponseAsync(Guid vmId, Func<Task<ApiResponse>> operation)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try { return await operation(); }
        catch (CheckpointReadException ex) { return ex.Result; }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.InvalidArgument); }
        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    /// <summary>按虚拟机当前快照策略创建快照。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <returns>快照创建结果。</returns>
    /// <editentry label="创建快照" category="快照"/>
    public static Task<ApiResponse> CreateSnapshotAsync(Guid vmId) =>
        VmSettingsWriteLock.RunAsync(vmId, () =>
            RunCheckpointOperationAsync(vmId, () => CreateSnapshotOperationAsync(vmId)));

    /// <summary>删除目标快照及其全部后代快照。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">要删除的历史节点，执行前核对归属及历史磁盘挂接互锁。</param>
    /// <returns>删除结果。</returns>
    /// <editentry label="删除快照" category="快照"/>
    public static Task<ApiResponse> DeleteSnapshotAsync(Guid vmId, CheckpointNode node) =>
        RunCheckpointOperationAsync(vmId, () => DeleteSnapshotOperationAsync(vmId, node));

    /// <summary>将目标历史快照向左合并到父快照并保留父快照名称与后续节点。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">要合并到父节点的历史快照，执行前核对归属、父子单线关系及历史磁盘挂接互锁。</param>
    /// <returns>合并结果。</returns>
    /// <editentry label="合并快照" category="快照"/>
    public static Task<ApiResponse> MergeSnapshotAsync(Guid vmId, CheckpointNode node) =>
        RunCheckpointOperationAsync(vmId, () => MergeSnapshotOperationAsync(vmId, node));

    internal static async Task<ApiResponse> RunCheckpointOperationAsync(Guid vmId, Func<Task<(bool Success, string Message)>> operation)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            var result = await operation();
            return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Message, kind: ApiErrorKind.OperationFailed);
        }
        catch (CheckpointReadException ex) { return ex.Result; }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.InvalidArgument); }
        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    private sealed class CheckpointReadException(ApiResponse result) : Exception(result.Error)
    { internal ApiResponse Result { get; } = result; }

    private static T RequireData<T>(ApiResponse<T> result)
    {
        if (result.HasData) return result.Data;
        throw new CheckpointReadException(ApiResponse.Fail(result.Success ? "无法读取目标虚拟机或相关配置。" : result.Error,
            result.Code, result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind));
    }

    private static void RequireSuccess<T>(ApiResponse<T> result)
    {
        if (!result.Success) throw new CheckpointReadException(ApiResponse.Fail(result.Error,
            result.Code, result.ErrorSource, result.Exception, result.ErrorKind));
    }

    private static void RequireSuccess(ApiResponse result)
    {
        if (!result.Success) throw new CheckpointReadException(result);
    }

    internal static Task<List<CheckpointNode>> ReadCheckpointStateAsync(Guid vmId) =>
        GetCheckpointNodesCoreAsync(vmId);

    private static async Task<List<CheckpointNode>> GetCheckpointNodesCoreAsync(Guid vmId)
    {
        try
        {
            var vmResponse = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => new { Guid = obj["Name"]?.ToString(), Path = obj.Path.ToString() });

            var vm = RequireData(vmResponse);
            if (string.IsNullOrEmpty(vm.Guid)) throw new InvalidOperationException("虚拟机查询未返回有效身份。");

            var allRawNodes = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}'",
                obj => new CheckpointNode
                {
                    Id = GetCheckpointId(obj.TryGetString("ConfigurationID"))
                        ?? GetCheckpointId(obj["InstanceID"]?.ToString()) ?? "",
                    InstanceId = obj["InstanceID"]?.ToString() ?? "",
                    VmId = vm.Guid!,
                    Name = obj["ElementName"]?.ToString() ?? "",
                    CreatedDate = obj["CreationTime"] != null
                        ? ManagementDateTimeConverter.ToDateTime(obj["CreationTime"].ToString())
                        : DateTime.MinValue,
                    ParentInstanceId = ExtractInstanceId(obj["Parent"]?.ToString()),
                    VirtualSystemType = obj["VirtualSystemType"]?.ToString() ?? "",
                    Path = obj.Path.ToString()
                });

            RequireSuccess(allRawNodes);
            if (!allRawNodes.Data?.Any() ?? true)
                return CreateInitialCheckpointNodes();

            var nodes = allRawNodes.Data!;
            foreach (var node in nodes)
                node.ParentId = nodes.FirstOrDefault(parent => string.Equals(parent.InstanceId,
                    node.ParentInstanceId, StringComparison.OrdinalIgnoreCase))?.Id;

            var snapshots = nodes.Where(n => n.VirtualSystemType.Contains("Snapshot") &&
                !string.IsNullOrEmpty(n.Id)).ToList();
            var realizedNode = nodes.FirstOrDefault(n => n.VirtualSystemType == "Microsoft:Hyper-V:System:Realized");

            DateTime originTime = snapshots.Any()
                ? snapshots.Min(s => s.CreatedDate).AddMinutes(-1)
                : DateTime.Now;

            if (realizedNode != null)
            {
                var sasdResponse = await WmiApi.QueryAsync(
                    "SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31",
                    obj => new
                    {
                        Id = obj["InstanceID"]?.ToString() ?? "",
                        Path = (obj["HostResource"] as string[])?.FirstOrDefault() ?? ""
                    });

                var sasdList = sasdResponse.Data ?? new List<object>()
                    .Select(_ => new { Id = "", Path = "" }).ToList();

                foreach (var snap in snapshots)
                {
                    var sasd = sasdList.FirstOrDefault(d => d.Id.Contains(snap.InstanceId));
                    if (!string.IsNullOrEmpty(sasd?.Path))
                        snap.VhdPath = sasd.Path.Replace("_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
                }

                string currentVhdPath = sasdList.FirstOrDefault(d => d.Id.Contains(realizedNode.InstanceId))?.Path ?? string.Empty;
                if (!string.IsNullOrEmpty(currentVhdPath))
                {
                    string originPath = await Task.Run(() => TraceToOriginPath(currentVhdPath));
                    if (File.Exists(originPath))
                    {
                        var fi = new FileInfo(originPath);
                        var candidate = fi.CreationTime < fi.LastWriteTime ? fi.CreationTime : fi.LastWriteTime;
                        if (snapshots.Any())
                            candidate = candidate < snapshots.Min(s => s.CreatedDate)
                                ? candidate
                                : snapshots.Min(s => s.CreatedDate).AddMinutes(-1);
                        originTime = candidate;
                    }
                }
            }

            foreach (var node in snapshots)
                node.NodeType = CheckpointNodeType.Snapshot;

            var originNode = new CheckpointNode
            {
                Id = CheckpointNode.OriginId,
                Name = Properties.Resources.VmCheckpointService_NodeLabelOrigin,
                NodeType = CheckpointNodeType.Origin,
                CreatedDate = originTime
            };
            var currentNode = new CheckpointNode
            {
                Id = CheckpointNode.CurrentId,
                Name = Properties.Resources.VmCheckpointService_NodeLabelCurrent,
                NodeType = CheckpointNodeType.Current,
                IsCurrent = true,
                CreatedDate = DateTime.Now
            };

            foreach (var s in snapshots)
                if (string.IsNullOrEmpty(s.ParentId)) s.ParentId = CheckpointNode.OriginId;

            if (realizedNode != null)
            {
                currentNode.ParentId = string.IsNullOrEmpty(realizedNode.ParentId)
                    ? CheckpointNode.OriginId : realizedNode.ParentId;
                var anchorNode = snapshots.FirstOrDefault(s => s.Id == realizedNode.ParentId);
                if (anchorNode != null) anchorNode.IsCurrent = true;
                else if (currentNode.ParentId == CheckpointNode.OriginId) originNode.IsCurrent = true;
            }

            var result = new List<CheckpointNode> { originNode };
            result.AddRange(snapshots);
            result.Add(currentNode);

            // 同步等检测完成再返回，历史磁盘挂接标记在节点绑定到 UI 之前落定，避免后台线程跨线程修改状态。
            await DetectAndMarkHistoricalDiskAsync(vmId, result);

            return result;
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            CoreDiagnostics.Report("CheckpointLookupFailed", error: ex);
            throw;
        }
    }

    private static async Task DetectAndMarkHistoricalDiskAsync(Guid vmId, List<CheckpointNode> nodes)
    {
        try
        {
            var vmGuidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            string vmGuid = RequireData(vmGuidResponse);
            var diskPaths = await GetVmScsiDiskPathsAsync(vmGuid);

            string? mountedDiskPath = diskPaths.FirstOrDefault(p =>
                p.Contains("_historical_disk_tmp", StringComparison.OrdinalIgnoreCase));

            CoreDiagnostics.Report("HistoricalDiskProbed", TraceEventType.Verbose, details: $"found={mountedDiskPath != null}");
            if (mountedDiskPath == null) return;

            string renamedParentPath = await GetVhdParentPathAsync(mountedDiskPath);
            string originalAvhdxPath = renamedParentPath.Replace("_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);

            var targetNode = nodes.FirstOrDefault(n =>
                n.NodeType == CheckpointNodeType.Snapshot &&
                !string.IsNullOrEmpty(n.VhdPath) &&
                (string.Equals(n.VhdPath, originalAvhdxPath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n.VhdPath, renamedParentPath, StringComparison.OrdinalIgnoreCase)));

            if (targetNode == null) throw new InvalidOperationException("已挂接历史磁盘，但无法匹配其快照，不能继续修改快照。");

            var slotInfo = await GetDiskSlotInfoAsync(vmGuid, mountedDiskPath);
            var slot = RequireData(slotInfo);

            targetNode.IsHistoricalDiskMounted = true;
            targetNode.HistoricalDiskTempPath = mountedDiskPath;
            targetNode.HistoricalDiskRenamedPath = renamedParentPath;
            targetNode.HistoricalDiskDriveInstanceId = slot.DriveInstanceId;
            targetNode.HistoricalDiskCtrlType = slot.CtrlType;
            targetNode.HistoricalDiskCtrlNum = slot.CtrlNum;
            targetNode.HistoricalDiskCtrlLoc = slot.CtrlLoc;

            CoreDiagnostics.Report("HistoricalDiskMarked", TraceEventType.Verbose);
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            CoreDiagnostics.Report("HistoricalDiskDetectionFailed", error: ex);
            throw;
        }
    }

    private static async Task<(bool Success, string Message)> MountHistoricalDiskOperationAsync(Guid vmId, CheckpointNode targetNode)
    {
        ArgumentNullException.ThrowIfNull(targetNode);
        var currentNode = ResolveHistoricalNode(await ReadCheckpointStateAsync(vmId), targetNode);
        if (currentNode == null)
            return (false, Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid);
        if (PrepareHistoricalDiskDismount(targetNode, currentNode))
            return (false, Properties.Resources.VmCheckpointService_ErrNodeHasHistoricalDisk);
        RefreshNodeOperationState(targetNode, currentNode);

        var existsResult = await CheckAnyHistoricalDiskExistsAsync(vmId);
        CoreDiagnostics.Report("HistoricalDiskExists", TraceEventType.Verbose, details: $"exists={existsResult}");
        if (existsResult)
            return (false, Properties.Resources.VmCheckpointService_ErrHistoricalDiskAlreadyExists);

        if (await IsNodeInCurrentChainAsync(vmId, targetNode.VhdPath))
            return (false, Properties.Resources.VmCheckpointService_ErrParentChainNode);

        if (targetNode.NodeType != CheckpointNodeType.Snapshot)
            return (false, Properties.Resources.VmCheckpointService_ErrOnlySnapshotNode);
        if (string.IsNullOrEmpty(targetNode.VhdPath))
            return (false, Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid);
        if (targetNode.IsHistoricalDiskMounted)
            return (false, Properties.Resources.VmCheckpointService_ErrNodeHasHistoricalDisk);

        string diskDir = Path.GetDirectoryName(targetNode.VhdPath) ?? "";
        string originalAvhdx = targetNode.VhdPath;
        string renamedVhdx = originalAvhdx.Replace(".avhdx", "_renamed.vhdx", StringComparison.OrdinalIgnoreCase);
        string tmpDisk = Path.Combine(diskDir, "_historical_disk_tmp.vhdx");

        if (string.IsNullOrEmpty(diskDir))
            return (false, Properties.Resources.VmCheckpointService_ErrCannotDetermineDiskDir);

        RequireSuccess(CheckHistoricalDiskOutputPaths(tmpDisk, renamedVhdx));

        var (ctrlType, ctrlNum, ctrlLoc) = await FindFreeScsiSlotAsync(vmId);
        if (ctrlNum == -1) return (false, Properties.Resources.VmCheckpointService_ErrNoScsiSlot);

        File.Move(originalAvhdx, renamedVhdx);

        bool createdTemporaryDisk = false;
        try
        {
            var createResult = await CreateDifferencingDiskAsync(tmpDisk, renamedVhdx);
            if (!createResult.Success)
            {
                File.Move(renamedVhdx, originalAvhdx);
                return (false, string.Format(Properties.Resources.VmCheckpointService_ErrCreateDiffDiskFailed, createResult.Message));
            }
            createdTemporaryDisk = true;

            var addResult = await AddVmHardDiskDriveAsync(vmId, ctrlType, ctrlNum, ctrlLoc, tmpDisk);
            if (!addResult.Success)
            {
                RequireHistoricalDiskCleanupSafety(addResult, tmpDisk, renamedVhdx);
                File.Move(renamedVhdx, originalAvhdx);
                return (false, addResult.Error);
            }

            targetNode.IsHistoricalDiskMounted = true;
            targetNode.HistoricalDiskTempPath = tmpDisk;
            targetNode.HistoricalDiskRenamedPath = renamedVhdx;
            targetNode.HistoricalDiskCtrlType = ctrlType;
            targetNode.HistoricalDiskCtrlNum = ctrlNum;
            targetNode.HistoricalDiskCtrlLoc = ctrlLoc;

            return (true, Properties.Resources.VmCheckpointService_MsgHistoricalDiskMounted);
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            if (createdTemporaryDisk && File.Exists(tmpDisk)) File.Delete(tmpDisk);
            if (File.Exists(renamedVhdx) && !File.Exists(originalAvhdx))
                File.Move(renamedVhdx, originalAvhdx);
            return (false, string.Format(Properties.Resources.VmCheckpointService_ErrOperationException, ex.Message));
        }
    }

    internal static ApiResponse CheckHistoricalDiskOutputPaths(string temporaryDisk, string renamedParent)
    {
        foreach (string path in new[] { temporaryDisk, renamedParent })
            if (Path.Exists(path))
                return ApiResponse.Fail($"历史磁盘挂接的输出路径已存在，未修改或删除该路径：{path}",
                    kind: ApiErrorKind.Conflict);
        return ApiResponse.Ok();
    }

    private static async Task<(bool Success, string Message)> DismountHistoricalDiskOperationAsync(Guid vmId, CheckpointNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var currentNode = ResolveHistoricalNode(await ReadCheckpointStateAsync(vmId), node);
        if (currentNode == null)
            return (false, Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid);
        if (!PrepareHistoricalDiskDismount(node, currentNode))
            return (false, Properties.Resources.VmCheckpointService_ErrNoActiveHistoricalDisk);

        try
        {
            // 按临时磁盘路径核对本 VM 当前资源。查询失败与已卸载必须区分，不能把失败当作 0 号槽位。
            var slotResponse = await GetDiskSlotInfoAsync(currentNode.VmId, node.HistoricalDiskTempPath,
                node.HistoricalDiskDriveInstanceId);
            if (!slotResponse.Success)
                return (false, slotResponse.Error);
            if (slotResponse.HasData)
            {
                var slot = slotResponse.Data!;
                node.HistoricalDiskDriveInstanceId = slot.DriveInstanceId;
                node.HistoricalDiskCtrlType = slot.CtrlType;
                node.HistoricalDiskCtrlNum = slot.CtrlNum;
                node.HistoricalDiskCtrlLoc = slot.CtrlLoc;
                var removeResult = await RemoveHistoricalDiskAsync(slot);
                RequireSuccess(removeResult);
            }

            if (File.Exists(node.HistoricalDiskTempPath))
                File.Delete(node.HistoricalDiskTempPath);

            string originalAvhdx = node.HistoricalDiskRenamedPath.Replace(
                "_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(node.HistoricalDiskRenamedPath) && !File.Exists(originalAvhdx))
                File.Move(node.HistoricalDiskRenamedPath, originalAvhdx);

            node.IsHistoricalDiskMounted = false;
            node.HistoricalDiskTempPath = string.Empty;
            node.HistoricalDiskRenamedPath = string.Empty;
            node.HistoricalDiskDriveInstanceId = string.Empty;

            return (true, Properties.Resources.VmCheckpointService_MsgHistoricalDiskDismounted);
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmCheckpointService_ErrDismountHistoricalDiskException, ex.Message));
        }
    }

    private static async Task<ApiResponse> RenameByPathAsync(string snapshotPath, string newName)
    {
        var result = await WmiApi.GetByPathAsync(snapshotPath, obj =>
        {
            obj["ElementName"] = newName;
            return obj.GetText(TextFormat.WmiDtd20);
        });
        RequireData(result);

        return await WmiApi.InvokeAsync(
            ManagementServiceWql,
            "ModifySystemSettings",
            p => p["SystemSettings"] = result.Data);
    }

    private static async Task<(bool Success, string Message)> RenameSnapshotOperationAsync(Guid vmId, CheckpointNode node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        try
        {
            var currentNode = ResolveHistoricalNode(await ReadCheckpointStateAsync(vmId), node);
            if (currentNode == null)
                return (false, Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid);

            var invokeResult = await RenameByPathAsync(currentNode.Path, newName);
            RequireSuccess(invokeResult);
            if (invokeResult.Success) node.Name = newName;
            return invokeResult.Success
                ? (true, Properties.Resources.VmCheckpointService_MsgNameUpdated)
                : (false, string.Format(Properties.Resources.VmCheckpointService_ErrUpdateWmiCode, invokeResult.Error));
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmCheckpointService_ErrRenameException, ex.Message));
        }
    }

    private static async Task<ApiResponse> ApplySnapshotOperationAsync(
        CheckpointNode node, Guid vmId, IEnumerable<CheckpointNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodes);
        if (!CanOperateHistoricalNode(node))
            return ApiResponse.Fail(Properties.Resources.VmCheckpointService_ErrOnlyHistoricalNode, kind: ApiErrorKind.InvalidArgument);

        var currentNodes = await ReadCheckpointStateAsync(vmId);
        var currentNode = ResolveHistoricalNode(currentNodes, node);
        if (currentNode == null)
            return ApiResponse.Fail(Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid, kind: ApiErrorKind.NotFound);
        var callerNodes = nodes.Append(node).ToList();
        RestoreKnownHistoricalDiskCleanup(currentNodes, callerNodes);
        RefreshNodeOperationState(node, currentNode);

        var steps = new List<OperationStepResult>();
        foreach (var mounted in currentNodes.Where(candidate => candidate.IsHistoricalDiskMounted).ToList())
        {
            var result = await RunCheckpointOperationAsync(vmId, async () =>
            {
                var dismounted = await DismountHistoricalDiskOperationAsync(vmId, mounted);
                foreach (var callerNode in callerNodes.Where(candidate =>
                    string.Equals(candidate.Id, mounted.Id, StringComparison.OrdinalIgnoreCase)))
                    RefreshNodeOperationState(callerNode, mounted);
                return dismounted;
            });
            steps.Add(OperationStepResult.From("DismountHistoricalDisk", result) with { ResourceId = mounted.Id });
            if (!result.Success) return result.WithSteps(steps);
        }
        var applied = await RunCheckpointResponseAsync(vmId, () => ApplySnapshotCoreAsync(currentNode, vmId));
        return applied.WithSteps(steps.Concat(applied.Steps));
    }

    internal static async Task<(bool Success, string Message)> DismountHistoricalDisksAndApplyOperationAsync(
        CheckpointNode node, IEnumerable<CheckpointNode> nodes,
        Func<CheckpointNode, Task<(bool Success, string Message)>> dismount,
        Func<Task<(bool Success, string Message)>> apply)
    {
        if (!CanOperateHistoricalNode(node))
            return (false, Properties.Resources.VmCheckpointService_ErrOnlyHistoricalNode);

        foreach (var mounted in nodes.Where(candidate => candidate.IsHistoricalDiskMounted).ToList())
        {
            var result = await dismount(mounted);
            if (!result.Success) return result;
        }
        return await apply();
    }

    private static async Task<ApiResponse> ApplySnapshotCoreAsync(CheckpointNode node, Guid vmId)
    {
        string vmWql = $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";
        var backend = new SnapshotApplyBackend(
            () => WmiApi.QueryFirstAsync(vmWql, obj => Convert.ToUInt16(obj["EnabledState"])),
            action => VmPowerService.ExecuteControlActionAsync(vmId, action),
            () => WmiApi.InvokeAsync(SnapshotServiceWql, "ApplySnapshot", p => p["Snapshot"] = node.Path),
            milliseconds => Task.Delay(milliseconds));
        return await ExecuteApplySnapshotAsync(backend);
    }

    internal sealed record SnapshotApplyBackend(
        Func<Task<ApiResponse<ushort>>> ReadState,
        Func<VmPowerAction, Task<ApiResponse>> Power,
        Func<Task<ApiResponse>> Apply,
        Func<int, Task> Delay);

    internal static async Task<ApiResponse> ExecuteApplySnapshotAsync(SnapshotApplyBackend backend)
    {
        var steps = new List<OperationStepResult>();
        string stage = "StateBeforeApply";
        ApiResponse Finish(ApiResponse result, string name)
        {
            steps.Add(OperationStepResult.From(name, result));
            return result.WithSteps(steps);
        }
        try
        {
            var initial = await backend.ReadState();
            if (!initial.HasData) return Finish(SnapshotStateFailure(initial), stage);
            bool restart = initial.Data is 2 or 32768;
            if (initial.Data is not (3 or 6))
            {
                stage = "Save";
                var saved = await backend.Power(VmPowerAction.Save);
                steps.Add(OperationStepResult.From(stage, saved));
                if (!saved.Success) return saved.WithSteps(steps);
                stage = "StateBeforeApply";
                bool ready = false;
                for (int i = 0; i < 30; i++)
                {
                    await backend.Delay(300);
                    var current = await backend.ReadState();
                    if (!current.HasData) return Finish(SnapshotStateFailure(current), stage);
                    if (current.Data is 3 or 6) { ready = true; break; }
                }
                if (!ready) return Finish(ApiResponse.Fail("虚拟机未进入可应用快照的状态，未执行快照应用。", kind: ApiErrorKind.InvalidState), stage);
            }
            stage = "Apply";
            var applied = await backend.Apply();
            steps.Add(OperationStepResult.From(stage, applied));
            if (!applied.Success || !restart) return applied.WithSteps(steps);
            stage = "StateBeforeRestart";
            await backend.Delay(2000);
            for (int i = 0; i < 10; i++)
            {
                var current = await backend.ReadState();
                if (!current.HasData)
                {
                    var failure = SnapshotStateFailure(current);
                    return Finish(ApiResponse.Fail("快照已应用，但无法读取重新启动所需的状态：" + failure.Error,
                        failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind), stage);
                }
                if (current.Data is 3 or 6)
                {
                    stage = "Start";
                    var started = await backend.Power(VmPowerAction.Start);
                    return Finish(started.Success ? started : ApiResponse.Fail("快照已应用，但重新启动失败：" + started.Error,
                        started.Code, started.ErrorSource, started.Exception, started.ErrorKind), stage);
                }
                await backend.Delay(1000);
            }
            return Finish(ApiResponse.Fail("快照已应用，但虚拟机未进入可重新启动的状态。", kind: ApiErrorKind.InvalidState), stage);
        }
        catch (Exception ex)
        {
            // 写入期间抛异常时不能推定请求没有提交，只读与等待阶段失败不重放此前步骤。
            return Finish(WmiJobWaiter.FromException(ex, stage is "Save" or "Apply" or "Start"), stage);
        }
    }

    private static ApiResponse SnapshotStateFailure(ApiResponse<ushort> result) => ApiResponse.Fail(
        result.Success ? Properties.Resources.VmCheckpointService_ErrVmNotFound : result.Error,
        result.Code, result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind);

    private static Task<(bool Success, string Message)> CreateSnapshotOperationAsync(Guid vmId) =>
        RunCheckpointMutationAsync(
            () => ReadCheckpointStateAsync(vmId),
            () => CreateSnapshotCoreAsync(vmId));

    private static async Task<(bool Success, string Message)> CreateSnapshotCoreAsync(Guid vmId)
    {
        var vmResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            obj => new { Path = obj.Path.ToString(), Guid = obj["Name"]?.ToString() ?? "", Name = obj["ElementName"]?.ToString() ?? "" });

        if (!vmResponse.HasData)
            return (false, Properties.Resources.VmCheckpointService_ErrVmCarrierNotFound);

        var vm = vmResponse.Data!;
        string snapshotName = $"{vm.Name} - ({DateTime.Now:yyyy/M/d - HH:mm:ss})";

        string settingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}";
        ushort policy = RequireData(await WmiApi.QueryFirstAsync(settingsWql,
            obj => obj["UserSnapshotType"] == null ? (ushort?)null : Convert.ToUInt16(obj["UserSnapshotType"])))!.Value;

        try
        {
            using var svcForScope = WmiApi.GetVirtualSystemManagementService();
            using var snapClass = new ManagementClass(svcForScope.Scope,
                new ManagementPath("Msvm_VirtualSystemSettingData"), null);
            using var snapSettings = snapClass.CreateInstance();
            snapSettings["ElementName"] = snapshotName;
            string snapXml = snapSettings.GetText(TextFormat.WmiDtd20);

            var createResult = await CreateWithPolicyAsync(policy,
                async () =>
                {
                    var receipt = await WmiApi.InvokeCreationAsync(SnapshotServiceWql, "CreateSnapshot",
                        "Msvm_VirtualSystemSettingData", input =>
                        {
                            input["AffectedSystem"] = vm.Path;
                            input["SnapshotSettings"] = snapXml;
                            input["SnapshotType"] = (ushort)2;
                        }, resultField: "ResultingSnapshot");
                    return ConfirmSnapshotReceipt(receipt);
                });

            RequireSuccess(createResult);
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmCheckpointService_ErrCreateFailed, ex.Message));
        }

        return (true, Properties.Resources.VmCheckpointService_MsgCreated);
    }

    internal static ApiResponse ConfirmSnapshotReceipt(WmiCreationResult receipt)
    {
        if (receipt.OutcomeUnknown || receipt.Response.Success && receipt.CreatedPaths.Length != 1)
            return WmiJobWaiter.Unknown("未取得唯一且已结束的快照创建回执。 " + receipt.Response.Error,
                receipt.Response.Code, receipt.Response.ErrorSource, receipt.Response.Exception);
        return receipt.Response;
    }

    internal static async Task<ApiResponse> CreateWithPolicyAsync(ushort policy, Func<Task<ApiResponse>> create)
    {
        if (policy == 2) return ApiResponse.Fail("此虚拟机已禁用快照。", kind: ApiErrorKind.InvalidState);
        if (policy is not (3 or 4 or 5)) return ApiResponse.Fail("无法识别当前快照策略。", kind: ApiErrorKind.Unsupported);
        try { return await create(); }
        catch (Exception error) { return WmiJobWaiter.FromException(error, submitted: true); }
    }

    private static async Task<(bool Success, string Message)> DeleteSnapshotOperationAsync(Guid vmId, CheckpointNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsLogicalNode)
            return (false, Properties.Resources.VmCheckpointService_ErrOriginCurrentNoDelete);

        var nodes = await ReadCheckpointStateAsync(vmId);
        var currentNode = ResolveHistoricalNode(nodes, node);
        if (currentNode == null)
            return (false, Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid);
        return await RunCheckpointMutationAsync(() => Task.FromResult(nodes),
            () => DeleteSnapshotCoreAsync(currentNode));
    }

    private static async Task<(bool Success, string Message)> DeleteSnapshotCoreAsync(CheckpointNode node)
    {
        var result = await WmiApi.InvokeAsync(
            SnapshotServiceWql, "DestroySnapshotTree",
            p => p["SnapshotSettingData"] = node.Path);

        RequireSuccess(result);
        if (result.Success)
            return (true, Properties.Resources.VmCheckpointService_MsgDeleted);
        return (false, string.Format(Properties.Resources.VmCheckpointService_ErrDeleteFailed, result.Error));
    }

    private static async Task<(bool Success, string Message)> MergeSnapshotOperationAsync(Guid vmId, CheckpointNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsLogicalNode)
            return (false, Properties.Resources.VmCheckpointService_ErrCannotMerge);

        var nodes = await ReadCheckpointStateAsync(vmId);
        var currentNode = ResolveHistoricalNode(nodes, node);
        if (currentNode == null)
            return (false, Properties.Resources.VmCheckpointService_ErrSnapshotPathInvalid);

        if (!CanMergeHistoricalNode(currentNode, nodes))
            return (false, Properties.Resources.VmCheckpointService_ErrCannotMerge);

        var parentNode = nodes.FirstOrDefault(candidate =>
            candidate.NodeType == CheckpointNodeType.Snapshot &&
            string.Equals(candidate.Id, currentNode.ParentId, StringComparison.OrdinalIgnoreCase));
        if (parentNode == null)
            return (false, Properties.Resources.VmCheckpointService_ErrCannotMerge);

        return await RunCheckpointMutationAsync(() => Task.FromResult(nodes), async () =>
        {
            var result = await MergeWithParentAsync(
                currentNode.Name,
                parentNode.Name,
                name => RenameByPathAsync(currentNode.Path, name),
                () => WmiApi.InvokeAsync(
                    SnapshotServiceWql,
                    "DestroySnapshot",
                    p => p["AffectedSnapshot"] = parentNode.Path));

            RequireSuccess(result);
            node.Name = parentNode.Name;
            return (true, Properties.Resources.VmCheckpointService_MsgMerged);
        });
    }

    internal static async Task<ApiResponse> MergeWithParentAsync(
        string originalName,
        string parentName,
        Func<string, Task<ApiResponse>> renameTarget,
        Func<Task<ApiResponse>> destroyParent)
    {
        bool renamed = !string.Equals(originalName, parentName, StringComparison.Ordinal);
        if (renamed)
        {
            var renameResult = await renameTarget(parentName);
            if (!renameResult.Success)
                return renameResult;
        }

        var destroyResult = await destroyParent();
        if (destroyResult.Success)
            return ApiResponse.Ok();

        if (renamed && destroyResult.ErrorKind is not (ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired))
            await renameTarget(originalName);

        return destroyResult;
    }

    /// <summary>按稳定身份读取虚拟机快照是否启用。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <returns>成功时返回实际设置，配置缺失或查询失败返回错误，不推断为启用。</returns>
    public static async Task<ApiResponse<bool>> GetCheckpointsEnabledAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<bool>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var result = await WmiApi.QueryFirstAsync(
            $"SELECT UserSnapshotType FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => obj["UserSnapshotType"] == null ? (bool?)null : Convert.ToByte(obj["UserSnapshotType"]) != 2);
        return result.HasData ? ApiResponse<bool>.Ok(result.Data.Value)
            : ApiResponse<bool>.Fail(result.Success ? "快照设置不存在或未提供有效值。" : result.Error,
                result.Code, result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind);
    }

    /// <summary>按稳定身份设置虚拟机是否允许使用快照。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="enabled">true 启用快照，false 禁用。</param>
    /// <returns>设置结果，错误包含实际查询或提交原因。</returns>
    public static Task<ApiResponse> SetCheckpointsEnabledAsync(Guid vmId, bool enabled)
    {
        if (vmId == Guid.Empty) return Task.FromResult(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
        return VmSettingsWriteLock.RunAsync(vmId, () => WmiApi.WithObjectAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => obj["UserSnapshotType"] = enabled ? (byte)3 : (byte)2));
    }

    private static async Task<List<string>> GetVmScsiDiskPathsAsync(string vmGuid)
    {
        var response = await WmiApi.QueryAsync(
            $"SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty);

        RequireSuccess(response);
        return (response.Data ?? new List<string>())
            .Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    private sealed record HistoricalDiskSlot(string CtrlType, int CtrlNum, int CtrlLoc,
        string? MediaPath, string DrivePath, string DriveInstanceId);

    private static async Task<ApiResponse<HistoricalDiskSlot>> GetDiskSlotInfoAsync(
        string vmGuid, string diskPath, string knownDriveInstanceId = "")
    {
        var sasdResponse = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_StorageAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => new
            {
                InstanceID = obj["InstanceID"]?.ToString() ?? "",
                Parent = obj["Parent"]?.ToString() ?? "",
                HostResource = (obj["HostResource"] as string[])?.FirstOrDefault() ?? "",
                ResourceType = Convert.ToInt32(obj["ResourceType"] ?? 0),
                Path = obj.Path.Path
            });

        if (!sasdResponse.Success)
            return ApiResponse<HistoricalDiskSlot>.Fail(sasdResponse.Error);

        var sasd = sasdResponse.Data?.FirstOrDefault(d =>
            d.ResourceType == 31 && d.HostResource.Equals(diskPath, StringComparison.OrdinalIgnoreCase));

        string driveInstanceId;
        if (sasd == null)
        {
            if (string.IsNullOrEmpty(knownDriveInstanceId)) return ApiResponse<HistoricalDiskSlot>.Empty();
            if (sasdResponse.Data?.Any(item => string.Equals(
                ExtractInstanceId(item.Parent)?.Replace("\\\\", "\\"), knownDriveInstanceId,
                StringComparison.OrdinalIgnoreCase)) == true)
                return ApiResponse<HistoricalDiskSlot>.Fail("历史磁盘的驱动器已被其他介质占用，未执行移除。");
            driveInstanceId = knownDriveInstanceId;
        }
        else
        {
            driveInstanceId = ExtractInstanceId(sasd.Parent)?.Replace("\\\\", "\\") ?? "";
        }

        if (!driveInstanceId.StartsWith($"Microsoft:{vmGuid}\\", StringComparison.OrdinalIgnoreCase))
            return ApiResponse<HistoricalDiskSlot>.Fail("无法确认历史磁盘的驱动器属于当前虚拟机。");

        string escapedParentId = driveInstanceId.Replace("\\", "\\\\").Replace("'", "\\'");

        var rasdResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ResourceAllocationSettingData WHERE InstanceID = '{escapedParentId}'",
            obj => new
            {
                Location = obj["AddressOnParent"]?.ToString(),
                Parent = obj["Parent"]?.ToString() ?? "",
                ResourceType = Convert.ToInt32(obj["ResourceType"] ?? 0),
                HostResource = (obj["HostResource"] as string[])?.FirstOrDefault() ?? "",
                Path = obj.Path.Path
            });

        if (!rasdResponse.Success) return ApiResponse<HistoricalDiskSlot>.Fail(rasdResponse.Error);
        if (!rasdResponse.HasData)
            return sasd == null ? ApiResponse<HistoricalDiskSlot>.Empty()
                : ApiResponse<HistoricalDiskSlot>.Fail("无法确认历史磁盘挂接的驱动器。");
        if (rasdResponse.Data.ResourceType != 17)
            return ApiResponse<HistoricalDiskSlot>.Fail("历史磁盘的挂接目标不是硬盘驱动器。");
        if (!string.IsNullOrWhiteSpace(rasdResponse.Data.HostResource))
            return ApiResponse<HistoricalDiskSlot>.Fail("历史磁盘的驱动器已直接引用其他介质，未执行移除。");

        var ctrlListResp = await WmiApi.QueryAsync(
            $"SELECT InstanceID FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 6 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => obj["InstanceID"]?.ToString() ?? "");
        if (!ctrlListResp.Success) return ApiResponse<HistoricalDiskSlot>.Fail(ctrlListResp.Error);

        var slot = ResolveHistoricalDiskSlot(rasdResponse.Data.Parent, rasdResponse.Data.Location,
            ctrlListResp.Data ?? []);
        if (slot == null)
            return ApiResponse<HistoricalDiskSlot>.Fail("无法确认历史磁盘的控制器及槽位，未执行移除。");
        return ApiResponse<HistoricalDiskSlot>.Ok(new("SCSI", slot.Value.Controller, slot.Value.Location,
            sasd?.Path, rasdResponse.Data.Path, driveInstanceId));
    }

    internal static (int Controller, int Location)? ResolveHistoricalDiskSlot(
        string parentPath, string? location, IReadOnlyList<string> controllerIds)
    {
        if (!int.TryParse(location, out var slot) || slot < 0) return null;
        var controllerId = ExtractInstanceId(parentPath)?.Replace("\\\\", "\\");
        if (string.IsNullOrEmpty(controllerId)) return null;
        for (int index = 0; index < controllerIds.Count; index++)
            if (string.Equals(controllerIds[index], controllerId, StringComparison.OrdinalIgnoreCase))
                return (index, slot);
        return null;
    }

    private static async Task<ApiResponse> RemoveHistoricalDiskAsync(HistoricalDiskSlot slot)
    {
        if (!string.IsNullOrEmpty(slot.MediaPath))
        {
            var mediaResult = await WmiApi.InvokeAsync(ManagementServiceWql, "RemoveResourceSettings",
                parameters => parameters["ResourceSettings"] = new[] { slot.MediaPath });
            if (!mediaResult.Success) return mediaResult;
        }
        return await WmiApi.InvokeAsync(ManagementServiceWql, "RemoveResourceSettings",
            parameters => parameters["ResourceSettings"] = new[] { slot.DrivePath });
    }

    private static async Task<ApiResponse> AddVmHardDiskDriveAsync(
        Guid vmId, string ctrlType, int ctrlNum, int ctrlLoc, string vhdPath)
    {
        var result = await VmStorageService.AddDriveAsync(
            vmId, new VmStorageAddRequest { ControllerType = ctrlType, ControllerNumber = ctrlNum,
                Location = ctrlLoc, DriveType = "HardDisk", PathOrNumber = vhdPath });

        return result.Success
            ? ApiResponse.Ok()
            : ApiResponse.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
    }

    private static async Task<(string, int, int)> FindFreeScsiSlotAsync(Guid vmId)
    {
        try
        {
            using var vm = WmiApi.GetVmComputerSystem(vmId);
            if (vm == null) return ("SCSI", -1, -1);
            string vmGuid = vm["Name"]?.ToString() ?? "";

            var driveResponse = await WmiApi.QueryAsync(
                $"SELECT Parent, AddressOnParent FROM Msvm_ResourceAllocationSettingData WHERE (ResourceType = 16 OR ResourceType = 17) AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
                obj => (
                    ParentId: ExtractInstanceId(obj["Parent"]?.ToString())?.Replace("\\\\", "\\") ?? "",
                    Loc: int.TryParse(obj["AddressOnParent"]?.ToString(), out int l) ? l : -1));
            RequireSuccess(driveResponse);
            var drives = driveResponse.Data ?? new();

            var ctrlResponse = await WmiApi.QueryAsync(
                $"SELECT InstanceID FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 6 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
                obj => obj["InstanceID"]?.ToString() ?? "");
            RequireSuccess(ctrlResponse);
            var controllers = ctrlResponse.Data ?? new List<string>();

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int loc = 0; loc < 64; loc++)
                {
                    bool used = drives.Any(d => d.Loc == loc &&
                        string.Equals(d.ParentId, controllers[c], StringComparison.OrdinalIgnoreCase));
                    if (!used) return ("SCSI", c, loc);
                }
            }

            return ("SCSI", -1, -1);
        }
        catch { throw; }
    }

    private static async Task<bool> CheckAnyHistoricalDiskExistsAsync(Guid vmId)
    {
        try
        {
            var guidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            RequireData(guidResponse);

            var paths = await GetVmScsiDiskPathsAsync(guidResponse.Data!);
            return paths.Any(p => p.Contains("_historical_disk_tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch { throw; }
    }

    private static async Task<bool> IsNodeInCurrentChainAsync(Guid vmId, string targetVhdPath)
    {
        var paths = await GetVmScsiDiskPathsAsync(vmId.ToString("D"));
        foreach (string attached in paths.Where(path => !path.Contains("_historical_disk_tmp", StringComparison.OrdinalIgnoreCase)))
        {
            string current = attached;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(current))
            {
                if (!visited.Add(current) || visited.Count > 256)
                    throw new InvalidOperationException("虚拟磁盘父链无效，不能确认历史磁盘是否仍在使用。");
                string normalized = current.Replace("_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(normalized, targetVhdPath, StringComparison.OrdinalIgnoreCase)) return true;
                current = await GetVhdParentPathAsync(current);
            }
        }
        return false;
    }

    private static async Task<(bool Success, string Message)> CreateDifferencingDiskAsync(
        string newPath, string parentPath)
    {
        try
        {
            using var svcForScope = WmiApi.GetVirtualSystemManagementService();
            using var settingClass = new ManagementClass(
                svcForScope.Scope,
                new ManagementPath("Msvm_VirtualHardDiskSettingData"),
                null);
            using var setting = settingClass.CreateInstance();
            setting["Type"] = (ushort)4;
            setting["Format"] = (ushort)3;
            setting["Path"] = newPath;
            setting["ParentPath"] = parentPath;
            setting["BlockSize"] = 0u;
            setting["LogicalSectorSize"] = 0u;
            setting["PhysicalSectorSize"] = 0u;
            setting["MaxInternalSize"] = 0ul;
            string xml = setting.GetText(TextFormat.CimDtd20);

            var result = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_ImageManagementService",
                "CreateVirtualHardDisk",
                p => p["VirtualDiskSettingData"] = xml,
                WmiScope.HyperV);

            RequireHistoricalDiskCleanupSafety(result, newPath, parentPath);
            return result.Success
                ? (true, string.Empty)
                : (false, result.Error);
        }
        catch (CheckpointReadException) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void RequireHistoricalDiskCleanupSafety(ApiResponse response, string diskPath, string parentPath)
    {
        if (response.ErrorKind is not (ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired)) return;
        RequireSuccess(ApiResponse.Fail(response.Error + Environment.NewLine +
            $"磁盘操作仍需核对，已保留临时磁盘及父盘，未删除或恢复文件名。临时磁盘：{diskPath}，父盘：{parentPath}",
            response.Code, response.ErrorSource, response.Exception, response.ErrorKind));
    }

    private static async Task<string> GetVhdParentPathAsync(string vhdPath)
    {
        var result = await HostVirtualDiskMetadata.ReadParentPathAsync(vhdPath).ConfigureAwait(false);
        RequireSuccess(result);
        return result.Data ?? string.Empty;
    }

    private static string TraceToOriginPath(string childPath)
    {
        string currentPath = childPath;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 256 && visited.Add(currentPath); i++)
        {
            var result = HostVirtualDiskMetadata.ReadParentPath(currentPath);
            if (!result.Success) return string.Empty;
            if (!result.HasData) return currentPath;
            currentPath = result.Data;
        }
        return string.Empty;
    }

    internal static List<CheckpointNode> CreateInitialCheckpointNodes()
    {
        return new List<CheckpointNode>
        {
            new() { Id = CheckpointNode.OriginId, Name = Properties.Resources.VmCheckpointService_NodeLabelOrigin, NodeType = CheckpointNodeType.Origin, IsCurrent = true, CreatedDate = DateTime.Now.AddMinutes(-1) },
            new() { Id = CheckpointNode.CurrentId, Name = Properties.Resources.VmCheckpointService_NodeLabelCurrent, NodeType = CheckpointNodeType.Current, ParentId = CheckpointNode.OriginId, IsCurrent = true, CreatedDate = DateTime.Now }
        };
    }

    private static string? ExtractInstanceId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var match = Regex.Match(path, "InstanceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
