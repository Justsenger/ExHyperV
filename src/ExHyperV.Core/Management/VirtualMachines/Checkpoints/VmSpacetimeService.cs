using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>管理检查点、回退及历史磁盘临时挂接；相关操作可能修改配置和磁盘文件。</summary>
public static class VmSpacetimeService
{
    private const string SnapshotServiceWql = "SELECT * FROM Msvm_VirtualSystemSnapshotService";
    private const string ManagementServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private static string GetSafeId(string id) => id.Replace(":", "_");

    private static string? GetCheckpointId(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        const string prefix = "Microsoft:";
        string value = instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? instanceId[prefix.Length..] : instanceId;
        return Guid.TryParse(value, out var id) ? id.ToString("D") : null;
    }

    private static SpacetimeNode? ResolveHistoricalNode(IEnumerable<SpacetimeNode> nodes, SpacetimeNode node)
    {
        if (node.NodeType != SpacetimeNodeType.Snapshot || !Guid.TryParse(node.Id, out var id)) return null;
        return nodes.FirstOrDefault(candidate => candidate.NodeType == SpacetimeNodeType.Snapshot &&
            Guid.TryParse(candidate.Id, out var candidateId) && candidateId == id);
    }

    // 身份与临时磁盘信息来自本 VM 的当前查询，不能使用调用方提供的路径执行操作。
    private static void RefreshNodeOperationState(SpacetimeNode destination, SpacetimeNode source)
    {
        destination.InstanceId = source.InstanceId;
        destination.VmId = source.VmId;
        destination.Path = source.Path;
        destination.VirtualSystemType = source.VirtualSystemType;
        destination.VhdPath = source.VhdPath;
        CopyWormholeCleanupState(destination, source);
    }

    private static void CopyWormholeCleanupState(SpacetimeNode destination, SpacetimeNode source)
    {
        destination.IsWormhole = source.IsWormhole;
        destination.WormholeTmpDiskPath = source.WormholeTmpDiskPath;
        destination.WormholeRenamedPath = source.WormholeRenamedPath;
        destination.WormholeDriveInstanceId = source.WormholeDriveInstanceId;
        destination.WormholeCtrlType = source.WormholeCtrlType;
        destination.WormholeCtrlNum = source.WormholeCtrlNum;
        destination.WormholeCtrlLoc = source.WormholeCtrlLoc;
    }

    internal static bool PrepareWormholeClose(SpacetimeNode node, SpacetimeNode currentNode)
    {
        if (currentNode.IsWormhole)
        {
            RefreshNodeOperationState(node, currentNode);
            return true;
        }

        // 已卸载后，删除临时盘或恢复名称仍可能失败。只恢复同 VM、同检查点的 Core 内部清理记录。
        bool sameIdentity = !string.IsNullOrEmpty(node.VmId) && !string.IsNullOrEmpty(node.InstanceId) &&
            string.Equals(node.VmId, currentNode.VmId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.InstanceId, currentNode.InstanceId, StringComparison.OrdinalIgnoreCase);
        return sameIdentity && node.IsWormhole &&
            !string.IsNullOrEmpty(node.WormholeTmpDiskPath) && !string.IsNullOrEmpty(node.WormholeRenamedPath);
    }

    internal static void RestoreKnownWormholeCleanup(
        IEnumerable<SpacetimeNode> currentNodes, IEnumerable<SpacetimeNode> knownNodes)
    {
        var known = knownNodes.ToList();
        foreach (var current in currentNodes.Where(candidate => candidate.NodeType == SpacetimeNodeType.Snapshot))
        {
            if (current.IsWormhole) continue;
            var pending = known.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, current.Id, StringComparison.OrdinalIgnoreCase) &&
                PrepareWormholeClose(candidate, current));
            if (pending != null) CopyWormholeCleanupState(current, pending);
        }
    }

    /// <summary>检查节点集合中是否存在已打开的历史磁盘挂接。</summary>
    /// <param name="nodes">检查点和当前状态节点的快照；允许 null 的规则入口按没有节点处理。</param>
    /// <returns>存在打开的虫洞时为 true；null 集合为 false。</returns>
    public static bool HasOpenWormhole(IEnumerable<SpacetimeNode>? nodes) =>
        nodes?.Any(node => node.IsWormhole) == true;

    /// <summary>判断节点是否代表可操作的历史检查点。</summary>
    /// <param name="node">目标历史检查点或已打开的历史磁盘挂接节点。</param>
    /// <returns>仅非空且 NodeType 为 Snapshot 时返回 true；Genesis 和 Current 均不满足。</returns>
    public static bool CanOperateHistoricalNode(SpacetimeNode? node) =>
        node?.NodeType == SpacetimeNodeType.Snapshot;

    /// <summary>检查是否允许创建检查点，要求已启用检查点且没有打开的虫洞。</summary>
    /// <param name="checkpointsEnabled">当前是否允许使用检查点。</param>
    /// <param name="nodes">检查点和当前状态节点的快照；允许 null 的规则入口按没有节点处理。</param>
    /// <returns>当前输入满足条件时为 true。</returns>
    public static bool CanCreateMoment(bool checkpointsEnabled, IEnumerable<SpacetimeNode>? nodes) =>
        checkpointsEnabled && !HasOpenWormhole(nodes);

    /// <summary>判断历史检查点是否能执行修改，要求节点有效且没有打开的虫洞。</summary>
    /// <param name="node">目标历史检查点或已打开的历史磁盘挂接节点。</param>
    /// <param name="nodes">检查点和当前状态节点的快照；允许 null 的规则入口按没有节点处理。</param>
    /// <returns>当前输入满足条件时为 true。</returns>
    public static bool CanMutateHistoricalNode(SpacetimeNode? node, IEnumerable<SpacetimeNode>? nodes) =>
        CanOperateHistoricalNode(node) && !HasOpenWormhole(nodes);

    internal static async Task<(bool Success, string Message)> RunCheckpointMutationAsync(
        Func<Task<List<SpacetimeNode>>> getNodes,
        Func<Task<(bool Success, string Message)>> mutate)
    {
        if (HasOpenWormhole(await getNodes()))
            return (false, Properties.Resources.VmSpacetimeSettings_HintCloseWormholeFirst);
        return await mutate();
    }

    /// <summary>按稳定身份读取检查点树、当前状态及历史磁盘挂接信息。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="encodeThumbnail">可选缩略图编码器；提供时可能更新缩略图缓存。</param>
    /// <returns>完整节点列表；失败时返回原因。</returns>
    public static async Task<ApiResponse<List<SpacetimeNode>>> GetSpacetimeNodesAsync(Guid vmId,
        Func<VmScreenshotImage, byte[]>? encodeThumbnail = null)
    {
        if (vmId == Guid.Empty) return ApiResponse<List<SpacetimeNode>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try { return ApiResponse<List<SpacetimeNode>>.Ok(await GetSpacetimeNodesCoreAsync(vmId, true, encodeThumbnail)); }
        catch (SpacetimeReadException ex) { return ApiResponse<List<SpacetimeNode>>.Fail(ex.Result.Error, ex.Result.Code, ex.Result.ErrorSource, ex.Result.Exception, ex.Result.ErrorKind); }
        catch (Exception ex) { return ApiResponse<List<SpacetimeNode>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    /// <summary>将指定历史节点的磁盘临时挂接到同一虚拟机。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="targetNode">来自该虚拟机的历史节点；执行前重新核对身份和当前磁盘关系。</param>
    /// <returns>挂接结果；失败可能发生在部分文件和配置变更之后。</returns>
    /// <remarks>
    /// <para>只处理节点 VhdPath 指定的一个磁盘，不挂接该检查点的全部磁盘。流程可能将 .avhdx 改名为 _renamed.vhdx，再创建同目录 _wormhole_tmp.vhdx 差分盘并挂到 SCSI；关闭时移除临时盘并恢复文件名。当前运行磁盘父链上的节点不能这样挂接。</para>
    /// <para>临时盘或改名目标已存在时返回冲突并保留文件，不凭固定文件名认定旧文件属于本次操作。创建结果尚未确认时保留现场；失败清理只删除本次已确认创建的临时盘。</para>
    /// </remarks>
    /// <editentry label="临时挂接历史磁盘" category="检查点"/>
    public static Task<ApiResponse> OpenWormholeAsync(Guid vmId, SpacetimeNode targetNode) =>
        RunSpacetimeOperationAsync(vmId, () => OpenWormholeOperationAsync(vmId, targetNode));

    /// <summary>移除历史磁盘挂接、删除临时盘并恢复历史磁盘名称。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">要关闭挂接的历史节点；执行前重新核对当前实际挂接。</param>
    /// <returns>清理结果；失败时保留可供重试的节点状态。</returns>
    /// <editentry label="结束历史磁盘挂接" category="检查点"/>
    public static Task<ApiResponse> CloseWormholeAsync(Guid vmId, SpacetimeNode node) =>
        RunSpacetimeOperationAsync(vmId, () => CloseWormholeOperationAsync(vmId, node));

    /// <summary>修改属于目标虚拟机的检查点名称。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">目标历史节点；执行前重新核对归属。</param>
    /// <param name="newName">非空的新显示名称。</param>
    /// <returns>重命名结果。</returns>
    public static Task<ApiResponse> RenameSnapshotAsync(Guid vmId, SpacetimeNode node, string newName) =>
        RunSpacetimeOperationAsync(vmId, () => RenameSnapshotOperationAsync(vmId, node, newName));

    /// <summary>关闭当前历史磁盘挂接，应用检查点，并完成流程要求的重新启动。</summary>
    /// <param name="node">要应用的历史节点。</param>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="nodes">可选的调用方节点快照，用于同步本次清理后的挂接状态。</param>
    /// <returns>整个应用及必要重启结果；Steps 记录 CloseWormhole（ResourceId 为节点 ID）、Save、Apply、Start 及失败的状态检查 StateBeforeApply/StateBeforeRestart。未执行的步骤不列出。后续失败不会撤销已经完成的步骤。</returns>
    /// <remarks>初始为运行或暂停的虚拟机，在应用后启动。</remarks>
    /// <editentry label="恢复检查点" category="检查点"/>
    public static Task<ApiResponse> TeleportAsync(SpacetimeNode node, Guid vmId, IEnumerable<SpacetimeNode>? nodes = null) =>
        RunSpacetimeResponseAsync(vmId, () => TeleportOperationAsync(node, vmId, nodes ?? []));

    private static async Task<ApiResponse> RunSpacetimeResponseAsync(Guid vmId, Func<Task<ApiResponse>> operation)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try { return await operation(); }
        catch (SpacetimeReadException ex) { return ex.Result; }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.InvalidArgument); }
        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    /// <summary>创建检查点并按需缓存缩略图；离线目标采用标准检查点。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="mode">Continuous 请求标准检查点；Still 仅在 Running 状态请求生产检查点，其他状态请求标准检查点。生产请求失败时保留失败结果。</param>
    /// <param name="externalThumb">可选外部截图；未提供时尝试捕获当前截图。</param>
    /// <param name="encodeThumbnail">可选缩略图编码器。</param>
    /// <returns>检查点创建结果；截图缓存缺失不否定已完成的检查点创建。临时切换检查点策略后恢复原值；OutcomeUnknown 时保留现场及原策略值，须核对原作业后再恢复。</returns>
    /// <editentry label="创建检查点" category="检查点"/>
    public static Task<ApiResponse> CaptureMomentAsync(Guid vmId, SpacetimeMode mode,
        VmScreenshotImage? externalThumb = null, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null) =>
        VmSettingsWriteLock.RunAsync(vmId, () =>
            RunSpacetimeOperationAsync(vmId, () => CaptureMomentOperationAsync(vmId, mode, externalThumb, encodeThumbnail)));

    /// <summary>删除目标检查点及其全部后代检查点。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">要删除的历史节点；执行前核对归属及历史磁盘挂接互锁。</param>
    /// <returns>删除结果。</returns>
    /// <editentry label="删除检查点" category="检查点"/>
    public static Task<ApiResponse> AnnihilateAsync(Guid vmId, SpacetimeNode node) =>
        RunSpacetimeOperationAsync(vmId, () => AnnihilateOperationAsync(vmId, node));

    /// <summary>删除并合并单个历史检查点，保留后继检查点关系。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="node">要合并的历史节点；执行前核对归属及历史磁盘挂接互锁。</param>
    /// <returns>合并结果。</returns>
    /// <editentry label="合并检查点" category="检查点"/>
    public static Task<ApiResponse> ConvergeAsync(Guid vmId, SpacetimeNode node) =>
        RunSpacetimeOperationAsync(vmId, () => ConvergeOperationAsync(vmId, node));

    internal static async Task<ApiResponse> RunSpacetimeOperationAsync(Guid vmId, Func<Task<(bool Success, string Message)>> operation)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            var result = await operation();
            return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Message, kind: ApiErrorKind.OperationFailed);
        }
        catch (SpacetimeReadException ex) { return ex.Result; }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.InvalidArgument); }
        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    private sealed class SpacetimeReadException(ApiResponse result) : Exception(result.Error)
    { internal ApiResponse Result { get; } = result; }

    private static T RequireData<T>(ApiResponse<T> result)
    {
        if (result.HasData) return result.Data;
        throw new SpacetimeReadException(ApiResponse.Fail(result.Success ? "无法读取目标虚拟机或相关配置。" : result.Error,
            result.Code, result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind));
    }

    private static void RequireSuccess<T>(ApiResponse<T> result)
    {
        if (!result.Success) throw new SpacetimeReadException(ApiResponse.Fail(result.Error,
            result.Code, result.ErrorSource, result.Exception, result.ErrorKind));
    }

    private static void RequireSuccess(ApiResponse result)
    {
        if (!result.Success) throw new SpacetimeReadException(result);
    }

    // Operation guards reuse node-level wormhole detection without loading,
    // capturing or creating the thumbnails used by the timeline view.
    internal static Task<List<SpacetimeNode>> ReadSpacetimeStateAsync(Guid vmId) =>
        GetSpacetimeNodesCoreAsync(vmId, includeThumbnails: false, encodeThumbnail: null);

    private static async Task<List<SpacetimeNode>> GetSpacetimeNodesCoreAsync(
        Guid vmId, bool includeThumbnails, Func<VmScreenshotImage, byte[]>? encodeThumbnail)
    {
        try
        {
            var vmResponse = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => new { Guid = obj["Name"]?.ToString(), Path = obj.Path.ToString() });

            var vm = RequireData(vmResponse);
            if (string.IsNullOrEmpty(vm.Guid)) throw new InvalidOperationException("虚拟机查询未返回有效身份。");

            var configResponse = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["ConfigurationDataRoot"]?.ToString());

            string configRoot = RequireData(configResponse);
            if (string.IsNullOrWhiteSpace(configRoot)) throw new InvalidOperationException("虚拟机配置根目录为空。");
            string snapshotDir = Path.Combine(configRoot, "Snapshots");

            var allRawNodes = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}'",
                obj => new SpacetimeNode
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
                return includeThumbnails
                    ? await CreateInitialSpacetimeAsync(vmId, snapshotDir, encodeThumbnail)
                    : CreateInitialSpacetimeNodes(null);

            var nodes = allRawNodes.Data!;
            foreach (var node in nodes)
                node.ParentId = nodes.FirstOrDefault(parent => string.Equals(parent.InstanceId,
                    node.ParentInstanceId, StringComparison.OrdinalIgnoreCase))?.Id;

            var snapshots = nodes.Where(n => n.VirtualSystemType.Contains("Snapshot") &&
                !string.IsNullOrEmpty(n.Id)).ToList();
            var realizedNode = nodes.FirstOrDefault(n => n.VirtualSystemType == "Microsoft:Hyper-V:System:Realized");

            DateTime genesisTime = snapshots.Any()
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
                    string genesisPath = await Task.Run(() => TraceToGenesisPath(currentVhdPath));
                    if (File.Exists(genesisPath))
                    {
                        var fi = new FileInfo(genesisPath);
                        var candidate = fi.CreationTime < fi.LastWriteTime ? fi.CreationTime : fi.LastWriteTime;
                        if (snapshots.Any())
                            candidate = candidate < snapshots.Min(s => s.CreatedDate)
                                ? candidate
                                : snapshots.Min(s => s.CreatedDate).AddMinutes(-1);
                        genesisTime = candidate;
                    }
                }
            }

            foreach (var node in snapshots)
            {
                node.NodeType = SpacetimeNodeType.Snapshot;
                if (includeThumbnails)
                    node.Thumbnail = LoadThumbnailFromDisk(snapshotDir, node.InstanceId);
            }

            VmScreenshotImage? genesisThumbnail = null;
            if (includeThumbnails)
            {
                genesisThumbnail = LoadThumbnailFromDisk(snapshotDir, SpacetimeNode.GenesisId);
                if (genesisThumbnail == null)
                {
                    genesisThumbnail = await VmScreenshotSource.CaptureAsync(vmId, 280, 160);
                    if (genesisThumbnail != null)
                        await SaveThumbnailToDisk(genesisThumbnail, snapshotDir, SpacetimeNode.GenesisId, encodeThumbnail);
                }
            }

            var genesisNode = new SpacetimeNode
            {
                Id = SpacetimeNode.GenesisId,
                Name = Properties.Resources.VmSpacetimeService_NodeLabelOrigin,
                NodeType = SpacetimeNodeType.Genesis,
                CreatedDate = genesisTime,
                Thumbnail = genesisThumbnail
            };
            var currentNode = new SpacetimeNode
            {
                Id = SpacetimeNode.CurrentId,
                Name = Properties.Resources.VmSpacetimeService_NodeLabelCurrent,
                NodeType = SpacetimeNodeType.Current,
                IsCurrent = true,
                CreatedDate = DateTime.Now,
                Thumbnail = null
            };

            foreach (var s in snapshots)
                if (string.IsNullOrEmpty(s.ParentId)) s.ParentId = SpacetimeNode.GenesisId;

            if (realizedNode != null)
            {
                currentNode.ParentId = string.IsNullOrEmpty(realizedNode.ParentId)
                    ? SpacetimeNode.GenesisId : realizedNode.ParentId;
                var anchorNode = snapshots.FirstOrDefault(s => s.Id == realizedNode.ParentId);
                if (anchorNode != null) anchorNode.IsCurrent = true;
                else if (currentNode.ParentId == SpacetimeNode.GenesisId) genesisNode.IsCurrent = true;
            }

            var result = new List<SpacetimeNode> { genesisNode };
            result.AddRange(snapshots);
            result.Add(currentNode);

            if (includeThumbnails)
                currentNode.Thumbnail = await VmScreenshotSource.CaptureAsync(vmId, 280, 160);
            // 同步等检测完成再返回：虫洞标记在节点绑定到 UI 之前落定，避免后台线程改已绑定的 IsWormhole(跨线程更新)
            await DetectAndMarkWormholeAsync(vmId, result);

            return result;
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_ErrLookupFailed, ex.Message));
            throw;
        }
    }

    // 虫洞检测

    private static async Task DetectAndMarkWormholeAsync(Guid vmId, List<SpacetimeNode> nodes)
    {
        try
        {
            var vmGuidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            string vmGuid = RequireData(vmGuidResponse);
            var diskPaths = await GetVmScsiDiskPathsAsync(vmGuid);

            string? wormholePath = diskPaths.FirstOrDefault(p =>
                p.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase));

            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_LogWormholeDisk, wormholePath ?? "未找到"));
            if (wormholePath == null) return;

            string renamedParentPath = await GetVhdParentPathAsync(wormholePath);
            string originalAvhdxPath = renamedParentPath.Replace("_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);

            var targetNode = nodes.FirstOrDefault(n =>
                n.NodeType == SpacetimeNodeType.Snapshot &&
                !string.IsNullOrEmpty(n.VhdPath) &&
                (string.Equals(n.VhdPath, originalAvhdxPath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n.VhdPath, renamedParentPath, StringComparison.OrdinalIgnoreCase)));

            if (targetNode == null) throw new InvalidOperationException("已挂接历史磁盘，但无法匹配其检查点，不能继续修改检查点。");

            var slotInfo = await GetDiskSlotInfoAsync(vmGuid, wormholePath);
            var slot = RequireData(slotInfo);

            targetNode.IsWormhole = true;
            targetNode.WormholeTmpDiskPath = wormholePath;
            targetNode.WormholeRenamedPath = renamedParentPath;
            targetNode.WormholeDriveInstanceId = slot.DriveInstanceId;
            targetNode.WormholeCtrlType = slot.CtrlType;
            targetNode.WormholeCtrlNum = slot.CtrlNum;
            targetNode.WormholeCtrlLoc = slot.CtrlLoc;

            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_LogWormholeMarkedOk, targetNode.Name));
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_LogWormholeDetectFailed, ex.Message));
            throw;
        }
    }

    // 虫洞开启

    /// <summary>将历史磁盘通过临时差分磁盘挂接到虚拟机，以访问检查点中的内容。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="targetNode">要临时挂接历史磁盘的检查点节点；执行会更新其挂接状态及路径信息。</param>
    /// <returns>挂接结果；过程中会重命名磁盘、创建文件并修改 targetNode，失败时可保留部分已完成的修改。</returns>
    private static async Task<(bool Success, string Message)> OpenWormholeOperationAsync(Guid vmId, SpacetimeNode targetNode)
    {
        ArgumentNullException.ThrowIfNull(targetNode);
        var currentNode = ResolveHistoricalNode(await ReadSpacetimeStateAsync(vmId), targetNode);
        if (currentNode == null)
            return (false, Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid);
        if (PrepareWormholeClose(targetNode, currentNode))
            return (false, Properties.Resources.VmSpacetimeService_ErrNodeHasWormhole);
        RefreshNodeOperationState(targetNode, currentNode);

        var existsResult = await CheckAnyWormholeExistsAsync(vmId);
        System.Diagnostics.Debug.WriteLine($"[OpenWormhole] CheckAnyWormholeExists={existsResult}");
        if (existsResult)
            return (false, Properties.Resources.VmSpacetimeService_ErrWormholeAlreadyExists);

        if (await IsNodeInCurrentChainAsync(vmId, targetNode.VhdPath))
            return (false, Properties.Resources.VmSpacetimeService_ErrParentChainNode);

        if (targetNode.NodeType != SpacetimeNodeType.Snapshot)
            return (false, Properties.Resources.VmSpacetimeService_ErrOnlySnapshotNode);
        if (string.IsNullOrEmpty(targetNode.VhdPath))
            return (false, Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid);
        if (targetNode.IsWormhole)
            return (false, Properties.Resources.VmSpacetimeService_ErrNodeHasWormhole);

        string diskDir = Path.GetDirectoryName(targetNode.VhdPath) ?? "";
        string originalAvhdx = targetNode.VhdPath;
        string renamedVhdx = originalAvhdx.Replace(".avhdx", "_renamed.vhdx", StringComparison.OrdinalIgnoreCase);
        string tmpDisk = Path.Combine(diskDir, "_wormhole_tmp.vhdx");

        if (string.IsNullOrEmpty(diskDir))
            return (false, Properties.Resources.VmSpacetimeService_ErrCannotDetermineDiskDir);

        RequireSuccess(CheckWormholeOutputPaths(tmpDisk, renamedVhdx));

        var (ctrlType, ctrlNum, ctrlLoc) = await FindFreeScsiSlotAsync(vmId);
        if (ctrlNum == -1) return (false, Properties.Resources.VmSpacetimeService_ErrNoScsiSlot);

        File.Move(originalAvhdx, renamedVhdx);

        bool createdTemporaryDisk = false;
        try
        {
            var createResult = await CreateDifferencingDiskAsync(tmpDisk, renamedVhdx);
            if (!createResult.Success)
            {
                File.Move(renamedVhdx, originalAvhdx);
                return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrCreateDiffDiskFailed, createResult.Message));
            }
            createdTemporaryDisk = true;

            var addResult = await AddVmHardDiskDriveAsync(vmId, ctrlType, ctrlNum, ctrlLoc, tmpDisk);
            if (!addResult.Success)
            {
                RequireWormholeCleanupSafety(addResult, tmpDisk, renamedVhdx);
                File.Move(renamedVhdx, originalAvhdx);
                return (false, addResult.Error);
            }

            targetNode.IsWormhole = true;
            targetNode.WormholeTmpDiskPath = tmpDisk;
            targetNode.WormholeRenamedPath = renamedVhdx;
            targetNode.WormholeCtrlType = ctrlType;
            targetNode.WormholeCtrlNum = ctrlNum;
            targetNode.WormholeCtrlLoc = ctrlLoc;

            return (true, Properties.Resources.VmSpacetimeService_MsgWormholeOpened);
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            if (createdTemporaryDisk && File.Exists(tmpDisk)) File.Delete(tmpDisk);
            if (File.Exists(renamedVhdx) && !File.Exists(originalAvhdx))
                File.Move(renamedVhdx, originalAvhdx);
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrSpacetimeException, ex.Message));
        }
    }

    internal static ApiResponse CheckWormholeOutputPaths(string temporaryDisk, string renamedParent)
    {
        foreach (string path in new[] { temporaryDisk, renamedParent })
            if (Path.Exists(path))
                return ApiResponse.Fail($"历史磁盘挂接的输出路径已存在，未修改或删除该路径：{path}",
                    kind: ApiErrorKind.Conflict);
        return ApiResponse.Ok();
    }

    // 虫洞关闭

    /// <summary>移除历史磁盘的临时挂接、删除临时磁盘并恢复原磁盘名。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="node">目标历史检查点或已打开的历史磁盘挂接节点。</param>
    /// <returns>清理结果；同时更新传入节点的挂接状态，部分清理失败需要按消息检查残留。</returns>
    private static async Task<(bool Success, string Message)> CloseWormholeOperationAsync(Guid vmId, SpacetimeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var currentNode = ResolveHistoricalNode(await ReadSpacetimeStateAsync(vmId), node);
        if (currentNode == null)
            return (false, Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid);
        if (!PrepareWormholeClose(node, currentNode))
            return (false, Properties.Resources.VmSpacetimeService_ErrNoActiveWormhole);

        try
        {
            // 按临时磁盘路径核对本 VM 当前资源。查询失败与已卸载必须区分，不能把失败当作 0 号槽位。
            var slotResponse = await GetDiskSlotInfoAsync(currentNode.VmId, node.WormholeTmpDiskPath,
                node.WormholeDriveInstanceId);
            if (!slotResponse.Success)
                return (false, slotResponse.Error);
            if (slotResponse.HasData)
            {
                var slot = slotResponse.Data!;
                node.WormholeDriveInstanceId = slot.DriveInstanceId;
                node.WormholeCtrlType = slot.CtrlType;
                node.WormholeCtrlNum = slot.CtrlNum;
                node.WormholeCtrlLoc = slot.CtrlLoc;
                var removeResult = await RemoveWormholeDiskAsync(slot);
                RequireSuccess(removeResult);
            }

            if (File.Exists(node.WormholeTmpDiskPath))
                File.Delete(node.WormholeTmpDiskPath);

            string originalAvhdx = node.WormholeRenamedPath.Replace(
                "_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(node.WormholeRenamedPath) && !File.Exists(originalAvhdx))
                File.Move(node.WormholeRenamedPath, originalAvhdx);

            node.IsWormhole = false;
            node.WormholeTmpDiskPath = string.Empty;
            node.WormholeRenamedPath = string.Empty;
            node.WormholeDriveInstanceId = string.Empty;

            return (true, Properties.Resources.VmSpacetimeService_MsgWormholeClosed);
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrCloseWormholeException, ex.Message));
        }
    }

    // 快照操作

    /// <summary>修改指定虚拟机的检查点名称；执行前按检查点 GUID 重新核对归属。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="node">目标检查点；使用其 Id 查找本虚拟机当前的检查点。</param>
    /// <param name="newName">要设置的新显示名称；不得为空白。</param>
    /// <returns>Success 表示修改成功，Message 为结果说明。</returns>
    private static async Task<(bool Success, string Message)> RenameSnapshotOperationAsync(Guid vmId, SpacetimeNode node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        try
        {
            var currentNode = ResolveHistoricalNode(await ReadSpacetimeStateAsync(vmId), node);
            if (currentNode == null)
                return (false, Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid);
            var result = await WmiApi.GetByPathAsync(currentNode.Path, obj =>
            {
                obj["ElementName"] = newName;
                return obj.GetText(TextFormat.WmiDtd20);
            });

            RequireData(result);

            var invokeResult = await WmiApi.InvokeAsync(
                ManagementServiceWql,
                "ModifySystemSettings",
                p => p["SystemSettings"] = result.Data);

            RequireSuccess(invokeResult);
            if (invokeResult.Success) node.Name = newName;
            return invokeResult.Success
                ? (true, Properties.Resources.VmSpacetimeService_MsgNameUpdated)
                : (false, string.Format(Properties.Resources.VmSpacetimeService_ErrUpdateWmiCode, invokeResult.Error));
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrRenameException, ex.Message));
        }
    }

    // A caller already displaying these nodes can retain their state changes when
    // an earlier wormhole closes and a later close fails, as the original UI did.
    /// <summary>先关闭已知虫洞，再应用指定检查点并按流程恢复电源状态。</summary>
    /// <param name="node">目标历史检查点或已打开的历史磁盘挂接节点。</param>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="nodes">需同步挂接状态的调用方节点快照；实际操作始终重新读取本虚拟机的节点。</param>
    /// <returns>应用结果；关闭挂接和应用检查点分步执行，后续失败不恢复已关闭的挂接。</returns>
    private static async Task<ApiResponse> TeleportOperationAsync(
        SpacetimeNode node, Guid vmId, IEnumerable<SpacetimeNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodes);
        if (!CanOperateHistoricalNode(node))
            return ApiResponse.Fail(Properties.Resources.VmSpacetimeService_ErrOnlyHistoricalNode, kind: ApiErrorKind.InvalidArgument);

        var currentNodes = await ReadSpacetimeStateAsync(vmId);
        var currentNode = ResolveHistoricalNode(currentNodes, node);
        if (currentNode == null)
            return ApiResponse.Fail(Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid, kind: ApiErrorKind.NotFound);
        var callerNodes = nodes.Append(node).ToList();
        RestoreKnownWormholeCleanup(currentNodes, callerNodes);
        RefreshNodeOperationState(node, currentNode);

        var steps = new List<OperationStepResult>();
        foreach (var wormhole in currentNodes.Where(candidate => candidate.IsWormhole).ToList())
        {
            var result = await RunSpacetimeOperationAsync(vmId, async () =>
            {
                var closed = await CloseWormholeOperationAsync(vmId, wormhole);
                foreach (var callerNode in callerNodes.Where(candidate =>
                    string.Equals(candidate.Id, wormhole.Id, StringComparison.OrdinalIgnoreCase)))
                    RefreshNodeOperationState(callerNode, wormhole);
                return closed;
            });
            steps.Add(OperationStepResult.From("CloseWormhole", result) with { ResourceId = wormhole.Id });
            if (!result.Success) return result.WithSteps(steps);
        }
        var applied = await RunSpacetimeResponseAsync(vmId, () => ApplySnapshotCoreAsync(currentNode, vmId));
        return applied.WithSteps(steps.Concat(applied.Steps));
    }

    internal static async Task<(bool Success, string Message)> CloseWormholesAndTeleportOperationAsync(
        SpacetimeNode node, IEnumerable<SpacetimeNode> nodes,
        Func<SpacetimeNode, Task<(bool Success, string Message)>> close,
        Func<Task<(bool Success, string Message)>> teleport)
    {
        if (!CanOperateHistoricalNode(node))
            return (false, Properties.Resources.VmSpacetimeService_ErrOnlyHistoricalNode);

        foreach (var wormhole in nodes.Where(candidate => candidate.IsWormhole).ToList())
        {
            var result = await close(wormhole);
            if (!result.Success) return result;
        }
        return await teleport();
    }

    private static async Task<ApiResponse> ApplySnapshotCoreAsync(SpacetimeNode node, Guid vmId)
    {
        string vmWql = $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";
        var backend = new SnapshotApplyBackend(
            () => WmiApi.QueryFirstAsync(vmWql, obj => Convert.ToUInt16(obj["EnabledState"])),
            action => VmPowerService.ExecuteControlActionAsync(vmId, action),
            () => WmiApi.InvokeAsync(SnapshotServiceWql, "ApplySnapshot", p => p["Snapshot"] = node.Path),
            milliseconds => Task.Delay(milliseconds));
        return await ApplySnapshotAsync(backend);
    }

    internal sealed record SnapshotApplyBackend(
        Func<Task<ApiResponse<ushort>>> ReadState,
        Func<VmPowerAction, Task<ApiResponse>> Power,
        Func<Task<ApiResponse>> Apply,
        Func<int, Task> Delay);

    internal static async Task<ApiResponse> ApplySnapshotAsync(SnapshotApplyBackend backend)
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
                if (!ready) return Finish(ApiResponse.Fail("虚拟机未进入可应用检查点的状态，未执行检查点应用。", kind: ApiErrorKind.InvalidState), stage);
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
                    return Finish(ApiResponse.Fail("检查点已应用，但无法读取重新启动所需的状态：" + failure.Error,
                        failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind), stage);
                }
                if (current.Data is 3 or 6)
                {
                    stage = "Start";
                    var started = await backend.Power(VmPowerAction.Start);
                    return Finish(started.Success ? started : ApiResponse.Fail("检查点已应用，但重新启动失败：" + started.Error,
                        started.Code, started.ErrorSource, started.Exception, started.ErrorKind), stage);
                }
                await backend.Delay(1000);
            }
            return Finish(ApiResponse.Fail("检查点已应用，但虚拟机未进入可重新启动的状态。", kind: ApiErrorKind.InvalidState), stage);
        }
        catch (Exception ex)
        {
            // 写入期间抛异常时不能推定请求没有提交；只读/等待阶段失败不重放此前步骤。
            return Finish(WmiJobWaiter.FromException(ex, stage is "Save" or "Apply" or "Start"), stage);
        }
    }

    private static ApiResponse SnapshotStateFailure(ApiResponse<ushort> result) => ApiResponse.Fail(
        result.Success ? Properties.Resources.VmSpacetimeService_ErrVmNotFound : result.Error,
        result.Code, result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind);

    /// <summary>创建检查点并按需缓存缩略图；未运行时采用标准检查点，即使请求生产检查点。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="mode">Continuous 创建标准检查点；其他模式在运行时请求生产检查点，未运行时回退为标准检查点。</param>
    /// <param name="externalThumb">可选的外部原始截图；null 时按流程尝试获取当前截图。</param>
    /// <param name="encodeThumbnail">可选截图编码器，将原始截图转换为缓存用图像字节；null 时不由调用者提供编码。</param>
    /// <returns>Success 表示检查点创建成功，Message 为结果说明；可能产生检查点及缓存文件。</returns>
    private static Task<(bool Success, string Message)> CaptureMomentOperationAsync(
        Guid vmId, SpacetimeMode mode, VmScreenshotImage? externalThumb = null, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null) =>
        RunCheckpointMutationAsync(
            () => ReadSpacetimeStateAsync(vmId),
            () => CaptureMomentCoreAsync(vmId, mode, externalThumb, encodeThumbnail));

    private static async Task<(bool Success, string Message)> CaptureMomentCoreAsync(
        Guid vmId, SpacetimeMode mode, VmScreenshotImage? externalThumb = null, Func<VmScreenshotImage, byte[]>? encodeThumbnail = null)
    {
        var vmResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            obj => new { Path = obj.Path.ToString(), Guid = obj["Name"]?.ToString() ?? "", Name = obj["ElementName"]?.ToString() ?? "" });

        if (!vmResponse.HasData)
            return (false, Properties.Resources.VmSpacetimeService_ErrVmCarrierNotFound);

        var vm = vmResponse.Data!;

        string snapshotListWql = $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'";
        var existingResponse = await WmiApi.QueryAsync(snapshotListWql, obj => obj["InstanceID"]?.ToString() ?? "");
        RequireSuccess(existingResponse);
        var existingIds = (existingResponse.Data ?? new List<string>()).ToHashSet();

        VmScreenshotImage? bitmap = externalThumb ?? await VmScreenshotSource.CaptureAsync(vmId, 280, 160);
        string snapshotName = $"{vm.Name} - ({DateTime.Now:yyyy/M/d - HH:mm:ss})";

        // Production 快照需要 VM 运行中（需要 guest VSS 配合），关机时自动降级为 Standard
        var stateResp = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            obj => Convert.ToInt32(obj["EnabledState"] ?? 0));
        bool isRunning = RequireData(stateResp) == 2;
        string settingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}";
        ushort originalPolicy = RequireData(await WmiApi.QueryFirstAsync(settingsWql,
            obj => obj["UserSnapshotType"] == null ? (ushort?)null : Convert.ToUInt16(obj["UserSnapshotType"])))!.Value;
        ushort requestedPolicy = mode == SpacetimeMode.Still && isRunning ? (ushort)4 : (ushort)5;

        try
        {
            // User checkpoints use SnapshotType=2. The generic CIM disk-snapshot
            // value 3 is rejected. VMMS chooses production from the live VM policy,
            // and ignores UserSnapshotType supplied in the embedded snapshot settings.
            using var svcForScope = WmiApi.GetVirtualSystemManagementService();
            using var snapClass = new ManagementClass(svcForScope.Scope,
                new ManagementPath("Msvm_VirtualSystemSettingData"), null);
            using var snapSettings = snapClass.CreateInstance();
            snapSettings["ElementName"] = snapshotName;
            string snapXml = snapSettings.GetText(TextFormat.WmiDtd20);

            var createResult = await CaptureWithPolicyAsync(originalPolicy, requestedPolicy,
                (expected, value) => ChangeSnapshotPolicyAsync(settingsWql, expected, value),
                () => WmiApi.InvokeAsync(
                SnapshotServiceWql,
                "CreateSnapshot",
                p =>
                {
                    p["AffectedSystem"] = vm.Path;
                    p["SnapshotSettings"] = snapXml;
                    p["SnapshotType"] = (ushort)2;
                }));

            RequireSuccess(createResult);
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrSpacetimeCollapse, ex.Message));
        }

        string? newId = null;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(200);
            var currentIds = await WmiApi.QueryAsync(snapshotListWql, obj => obj["InstanceID"]?.ToString() ?? "");
            newId = currentIds.Data?.FirstOrDefault(id => !existingIds.Contains(id));
            if (newId != null) break;
        }

        if (originalPolicy != requestedPolicy)
        {
            if (newId == null)
                throw new SpacetimeReadException(ApiResponse.Fail("检查点已创建，原虚拟机策略已恢复，但新检查点身份尚未确认。", kind: ApiErrorKind.CleanupRequired));
            // Applying the checkpoint must also retain the user's original policy.
            string historicalWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE InstanceID = '{newId.Replace("'", "''")}'";
            var restored = await ChangeSnapshotPolicyAsync(historicalWql, requestedPolicy, originalPolicy);
            if (!restored.Success)
                throw new SpacetimeReadException(ApiResponse.Fail("检查点已创建，恢复检查点内的原策略时失败：" + restored.Error,
                    restored.Code, restored.ErrorSource, restored.Exception,
                    restored.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired));
        }

        if (newId != null && bitmap != null)
        {
            string? snapshotDir = await GetSnapshotDirectoryByGuidAsync(vm.Guid);
            if (!string.IsNullOrEmpty(snapshotDir))
                await SaveThumbnailToDisk(bitmap, snapshotDir, newId, encodeThumbnail);
        }

        return (true, Properties.Resources.VmSpacetimeService_MsgAnchorSet);
    }

    private static async Task<ApiResponse> ChangeSnapshotPolicyAsync(string wql, ushort expected, ushort value)
    {
        var settings = await WmiApi.QueryFirstAsync(wql, obj =>
        {
            ushort current = Convert.ToUInt16(obj["UserSnapshotType"]);
            if (current == value) return (Current: current, Xml: "");
            obj["UserSnapshotType"] = value;
            return (Current: current, Xml: obj.GetText(TextFormat.WmiDtd20));
        });
        if (!settings.HasData) return ApiResponse.Fail(settings.Success ? "检查点策略对应的配置已缺失。" : settings.Error,
            settings.Code, settings.ErrorSource, settings.Exception, settings.Success ? ApiErrorKind.NotFound : settings.ErrorKind);
        if (settings.Data.Current == value) return ApiResponse.Ok();
        if (settings.Data.Current != expected)
            return ApiResponse.Fail($"检查点策略已由其他调用者改为 {settings.Data.Current}，保留该值。", kind: ApiErrorKind.Conflict);
        return await WmiApi.InvokeAsync(ManagementServiceWql, "ModifySystemSettings", p => p["SystemSettings"] = settings.Data.Xml);
    }

    internal static async Task<ApiResponse> CaptureWithPolicyAsync(ushort original, ushort requested,
        Func<ushort, ushort, Task<ApiResponse>> change, Func<Task<ApiResponse>> create)
    {
        if (original == 2) return ApiResponse.Fail("此虚拟机已禁用检查点。", kind: ApiErrorKind.InvalidState);
        if (original is not (3 or 4 or 5)) return ApiResponse.Fail("无法识别当前检查点策略。", kind: ApiErrorKind.Unsupported);
        static async Task<ApiResponse> Invoke(Func<Task<ApiResponse>> operation)
        {
            try { return await operation(); }
            catch (Exception error) { return WmiJobWaiter.FromException(error, submitted: true); }
        }
        if (original == requested) return await Invoke(create);

        var changed = await Invoke(() => change(original, requested));
        var result = changed.Success ? await Invoke(create) : changed;
        if (result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            return ApiResponse.Fail($"{result.Error} 原检查点策略为 {original}，本次请求策略为 {requested}；核对原作业终态后再恢复策略。",
                result.Code, result.ErrorSource, result.Exception, ApiErrorKind.OutcomeUnknown);

        // A settled failure can still follow a successful policy change. Restore it
        // without overwriting another caller's later policy or retrying the capture.
        var restored = await Invoke(() => change(requested, original));
        if (!restored.Success)
            return ApiResponse.Fail($"{result.Error} 检查点原策略 {original} 尚未恢复：{restored.Error}",
                restored.Code, restored.ErrorSource, restored.Exception,
                restored.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
        return result;
    }

    /// <summary>删除指定历史检查点及其整个后代检查点树。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="node">目标历史检查点或已打开的历史磁盘挂接节点。</param>
    /// <returns>Success 表示 Hyper-V 删除树完成，Message 为结果；这是不可由返回值回滚的删除操作。</returns>
    private static async Task<(bool Success, string Message)> AnnihilateOperationAsync(Guid vmId, SpacetimeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsLogicalNode)
            return (false, Properties.Resources.VmSpacetimeService_ErrOriginCurrentNoAnnihilate);

        var nodes = await ReadSpacetimeStateAsync(vmId);
        var currentNode = ResolveHistoricalNode(nodes, node);
        if (currentNode == null)
            return (false, Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid);
        return await RunCheckpointMutationAsync(() => Task.FromResult(nodes),
            () => AnnihilateCoreAsync(vmId, currentNode));
    }

    private static async Task<(bool Success, string Message)> AnnihilateCoreAsync(Guid vmId, SpacetimeNode node)
    {
        var result = await WmiApi.InvokeAsync(
            SnapshotServiceWql, "DestroySnapshotTree",
            p => p["SnapshotSettingData"] = node.Path);

        RequireSuccess(result);
        if (result.Success)
        {
            string? dir = await GetSnapshotDirectoryAsync(vmId);
            if (!string.IsNullOrEmpty(dir)) DeleteThumbnailFile(dir, node.InstanceId);
            return (true, Properties.Resources.VmSpacetimeService_MsgAnnihilated);
        }
        return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrAnnihilateFailed, result.Error));
    }

    /// <summary>删除并合并单个历史检查点，保留由 Hyper-V 重新连接的后继关系。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="node">目标历史检查点或已打开的历史磁盘挂接节点。</param>
    /// <returns>Success 表示 Hyper-V 单检查点删除完成，Message 为结果；不同于删除整个子树。</returns>
    private static async Task<(bool Success, string Message)> ConvergeOperationAsync(Guid vmId, SpacetimeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsLogicalNode)
            return (false, Properties.Resources.VmSpacetimeService_ErrOriginCurrentNoConverge);

        var nodes = await ReadSpacetimeStateAsync(vmId);
        var currentNode = ResolveHistoricalNode(nodes, node);
        if (currentNode == null)
            return (false, Properties.Resources.VmSpacetimeService_ErrSnapshotPathInvalid);
        return await RunCheckpointMutationAsync(() => Task.FromResult(nodes),
            () => ConvergeCoreAsync(vmId, currentNode));
    }

    private static async Task<(bool Success, string Message)> ConvergeCoreAsync(Guid vmId, SpacetimeNode node)
    {
        var result = await WmiApi.InvokeAsync(
            SnapshotServiceWql, "DestroySnapshot",
            p => p["AffectedSnapshot"] = node.Path);

        System.Diagnostics.Debug.WriteLine($"[Converge] node.Path={node.Path}, result.Success={result.Success}, error={result.Error}");

        RequireSuccess(result);
        if (result.Success)
        {
            string? dir = await GetSnapshotDirectoryAsync(vmId);
            if (!string.IsNullOrEmpty(dir)) DeleteThumbnailFile(dir, node.InstanceId);
            return (true, Properties.Resources.VmSpacetimeService_MsgConverging);
        }
        return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrConvergeFailed, result.Error));
    }

    /// <summary>按稳定身份读取虚拟机检查点是否启用。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <returns>成功时返回实际设置；配置缺失或查询失败返回错误，不推断为启用。</returns>
    public static async Task<ApiResponse<bool>> GetCheckpointsEnabledAsync(Guid vmId)
    {
        if (vmId == Guid.Empty) return ApiResponse<bool>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        var result = await WmiApi.QueryFirstAsync(
            $"SELECT UserSnapshotType FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => obj["UserSnapshotType"] == null ? (bool?)null : Convert.ToByte(obj["UserSnapshotType"]) != 2);
        return result.HasData ? ApiResponse<bool>.Ok(result.Data.Value)
            : ApiResponse<bool>.Fail(result.Success ? "检查点设置不存在或未提供有效值。" : result.Error,
                result.Code, result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind);
    }

    /// <summary>按稳定身份设置虚拟机是否允许使用检查点。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID。</param>
    /// <param name="enabled">true 启用检查点，false 禁用。</param>
    /// <returns>设置结果；错误包含实际查询或提交原因。</returns>
    public static Task<ApiResponse> SetCheckpointsEnabledAsync(Guid vmId, bool enabled)
    {
        if (vmId == Guid.Empty) return Task.FromResult(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
        return VmSettingsWriteLock.RunAsync(vmId, () => WmiApi.WithObjectAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => obj["UserSnapshotType"] = enabled ? (byte)3 : (byte)2));
    }

    // WMI 磁盘操作辅助方法

    /// <summary>获取虚拟机所有 SCSI 磁盘的文件路径。</summary>
    private static async Task<List<string>> GetVmScsiDiskPathsAsync(string vmGuid)
    {
        var response = await WmiApi.QueryAsync(
            $"SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty);

        RequireSuccess(response);
        return (response.Data ?? new List<string>())
            .Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    private sealed record WormholeDiskSlot(string CtrlType, int CtrlNum, int CtrlLoc,
        string? MediaPath, string DrivePath, string DriveInstanceId);

    /// <summary>按磁盘路径取得本 VM 的实际挂接；Empty 表示已卸载，Fail 表示无法确认，绝不猜测槽位。</summary>
    private static async Task<ApiResponse<WormholeDiskSlot>> GetDiskSlotInfoAsync(
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
            return ApiResponse<WormholeDiskSlot>.Fail(sasdResponse.Error);

        var sasd = sasdResponse.Data?.FirstOrDefault(d =>
            d.ResourceType == 31 && d.HostResource.Equals(diskPath, StringComparison.OrdinalIgnoreCase));

        string driveInstanceId;
        if (sasd == null)
        {
            if (string.IsNullOrEmpty(knownDriveInstanceId)) return ApiResponse<WormholeDiskSlot>.Empty();
            // 介质移除成功、驱动器移除失败时仍要收尾；如果该驱动器已被其他介质占用，则禁止移除。
            if (sasdResponse.Data?.Any(item => string.Equals(
                ExtractInstanceId(item.Parent)?.Replace("\\\\", "\\"), knownDriveInstanceId,
                StringComparison.OrdinalIgnoreCase)) == true)
                return ApiResponse<WormholeDiskSlot>.Fail("历史磁盘的驱动器已被其他介质占用，未执行移除。");
            driveInstanceId = knownDriveInstanceId;
        }
        else
        {
            driveInstanceId = ExtractInstanceId(sasd.Parent)?.Replace("\\\\", "\\") ?? "";
        }

        if (!driveInstanceId.StartsWith($"Microsoft:{vmGuid}\\", StringComparison.OrdinalIgnoreCase))
            return ApiResponse<WormholeDiskSlot>.Fail("无法确认历史磁盘的驱动器属于当前虚拟机。");

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

        if (!rasdResponse.Success) return ApiResponse<WormholeDiskSlot>.Fail(rasdResponse.Error);
        if (!rasdResponse.HasData)
            return sasd == null ? ApiResponse<WormholeDiskSlot>.Empty()
                : ApiResponse<WormholeDiskSlot>.Fail("无法确认历史磁盘挂接的驱动器。");
        if (rasdResponse.Data.ResourceType != 17)
            return ApiResponse<WormholeDiskSlot>.Fail("历史磁盘的挂接目标不是硬盘驱动器。");
        if (!string.IsNullOrWhiteSpace(rasdResponse.Data.HostResource))
            return ApiResponse<WormholeDiskSlot>.Fail("历史磁盘的驱动器已直接引用其他介质，未执行移除。");

        var ctrlListResp = await WmiApi.QueryAsync(
            $"SELECT InstanceID FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 6 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => obj["InstanceID"]?.ToString() ?? "");
        if (!ctrlListResp.Success) return ApiResponse<WormholeDiskSlot>.Fail(ctrlListResp.Error);

        var slot = ResolveWormholeSlot(rasdResponse.Data.Parent, rasdResponse.Data.Location,
            ctrlListResp.Data ?? []);
        if (slot == null)
            return ApiResponse<WormholeDiskSlot>.Fail("无法确认历史磁盘的控制器及槽位，未执行移除。");
        return ApiResponse<WormholeDiskSlot>.Ok(new("SCSI", slot.Value.Controller, slot.Value.Location,
            sasd?.Path, rasdResponse.Data.Path, driveInstanceId));
    }

    internal static (int Controller, int Location)? ResolveWormholeSlot(
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

    private static async Task<ApiResponse> RemoveWormholeDiskAsync(WormholeDiskSlot slot)
    {
        // 使用刚核对的实际资源路径，而不重新按控制器索引选盘。
        if (!string.IsNullOrEmpty(slot.MediaPath))
        {
            var mediaResult = await WmiApi.InvokeAsync(ManagementServiceWql, "RemoveResourceSettings",
                parameters => parameters["ResourceSettings"] = new[] { slot.MediaPath });
            if (!mediaResult.Success) return mediaResult;
        }
        return await WmiApi.InvokeAsync(ManagementServiceWql, "RemoveResourceSettings",
            parameters => parameters["ResourceSettings"] = new[] { slot.DrivePath });
    }

    /// <summary>
    /// 添加虚拟磁盘到虚拟机。直接复用 VmStorageService.AddDriveAsync。
    /// </summary>
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

    // 空闲槽位查找

    private static async Task<(string, int, int)> FindFreeScsiSlotAsync(Guid vmId)
    {
        try
        {
            using var vm = WmiApi.GetVmComputerSystem(vmId);
            if (vm == null) return ("SCSI", -1, -1);
            string vmGuid = vm["Name"]?.ToString() ?? "";

            // 磁盘和 DVD 都会占用控制器槽位。
            // 控制器匹配用 Parent 里的 InstanceID 精确比对(去 \\ 转义)——原实现 u.Contains(controllerId) 因 Parent 是双反斜杠、
            // 控制器 InstanceID 是单反斜杠，恒不匹配 → 永远 0 命中 → 永远返回 (0,0) 撞启动盘(实测命中=0、报"位置正在使用")。
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

    private static async Task<bool> CheckAnyWormholeExistsAsync(Guid vmId)
    {
        try
        {
            var guidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            RequireData(guidResponse);

            var paths = await GetVmScsiDiskPathsAsync(guidResponse.Data!);
            return paths.Any(p => p.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch { throw; }
    }

    private static async Task<bool> IsNodeInCurrentChainAsync(Guid vmId, string targetVhdPath)
    {
        var paths = await GetVmScsiDiskPathsAsync(vmId.ToString("D"));
        foreach (string attached in paths.Where(path => !path.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase)))
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

    // 底层辅助

    /// <summary>
    /// 创建差分磁盘。走 WmiApi.InvokeAsync，不再直接持有 ManagementScope。
    /// </summary>
    private static async Task<(bool Success, string Message)> CreateDifferencingDiskAsync(
        string newPath, string parentPath)
    {
        try
        {
            // 借用 GetVirtualSystemManagementService 的 Scope 构造设置对象
            using var svcForScope = WmiApi.GetVirtualSystemManagementService();
            using var settingClass = new ManagementClass(
                svcForScope.Scope,
                new ManagementPath("Msvm_VirtualHardDiskSettingData"),
                null);
            using var setting = settingClass.CreateInstance();
            setting["Type"] = (ushort)4;   // Differencing
            setting["Format"] = (ushort)3; // VHDX
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

            RequireWormholeCleanupSafety(result, newPath, parentPath);
            return result.Success
                ? (true, string.Empty)
                : (false, result.Error);
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void RequireWormholeCleanupSafety(ApiResponse response, string diskPath, string parentPath)
    {
        if (response.ErrorKind is not (ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired)) return;
        RequireSuccess(ApiResponse.Fail(response.Error + Environment.NewLine +
            $"磁盘操作仍需核对，已保留临时磁盘及父盘，未删除或恢复文件名。临时磁盘：{diskPath}；父盘：{parentPath}",
            response.Code, response.ErrorSource, response.Exception, response.ErrorKind));
    }

    /// <summary>获取已确认的 VHD/VHDX 父路径；独立盘返回空字符串，无法确认时停止操作。</summary>
    private static async Task<string> GetVhdParentPathAsync(string vhdPath)
    {
        var result = await HostVirtualDiskMetadata.ReadParentPathAsync(vhdPath).ConfigureAwait(false);
        RequireSuccess(result);
        return result.Data ?? string.Empty;
    }

    /// <summary>沿差分链追溯用于显示创建时间的根盘；无法确认时不把中间盘当成根盘。</summary>
    private static string TraceToGenesisPath(string childPath)
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

    // 私有辅助（不改动）

    private static async Task<string?> GetSnapshotDirectoryAsync(Guid vmId)
    {
        try
        {
            var vmResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            if (!vmResponse.HasData || string.IsNullOrWhiteSpace(vmResponse.Data))
                return null;

            var response = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData " +
                $"WHERE VirtualSystemIdentifier = '{WmiApi.Escape(vmResponse.Data)}' " +
                "AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["ConfigurationDataRoot"]?.ToString());
            string? root = response.Data;
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Snapshots");
        }
        catch { return null; }
    }

    private static async Task<string?> GetSnapshotDirectoryByGuidAsync(string vmGuid)
    {
        try
        {
            var response = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmGuid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["ConfigurationDataRoot"]?.ToString());
            string? root = response.Data;
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Snapshots");
        }
        catch { return null; }
    }

    private static void DeleteThumbnailFile(string snapshotDir, string nodeId)
    {
        try
        {
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(nodeId)}.jpg");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Debug.WriteLine(string.Format(
                    Properties.Resources.VmSpacetimeService_LogSnapshotScreenCleaned, nodeId));
            }
        }
        catch (SpacetimeReadException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(
                Properties.Resources.VmSpacetimeService_LogCleanScreenFailed, ex.Message));
        }
    }

    private static async Task<List<SpacetimeNode>> CreateInitialSpacetimeAsync(Guid vmId, string snapshotDir, Func<VmScreenshotImage, byte[]>? encodeThumbnail)
    {
        var thumb = await VmScreenshotSource.CaptureAsync(vmId, 280, 160);
        if (thumb != null && !string.IsNullOrEmpty(snapshotDir))
            await SaveThumbnailToDisk(thumb, snapshotDir, SpacetimeNode.GenesisId, encodeThumbnail);

        return CreateInitialSpacetimeNodes(thumb);
    }

    internal static List<SpacetimeNode> CreateInitialSpacetimeNodes(VmScreenshotImage? thumb)
    {
        return new List<SpacetimeNode>
        {
            new() { Id = SpacetimeNode.GenesisId, Name = Properties.Resources.VmSpacetimeService_NodeLabelOrigin,  NodeType = SpacetimeNodeType.Genesis,  IsCurrent = true, CreatedDate = DateTime.Now.AddMinutes(-1), Thumbnail = thumb },
            new() { Id = SpacetimeNode.CurrentId, Name = Properties.Resources.VmSpacetimeService_NodeLabelCurrent, NodeType = SpacetimeNodeType.Current, ParentId = SpacetimeNode.GenesisId, IsCurrent = true, CreatedDate = DateTime.Now, Thumbnail = thumb }
        };
    }

    private static VmScreenshotImage? LoadThumbnailFromDisk(string snapshotDir, string id)
    {
        try
        {
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(id)}.jpg");
            return File.Exists(filePath) ? VmScreenshotImage.FromEncoded(File.ReadAllBytes(filePath)) : null;
        }
        catch { return null; }
    }

    private static async Task SaveThumbnailToDisk(VmScreenshotImage bitmap, string snapshotDir, string id, Func<VmScreenshotImage, byte[]>? encodeThumbnail)
    {
        try
        {
            if (!Directory.Exists(snapshotDir)) Directory.CreateDirectory(snapshotDir);
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(id)}.jpg");
            await Task.Run(() => File.WriteAllBytes(filePath, encodeThumbnail != null ? encodeThumbnail(bitmap) : bitmap.EncodeJpeg()));
        }
        catch { }
    }

    private static string? ExtractInstanceId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var match = Regex.Match(path, "InstanceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
