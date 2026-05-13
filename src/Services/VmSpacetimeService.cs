using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Tools.Api;

namespace ExHyperV.Services;

internal class VmSpacetimeService
{
    private const string SnapshotServiceWql = "SELECT * FROM Msvm_VirtualSystemSnapshotService";
    private const string ManagementServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private readonly VmPowerService _powerService = new();
    private string GetSafeId(string id) => id.Replace(":", "_");

    // ============================================================
    // 时空节点查询
    // ============================================================

    public async Task<List<SpacetimeNode>> GetSpacetimeNodesAsync(string vmName)
    {
        try
        {
            var vmResponse = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => new { Guid = obj["Name"]?.ToString(), Path = obj.Path.ToString() });

            if (!vmResponse.HasData || string.IsNullOrEmpty(vmResponse.Data!.Guid))
                return new List<SpacetimeNode>();

            var vm = vmResponse.Data!;

            var configResponse = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["ConfigurationDataRoot"]?.ToString());

            string configRoot = configResponse.Data ?? string.Empty;
            string snapshotDir = Path.Combine(configRoot, "Snapshots");

            var allRawNodes = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}'",
                obj => new SpacetimeNode
                {
                    Id = obj["InstanceID"]?.ToString() ?? "",
                    Name = obj["ElementName"]?.ToString() ?? "",
                    CreatedDate = obj["CreationTime"] != null
                        ? ManagementDateTimeConverter.ToDateTime(obj["CreationTime"].ToString())
                        : DateTime.MinValue,
                    ParentId = ExtractInstanceId(obj["Parent"]?.ToString()),
                    VirtualSystemType = obj["VirtualSystemType"]?.ToString() ?? "",
                    Path = obj.Path.ToString()
                });

            if (!allRawNodes.Data?.Any() ?? true)
                return await CreateInitialSpacetimeAsync(vmName, snapshotDir);

            var nodes = allRawNodes.Data!;
            var snapshots = nodes.Where(n => n.VirtualSystemType.Contains("Snapshot")).ToList();
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
                    var sasd = sasdList.FirstOrDefault(d => d.Id.Contains(snap.Id));
                    if (!string.IsNullOrEmpty(sasd?.Path))
                        snap.VhdPath = sasd.Path.Replace("_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
                }

                string currentVhdPath = sasdList.FirstOrDefault(d => d.Id.Contains(realizedNode.Id))?.Path ?? string.Empty;
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
                node.Thumbnail = LoadThumbnailFromDisk(snapshotDir, node.Id);
            }

            var genesisThumbnail = LoadThumbnailFromDisk(snapshotDir, SpacetimeNode.GenesisId);
            if (genesisThumbnail == null)
            {
                genesisThumbnail = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
                if (genesisThumbnail != null)
                    await SaveThumbnailToDisk(genesisThumbnail, snapshotDir, SpacetimeNode.GenesisId);
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

            currentNode.Thumbnail = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
            _ = Task.Run(async () => await DetectAndMarkWormholeAsync(vmName, result));

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_ErrLookupFailed, ex.Message));
            return new List<SpacetimeNode>();
        }
    }

    // ============================================================
    // 虫洞检测
    // ============================================================

    private async Task DetectAndMarkWormholeAsync(string vmName, List<SpacetimeNode> nodes)
    {
        try
        {
            var vmGuidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString());
            if (!vmGuidResponse.HasData) return;

            string vmGuid = vmGuidResponse.Data!;
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

            if (targetNode == null) return;

            // 从 WMI 获取控制器信息
            var slotInfo = await GetDiskSlotInfoAsync(vmGuid, wormholePath);

            targetNode.IsWormhole = true;
            targetNode.WormholeTmpDiskPath = wormholePath;
            targetNode.WormholeRenamedPath = renamedParentPath;
            targetNode.WormholeCtrlType = slotInfo.CtrlType;
            targetNode.WormholeCtrlNum = slotInfo.CtrlNum;
            targetNode.WormholeCtrlLoc = slotInfo.CtrlLoc;

            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_LogWormholeMarkedOk, targetNode.Name));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_LogWormholeDetectFailed, ex.Message));
        }
    }

    // ============================================================
    // 虫洞开启
    // ============================================================

    public async Task<(bool Success, string Message)> OpenWormholeAsync(string vmName, SpacetimeNode targetNode)
    {
        if (await CheckAnyWormholeExistsAsync(vmName))
            return (false, Properties.Resources.VmSpacetimeService_ErrWormholeAlreadyExists);

        if (await IsNodeInCurrentChainAsync(vmName, targetNode.VhdPath))
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
        if (File.Exists(tmpDisk)) File.Delete(tmpDisk);

        var (ctrlType, ctrlNum, ctrlLoc) = await FindFreeScsiSlotAsync(vmName);
        if (ctrlNum == -1) return (false, Properties.Resources.VmSpacetimeService_ErrNoScsiSlot);

        File.Move(originalAvhdx, renamedVhdx);

        try
        {
            var createResult = await CreateDifferencingDiskAsync(tmpDisk, renamedVhdx);
            if (!createResult.Success)
            {
                File.Move(renamedVhdx, originalAvhdx);
                return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrCreateDiffDiskFailed, createResult.Message));
            }

            var addResult = await AddVmHardDiskDriveAsync(vmName, ctrlType, ctrlNum, ctrlLoc, tmpDisk);
            if (!addResult.Success)
            {
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
        catch (Exception ex)
        {
            if (File.Exists(tmpDisk)) File.Delete(tmpDisk);
            if (File.Exists(renamedVhdx) && !File.Exists(originalAvhdx))
                File.Move(renamedVhdx, originalAvhdx);
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrSpacetimeException, ex.Message));
        }
    }

    // ============================================================
    // 虫洞关闭
    // ============================================================

    public async Task<(bool Success, string Message)> CloseWormholeAsync(string vmName, SpacetimeNode node)
    {
        if (!node.IsWormhole)
            return (false, Properties.Resources.VmSpacetimeService_ErrNoActiveWormhole);

        try
        {
            var removeResult = await RemoveVmHardDiskDriveAsync(
                vmName, node.WormholeCtrlType, node.WormholeCtrlNum, node.WormholeCtrlLoc);

            if (!removeResult.Success)
                return (false, removeResult.Error);

            if (File.Exists(node.WormholeTmpDiskPath))
                File.Delete(node.WormholeTmpDiskPath);

            string originalAvhdx = node.WormholeRenamedPath.Replace(
                "_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(node.WormholeRenamedPath) && !File.Exists(originalAvhdx))
                File.Move(node.WormholeRenamedPath, originalAvhdx);

            node.IsWormhole = false;
            node.WormholeTmpDiskPath = string.Empty;
            node.WormholeRenamedPath = string.Empty;

            return (true, Properties.Resources.VmSpacetimeService_MsgWormholeClosed);
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrCloseWormholeException, ex.Message));
        }
    }

    // ============================================================
    // 快照操作
    // ============================================================

    public async Task<(bool Success, string Message)> RenameSnapshotAsync(string snapshotPath, string newName)
    {
        try
        {
            var result = await WmiApi.GetByPathAsync(snapshotPath, obj =>
            {
                obj["ElementName"] = newName;
                return obj.GetText(TextFormat.WmiDtd20);
            });

            if (!result.HasData)
                return (false, Properties.Resources.VmSpacetimeService_ErrWmiMgmtSvcNotFound);

            var invokeResult = await WmiApi.InvokeAsync(
                ManagementServiceWql,
                "ModifySystemSettings",
                p => p["SystemSettings"] = result.Data);

            return invokeResult.Success
                ? (true, Properties.Resources.VmSpacetimeService_MsgNameUpdated)
                : (false, string.Format(Properties.Resources.VmSpacetimeService_ErrUpdateWmiCode, invokeResult.Error));
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrRenameException, ex.Message));
        }
    }

    public async Task<(bool Success, string Message)> TeleportAsync(SpacetimeNode node, string vmName)
    {
        if (node.NodeType != SpacetimeNodeType.Snapshot)
            return (false, Properties.Resources.VmSpacetimeService_ErrOnlyHistoricalNode);

        try
        {
            string vmWql = $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";
            var stateResponse = await WmiApi.QueryFirstAsync(vmWql, obj => (ushort)(obj["EnabledState"] ?? 0));
            ushort initialState = stateResponse.Data;
            bool shouldRestart = initialState == 2 || initialState == 32768;

            if (initialState != 3 && initialState != 6)
            {
                await _powerService.ExecuteControlActionAsync(vmName, "Save");
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(300);
                    var check = await WmiApi.QueryFirstAsync(vmWql, obj => (ushort)(obj["EnabledState"] ?? 0));
                    if (check.Data == 6) break;
                }
            }

            var result = await WmiApi.InvokeAsync(
                SnapshotServiceWql, "ApplySnapshot",
                p => p["Snapshot"] = node.Path);

            if (result.Success)
            {
                if (shouldRestart)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        for (int i = 0; i < 10; i++)
                        {
                            var s = await WmiApi.QueryFirstAsync(vmWql, obj => (ushort)(obj["EnabledState"] ?? 0));
                            if (s.Data == 3 || s.Data == 6)
                            {
                                await _powerService.ExecuteControlActionAsync(vmName, "Start");
                                break;
                            }
                            await Task.Delay(1000);
                        }
                    });
                    return (true, Properties.Resources.VmSpacetimeService_MsgTravelInitiated);
                }
                return (true, Properties.Resources.VmSpacetimeService_MsgTravelSucceeded);
            }
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrTravelFailed, result.Error));
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrSpacetimeException, ex.Message));
        }
    }

    public async Task<(bool Success, string Message)> CaptureMomentAsync(
        string vmName, SpacetimeMode mode, BitmapSource? externalThumb = null)
    {
        var vmResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => new { Path = obj.Path.ToString(), Guid = obj["Name"]?.ToString() ?? "" });

        if (!vmResponse.HasData)
            return (false, Properties.Resources.VmSpacetimeService_ErrVmCarrierNotFound);

        var vm = vmResponse.Data!;

        // 读取当前快照类型
        string settingsWql = $"SELECT UserSnapshotType FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
        var typeResponse = await WmiApi.QueryFirstAsync(settingsWql, obj => obj["UserSnapshotType"]?.ToString());
        string originalType = typeResponse.Data == "5" ? "Standard" : "Production";
        byte targetTypeCode = mode == SpacetimeMode.Continuous ? (byte)5 : (byte)3;
        byte originalTypeCode = originalType == "Standard" ? (byte)5 : (byte)3;

        string snapshotListWql = $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'";
        var existingResponse = await WmiApi.QueryAsync(snapshotListWql, obj => obj["InstanceID"]?.ToString() ?? "");
        var existingIds = (existingResponse.Data ?? new List<string>()).ToHashSet();

        BitmapSource? bitmap = externalThumb ?? await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
        string snapshotName = $"{vmName} - ({DateTime.Now:yyyy/M/d - HH:mm:ss})";

        try
        {
            // 临时切换快照类型
            await WmiApi.WithObjectAsync(
                settingsWql,
                obj => obj["UserSnapshotType"] = targetTypeCode);

            // 构造快照设置 XML
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var snapClass = new ManagementClass(ms, new ManagementPath("Msvm_VirtualSystemSettingData"), null);
            using var snapSettings = snapClass.CreateInstance();
            snapSettings["ElementName"] = snapshotName;
            string snapXml = snapSettings.GetText(TextFormat.CimDtd20);

            // 创建快照
            // SnapshotType: 2=Standard, 3=Production
            byte snapType = mode == SpacetimeMode.Continuous ? (byte)2 : (byte)3;
            await WmiApi.InvokeAsync(
                SnapshotServiceWql,
                "CreateSnapshot",
                p =>
                {
                    p["AffectedSystem"] = vm.Path;
                    p["SnapshotSettings"] = snapXml;
                    p["SnapshotType"] = snapType;
                });
        }
        catch (Exception ex)
        {
            // 恢复原始快照类型
            await WmiApi.WithObjectAsync(settingsWql, obj => obj["UserSnapshotType"] = originalTypeCode);
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrSpacetimeCollapse, ex.Message));
        }
        finally
        {
            await WmiApi.WithObjectAsync(settingsWql, obj => obj["UserSnapshotType"] = originalTypeCode);
        }

        // 等待新快照出现
        string? newId = null;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(200);
            var currentIds = await WmiApi.QueryAsync(snapshotListWql, obj => obj["InstanceID"]?.ToString() ?? "");
            newId = currentIds.Data?.FirstOrDefault(id => !existingIds.Contains(id));
            if (newId != null) break;
        }

        if (newId != null && bitmap != null)
        {
            string? snapshotDir = await GetSnapshotDirectoryByGuidAsync(vm.Guid);
            if (!string.IsNullOrEmpty(snapshotDir))
                await SaveThumbnailToDisk(bitmap, snapshotDir, newId);
        }

        return (true, Properties.Resources.VmSpacetimeService_MsgAnchorSet);
    }

    public async Task<(bool Success, string Message)> AnnihilateAsync(string vmName, SpacetimeNode node)
    {
        if (node.IsLogicalNode)
            return (false, Properties.Resources.VmSpacetimeService_ErrOriginCurrentNoAnnihilate);

        var result = await WmiApi.InvokeAsync(
            SnapshotServiceWql, "DestroySnapshotTree",
            p => p["SnapshotSettingData"] = node.Path);

        if (result.Success)
        {
            string? dir = await GetSnapshotDirectoryAsync(vmName);
            if (!string.IsNullOrEmpty(dir)) DeleteThumbnailFile(dir, node.Id);
            return (true, Properties.Resources.VmSpacetimeService_MsgAnnihilated);
        }
        return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrAnnihilateFailed, result.Error));
    }

    public async Task<(bool Success, string Message)> ConvergeAsync(string vmName, SpacetimeNode node)
    {
        if (node.IsLogicalNode)
            return (false, Properties.Resources.VmSpacetimeService_ErrOriginCurrentNoConverge);

        var result = await WmiApi.InvokeAsync(
            SnapshotServiceWql, "DestroySnapshot",
            p => p["AffectedSnapshot"] = node.Path);

        if (result.Success)
        {
            string? dir = await GetSnapshotDirectoryAsync(vmName);
            if (!string.IsNullOrEmpty(dir)) DeleteThumbnailFile(dir, node.Id);
            return (true, Properties.Resources.VmSpacetimeService_MsgConverging);
        }
        return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrConvergeFailed, result.Error));
    }

    public async Task<bool> GetCheckpointsEnabledAsync(string vmName)
    {
        try
        {
            var guidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString());
            if (!guidResponse.HasData) return true;

            var typeResponse = await WmiApi.QueryFirstAsync(
                $"SELECT UserSnapshotType FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{guidResponse.Data}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["UserSnapshotType"]?.ToString());

            return typeResponse.Data != "2";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmSpacetimeService_LogReadCheckpointFailed, ex.Message));
            return true;
        }
    }

    public async Task<(bool Success, string Message)> SetCheckpointsEnabledAsync(string vmName, bool enabled)
    {
        try
        {
            var guidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString());
            if (!guidResponse.HasData)
                return (false, Properties.Resources.VmSpacetimeService_ErrVmNotFound);

            string settingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{guidResponse.Data}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

            var result = await WmiApi.WithObjectAsync(
                settingsWql,
                obj => obj["UserSnapshotType"] = enabled ? (byte)3 : (byte)2);

            return result.Success
                ? (true, enabled
                    ? Properties.Resources.VmSpacetimeService_MsgCheckpointEnabled
                    : Properties.Resources.VmSpacetimeService_MsgCheckpointDisabled)
                : (false, string.Format(Properties.Resources.VmSpacetimeService_ErrOperationWmiCode, result.Error));
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmSpacetimeService_ErrOperationException, ex.Message));
        }
    }

    // ============================================================
    // WMI 磁盘操作辅助方法
    // ============================================================

    /// <summary>获取虚拟机所有 SCSI 磁盘的文件路径。</summary>
    private async Task<List<string>> GetVmScsiDiskPathsAsync(string vmGuid)
    {
        var response = await WmiApi.QueryAsync(
            $"SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty);

        return (response.Data ?? new List<string>())
            .Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    /// <summary>获取指定磁盘路径对应的控制器槽位信息。</summary>
    private async Task<(string CtrlType, int CtrlNum, int CtrlLoc)> GetDiskSlotInfoAsync(
        string vmGuid, string diskPath)
    {
        // 找到 SASD（ResourceType=31）的 HostResource 匹配的对象
        var sasdResponse = await WmiApi.QueryAsync(
            $"SELECT InstanceID, Parent, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => new
            {
                InstanceID = obj["InstanceID"]?.ToString() ?? "",
                Parent = obj["Parent"]?.ToString() ?? "",
                HostResource = (obj["HostResource"] as string[])?.FirstOrDefault() ?? ""
            });

        var sasd = sasdResponse.Data?.FirstOrDefault(d =>
            d.HostResource.Equals(diskPath, StringComparison.OrdinalIgnoreCase));

        if (sasd == null) return ("SCSI", 0, 0);

        // Parent 指向 RASD（ResourceType=17，磁盘槽位）
        // 从 Parent 路径提取 InstanceID
        var parentIdMatch = Regex.Match(sasd.Parent, @"InstanceID=""([^""]+)""", RegexOptions.IgnoreCase);
        if (!parentIdMatch.Success) return ("SCSI", 0, 0);

        string parentId = parentIdMatch.Groups[1].Value.Replace("\\\\", "\\");
        string escapedParentId = parentId.Replace("\\", "\\\\").Replace("'", "\\'");

        var rasdResponse = await WmiApi.QueryFirstAsync(
            $"SELECT AddressOnParent, Parent FROM Msvm_ResourceAllocationSettingData WHERE InstanceID = '{escapedParentId}'",
            obj => new
            {
                Location = Convert.ToInt32(obj["AddressOnParent"] ?? 0),
                Parent = obj["Parent"]?.ToString() ?? ""
            });

        if (!rasdResponse.HasData) return ("SCSI", 0, 0);

        int ctrlLoc = rasdResponse.Data!.Location;

        // Parent 指向控制器（ResourceType=6，SCSI）
        var ctrlIdMatch = Regex.Match(rasdResponse.Data.Parent, @"InstanceID=""([^""]+)""", RegexOptions.IgnoreCase);
        if (!ctrlIdMatch.Success) return ("SCSI", 0, ctrlLoc);

        string ctrlId = ctrlIdMatch.Groups[1].Value.Replace("\\\\", "\\");
        string escapedCtrlId = ctrlId.Replace("\\", "\\\\").Replace("'", "\\'");

        var ctrlResponse = await WmiApi.QueryFirstAsync(
            $"SELECT Address FROM Msvm_ResourceAllocationSettingData WHERE InstanceID = '{escapedCtrlId}'",
            obj => Convert.ToInt32(obj["Address"] ?? 0));

        return ("SCSI", ctrlResponse.Data, ctrlLoc);
    }

    /// <summary>添加虚拟磁盘到虚拟机。替换 Add-VMHardDiskDrive。</summary>
    private async Task<ApiResponse> AddVmHardDiskDriveAsync(
        string vmName, string ctrlType, int ctrlNum, int ctrlLoc, string vhdPath)
    {
        using var vm = WmiApi.GetVmComputerSystem(vmName);
        if (vm == null) return ApiResponse.Fail("VM not found");

        var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

        // 找到对应控制器
        string vmGuid = vm["Name"]?.ToString() ?? "";
        var ctrlResponse = await WmiApi.QueryAsync(
            $"SELECT InstanceID FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 6 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => obj["InstanceID"]?.ToString() ?? "");

        var controllers = ctrlResponse.Data ?? new List<string>();
        if (ctrlNum >= controllers.Count) return ApiResponse.Fail("Controller not found");

        string ctrlInstanceId = controllers[ctrlNum];

        // 构造磁盘槽位 RASD
        using var rasdClass = new ManagementClass(ms, new ManagementPath("Msvm_ResourceAllocationSettingData"), null);
        using var rasd = rasdClass.CreateInstance();
        rasd["ResourceType"] = (ushort)17;
        rasd["ResourceSubType"] = "Microsoft:Hyper-V:Disk Drive";
        rasd["Parent"] = $"\\\\{Environment.MachineName}\\root\\virtualization\\v2:Msvm_ResourceAllocationSettingData.InstanceID=\"{ctrlInstanceId.Replace("\\", "\\\\")}\"";
        rasd["AddressOnParent"] = ctrlLoc.ToString();

        var rasdResult = await WmiApi.InvokeAsync(
            ManagementServiceWql, "AddResourceSettings",
            p =>
            {
                p["AffectedConfiguration"] = vm.Path.Path;
                p["ResourceSettings"] = new string[] { rasd.GetText(TextFormat.CimDtd20) };
            });

        if (!rasdResult.Success) return rasdResult;

        // 找到刚创建的槽位，挂载 VHDX
        await Task.Delay(300);
        var newSlotResponse = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 17 AND InstanceID LIKE 'Microsoft:{vmGuid}%' AND AddressOnParent = '{ctrlLoc}'",
            obj => obj["InstanceID"]?.ToString() ?? "");

        if (!newSlotResponse.HasData) return ApiResponse.Fail("Slot not found after creation");

        string slotId = newSlotResponse.Data!;
        string escapedSlotId = slotId.Replace("\\", "\\\\");

        using var sasdClass = new ManagementClass(ms, new ManagementPath("Msvm_StorageAllocationSettingData"), null);
        using var sasd = sasdClass.CreateInstance();
        sasd["ResourceType"] = (ushort)31;
        sasd["ResourceSubType"] = "Microsoft:Hyper-V:Virtual Hard Disk";
        sasd["Parent"] = $"\\\\{Environment.MachineName}\\root\\virtualization\\v2:Msvm_ResourceAllocationSettingData.InstanceID=\"{escapedSlotId}\"";
        sasd["HostResource"] = new string[] { vhdPath };

        return await WmiApi.InvokeAsync(
            ManagementServiceWql, "AddResourceSettings",
            p =>
            {
                p["AffectedConfiguration"] = vm.Path.Path;
                p["ResourceSettings"] = new string[] { sasd.GetText(TextFormat.CimDtd20) };
            });
    }

    /// <summary>从虚拟机移除虚拟磁盘。替换 Remove-VMHardDiskDrive。</summary>
    private async Task<ApiResponse> RemoveVmHardDiskDriveAsync(
        string vmName, string ctrlType, int ctrlNum, int ctrlLoc)
    {
        using var vm = WmiApi.GetVmComputerSystem(vmName);
        if (vm == null) return ApiResponse.Fail("VM not found");

        string vmGuid = vm["Name"]?.ToString() ?? "";

        // 找到对应 SASD 对象路径
        var sasdResponse = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID, Parent FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => obj.Path.Path);

        if (!sasdResponse.HasData) return ApiResponse.Fail("Disk not found");

        return await WmiApi.InvokeAsync(
            ManagementServiceWql, "RemoveResourceSettings",
            p => p["ResourceSettings"] = new string[] { sasdResponse.Data! });
    }

    // ============================================================
    // 空闲槽位查找
    // ============================================================

    private async Task<(string, int, int)> FindFreeScsiSlotAsync(string vmName)
    {
        try
        {
            using var vm = WmiApi.GetVmComputerSystem(vmName);
            if (vm == null) return ("SCSI", -1, -1);
            string vmGuid = vm["Name"]?.ToString() ?? "";

            // 获取所有已用的 SCSI 槽位
            var usedResponse = await WmiApi.QueryAsync(
                $"SELECT Parent, AddressOnParent FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 17 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
                obj => $"{obj["Parent"]}_{obj["AddressOnParent"]}");

            var usedRaw = usedResponse.Data ?? new List<string>();

            // 获取 SCSI 控制器列表
            var ctrlResponse = await WmiApi.QueryAsync(
                $"SELECT InstanceID FROM Msvm_ResourceAllocationSettingData WHERE ResourceType = 6 AND InstanceID LIKE 'Microsoft:{vmGuid}%'",
                obj => obj["InstanceID"]?.ToString() ?? "");

            var controllers = ctrlResponse.Data ?? new List<string>();

            for (int c = 0; c < controllers.Count; c++)
            {
                for (int l = 0; l < 64; l++)
                {
                    bool used = usedRaw.Any(u => u.Contains(controllers[c]) && u.EndsWith($"_{l}"));
                    if (!used) return ("SCSI", c, l);
                }
            }

            return ("SCSI", -1, -1);
        }
        catch { return ("SCSI", -1, -1); }
    }

    private async Task<bool> CheckAnyWormholeExistsAsync(string vmName)
    {
        try
        {
            var guidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString());
            if (!guidResponse.HasData) return false;

            var paths = await GetVmScsiDiskPathsAsync(guidResponse.Data!);
            return paths.Any(p => p.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private async Task<bool> IsNodeInCurrentChainAsync(string vmName, string targetVhdPath)
    {
        try
        {
            var guidResponse = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString());
            if (!guidResponse.HasData) return false;

            var paths = await GetVmScsiDiskPathsAsync(guidResponse.Data!);
            string? cur = paths.FirstOrDefault(p =>
                !p.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(cur)) return false;

            for (int i = 0; i < 20; i++)
            {
                string curNorm = cur.Replace("_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(curNorm, targetVhdPath, StringComparison.OrdinalIgnoreCase))
                    return true;
                string parent = await GetVhdParentPathAsync(cur);
                if (string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }
            return false;
        }
        catch { return false; }
    }

    // ============================================================
    // 底层辅助（保留 WMI 实现）
    // ============================================================

    private async Task<(bool Success, string Message)> CreateDifferencingDiskAsync(
        string newPath, string parentPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var imgSearcher = new ManagementObjectSearcher(ms,
                    new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
                using var imgCol = imgSearcher.Get();
                using var svcInst = imgCol.Cast<ManagementObject>().FirstOrDefault();
                if (svcInst == null)
                    return (false, Properties.Resources.VmSpacetimeService_ErrWmiImageSvcNotFound);

                using var settingClass = new ManagementClass(ms,
                    new ManagementPath("Msvm_VirtualHardDiskSettingData"), null);
                using var setting = settingClass.CreateInstance();
                setting["Type"] = 4;
                setting["Format"] = 3;
                setting["Path"] = newPath;
                setting["ParentPath"] = parentPath;
                setting["BlockSize"] = 0u;
                setting["LogicalSectorSize"] = 0u;
                setting["PhysicalSectorSize"] = 0u;
                setting["MaxInternalSize"] = 0ul;

                using var inParams = svcInst.GetMethodParameters("CreateVirtualHardDisk");
                inParams["VirtualDiskSettingData"] = setting.GetText(TextFormat.WmiDtd20);
                using var outParams = svcInst.InvokeMethod("CreateVirtualHardDisk", inParams, null);

                uint ret = (uint)outParams["ReturnValue"];
                if (ret == 0) return (true, "");
                if (ret == 4096)
                {
                    using var job = new ManagementObject(ms, new ManagementPath(outParams["Job"].ToString()), null);
                    while (true)
                    {
                        job.Get();
                        if ((ushort)job["JobState"] >= 7) break;
                        Thread.Sleep(200);
                    }
                    ushort err = (ushort)job["ErrorCode"];
                    return err == 0 ? (true, "") : (false, job["ErrorDescription"]?.ToString() ?? "");
                }
                return (false, string.Format(Properties.Resources.VmSpacetimeService_LogWmiReturned, ret));
            }
            catch (Exception ex) { return (false, ex.Message); }
        });
    }

    private async Task<string> GetVhdParentPathAsync(string vhdPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var searcher = new ManagementObjectSearcher(ms,
                    new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
                using var col = searcher.Get();
                using var svcInst = col.Cast<ManagementObject>().FirstOrDefault();
                if (svcInst == null) return string.Empty;

                using var inParams = svcInst.GetMethodParameters("GetVirtualHardDiskSettingData");
                inParams["Path"] = vhdPath;
                using var outParams = svcInst.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);

                string xml = outParams["SettingData"]?.ToString() ?? string.Empty;
                var match = Regex.Match(xml,
                    @"<PROPERTY NAME=""ParentPath"" TYPE=""string"">\s*<VALUE>(.*?)</VALUE>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            }
            catch { return string.Empty; }
        });
    }

    private string TraceToGenesisPath(string childPath)
    {
        string currentPath = childPath;
        var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
        using var searcher = new ManagementObjectSearcher(ms,
            new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
        using var col = searcher.Get();
        using var imgInst = col.Cast<ManagementObject>().FirstOrDefault();
        if (imgInst == null) return childPath;

        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var inParams = imgInst.GetMethodParameters("GetVirtualHardDiskSettingData");
                inParams["Path"] = currentPath;
                using var outParams = imgInst.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
                string xml = outParams["SettingData"]?.ToString() ?? string.Empty;
                var match = Regex.Match(xml,
                    @"<PROPERTY NAME=""ParentPath"" TYPE=""string"">\s*<VALUE>(.*?)</VALUE>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!match.Success) break;
                string parent = match.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(parent)) break;
                if (!Path.IsPathRooted(parent))
                    parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentPath)!, parent));
                if (File.Exists(parent)) currentPath = parent; else break;
            }
            catch { break; }
        }
        return currentPath;
    }

    // ============================================================
    // 私有辅助（不改动）
    // ============================================================

    private async Task<string?> GetSnapshotDirectoryAsync(string vmName)
    {
        try
        {
            var response = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = (SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}') AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["ConfigurationDataRoot"]?.ToString());
            string? root = response.Data;
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Snapshots");
        }
        catch { return null; }
    }

    private async Task<string?> GetSnapshotDirectoryByGuidAsync(string vmGuid)
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

    private void DeleteThumbnailFile(string snapshotDir, string nodeId)
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
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(
                Properties.Resources.VmSpacetimeService_LogCleanScreenFailed, ex.Message));
        }
    }

    private async Task<List<SpacetimeNode>> CreateInitialSpacetimeAsync(string vmName, string snapshotDir)
    {
        var thumb = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
        if (thumb != null && !string.IsNullOrEmpty(snapshotDir))
            await SaveThumbnailToDisk(thumb, snapshotDir, SpacetimeNode.GenesisId);

        return new List<SpacetimeNode>
        {
            new() { Id = SpacetimeNode.GenesisId, Name = Properties.Resources.VmSpacetimeService_NodeLabelOrigin,  NodeType = SpacetimeNodeType.Genesis,  IsCurrent = true, CreatedDate = DateTime.Now.AddMinutes(-1), Thumbnail = thumb },
            new() { Id = SpacetimeNode.CurrentId, Name = Properties.Resources.VmSpacetimeService_NodeLabelCurrent, NodeType = SpacetimeNodeType.Current, ParentId = SpacetimeNode.GenesisId, IsCurrent = true, CreatedDate = DateTime.Now, Thumbnail = thumb }
        };
    }

    private BitmapSource? LoadThumbnailFromDisk(string snapshotDir, string id)
    {
        try
        {
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(id)}.jpg");
            if (!File.Exists(filePath)) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private async Task SaveThumbnailToDisk(BitmapSource bitmap, string snapshotDir, string id)
    {
        try
        {
            if (!Directory.Exists(snapshotDir)) Directory.CreateDirectory(snapshotDir);
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(id)}.jpg");
            await Task.Run(() =>
            {
                using var stream = new FileStream(filePath, FileMode.Create);
                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            });
        }
        catch { }
    }

    private string? ExtractInstanceId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var match = Regex.Match(path, "InstanceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}