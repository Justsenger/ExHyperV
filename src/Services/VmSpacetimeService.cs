using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal class VmSpacetimeService
{
    private const string SnapshotServiceWql = "SELECT * FROM Msvm_VirtualSystemSnapshotService";
    private readonly VmPowerService _powerService = new();

    private string GetSafeId(string id) => id.Replace(":", "_");

    // ============================================================
    // 时空节点查询（含虫洞检测）
    // ============================================================

    public async Task<List<SpacetimeNode>> GetSpacetimeNodesAsync(string vmName)
    {
        try
        {
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
            var vmData = await WmiTools.QueryAsync(vmWql, obj => new {
                Guid = obj["Name"]?.ToString(),
                Path = obj.Path.ToString()
            });

            var vm = vmData.FirstOrDefault();
            if (vm == null || string.IsNullOrEmpty(vm.Guid)) return new List<SpacetimeNode>();

            string configRootWql = $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var configData = await WmiTools.QueryAsync(configRootWql, obj => obj["ConfigurationDataRoot"]?.ToString());
            string configRoot = configData.FirstOrDefault() ?? string.Empty;
            string snapshotDir = Path.Combine(configRoot, "Snapshots");

            string settingWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}'";
            var allRawNodes = await WmiTools.QueryAsync(settingWql, obj => new SpacetimeNode
            {
                Id = obj["InstanceID"]?.ToString() ?? "",
                Name = obj["ElementName"]?.ToString() ?? "",
                CreatedDate = obj["CreationTime"] != null ? ManagementDateTimeConverter.ToDateTime(obj["CreationTime"].ToString()) : DateTime.MinValue,
                ParentId = ExtractInstanceId(obj["Parent"]?.ToString()),
                VirtualSystemType = obj["VirtualSystemType"]?.ToString() ?? "",
                Path = obj.Path.ToString()
            });

            if (!allRawNodes.Any()) return await CreateInitialSpacetimeAsync(vmName, snapshotDir);

            var snapshots = allRawNodes.Where(n => n.VirtualSystemType.Contains("Snapshot")).ToList();
            var realizedNode = allRawNodes.FirstOrDefault(n => n.VirtualSystemType == "Microsoft:Hyper-V:System:Realized");

            DateTime genesisTime = snapshots.Any() ? snapshots.Min(s => s.CreatedDate).AddMinutes(-1) : DateTime.Now;
            if (realizedNode != null)
            {
                string sasdWql = "SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31";
                var sasdList = await WmiTools.QueryAsync(sasdWql, obj => new {
                    Id = obj["InstanceID"]?.ToString() ?? "",
                    Path = (obj["HostResource"] as string[])?.FirstOrDefault()
                });

                // 填充每个快照节点的磁盘路径（供虫洞检测匹配用）
                foreach (var snap in snapshots)
                {
                    var sasd = sasdList.FirstOrDefault(d => d.Id.Contains(snap.Id));
                    if (sasd?.Path != null)
                    {
                        // 虫洞期间父盘改名为 _renamed.vhdx，还原成原始 .avhdx 路径存储
                        snap.VhdPath = sasd.Path.Replace(
                            "_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
                    }
                }

                string currentVhdPath = sasdList.FirstOrDefault(d => d.Id.Contains(realizedNode.Id))?.Path ?? string.Empty;
                if (!string.IsNullOrEmpty(currentVhdPath))
                {
                    string genesisPath = await Task.Run(() => TraceToGenesisPath(currentVhdPath));
                    if (File.Exists(genesisPath)) genesisTime = File.GetLastWriteTime(genesisPath);
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
                if (genesisThumbnail != null) await SaveThumbnailToDisk(genesisThumbnail, snapshotDir, SpacetimeNode.GenesisId);
            }

            var genesisNode = new SpacetimeNode { Id = SpacetimeNode.GenesisId, Name = "起源", NodeType = SpacetimeNodeType.Genesis, CreatedDate = genesisTime, Thumbnail = genesisThumbnail };
            var currentNode = new SpacetimeNode { Id = SpacetimeNode.CurrentId, Name = "当前", NodeType = SpacetimeNodeType.Current, IsCurrent = true, CreatedDate = DateTime.Now, Thumbnail = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160) };

            foreach (var s in snapshots)
                if (string.IsNullOrEmpty(s.ParentId)) s.ParentId = SpacetimeNode.GenesisId;

            if (realizedNode != null)
            {
                currentNode.ParentId = string.IsNullOrEmpty(realizedNode.ParentId) ? SpacetimeNode.GenesisId : realizedNode.ParentId;
                var anchorNode = snapshots.FirstOrDefault(s => s.Id == realizedNode.ParentId);
                if (anchorNode != null) anchorNode.IsCurrent = true;
                else if (currentNode.ParentId == SpacetimeNode.GenesisId) genesisNode.IsCurrent = true;
            }

            var result = new List<SpacetimeNode> { genesisNode };
            result.AddRange(snapshots);
            result.Add(currentNode);

            // 虫洞检测：扫描挂载状态，标记对应节点
            await DetectAndMarkWormholeAsync(vmName, result);

            return result;
        }
        catch (Exception ex) { Debug.WriteLine($"时空检索失败: {ex.Message}"); return new List<SpacetimeNode>(); }
    }

    // ============================================================
    // 虫洞检测
    // ============================================================

    private async Task DetectAndMarkWormholeAsync(string vmName, List<SpacetimeNode> nodes)
    {
        try
        {
            string safe = vmName.Replace("'", "''");

            var drives = await Utils.Run2(
                $"Get-VMHardDiskDrive -VMName '{safe}' | " +
                $"Where-Object {{ $_.ControllerType -eq 'SCSI' }} | " +
                $"Select-Object ControllerType, ControllerNumber, ControllerLocation, Path | " +
                $"ConvertTo-Json -Compress");

            string json = string.Join("", drives.Select(o => o?.ToString() ?? "")).Trim();
            Debug.WriteLine($"[Wormhole] SCSI磁盘JSON: {json}");  // ← 看有没有 _wormhole_tmp

            if (string.IsNullOrEmpty(json) || json == "null") { Debug.WriteLine("[Wormhole] 无SCSI磁盘，退出"); return; }
            if (!json.StartsWith("[")) json = $"[{json}]";

            var diskEntries = System.Text.Json.JsonSerializer.Deserialize<List<ScsiDiskEntry>>(json);
            if (diskEntries == null) { Debug.WriteLine("[Wormhole] 反序列化失败"); return; }

            var wormholeDisk = diskEntries.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.Path) &&
                d.Path.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase));

            Debug.WriteLine($"[Wormhole] 虫洞盘: {wormholeDisk?.Path ?? "未找到"}");  // ← 看能不能找到
            if (wormholeDisk == null) return;

            string renamedParentPath = await GetVhdParentPathAsync(wormholeDisk.Path);
            Debug.WriteLine($"[Wormhole] 父盘路径: {renamedParentPath}");  // ← 看父盘路径对不对

            string originalAvhdxPath = renamedParentPath.Replace(
                "_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
            Debug.WriteLine($"[Wormhole] 还原路径: {originalAvhdxPath}");

            // 打印所有快照节点的 VhdPath
            foreach (var n in nodes.Where(n => n.NodeType == SpacetimeNodeType.Snapshot))
                Debug.WriteLine($"[Wormhole] 节点 [{n.Name}] VhdPath={n.VhdPath}");

            var targetNode = nodes.FirstOrDefault(n =>
                n.NodeType == SpacetimeNodeType.Snapshot &&
                !string.IsNullOrEmpty(n.VhdPath) &&
                (string.Equals(n.VhdPath, originalAvhdxPath, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n.VhdPath, renamedParentPath, StringComparison.OrdinalIgnoreCase)));

            Debug.WriteLine($"[Wormhole] 匹配节点: {targetNode?.Name ?? "未匹配"}");  // ← 最关键
            if (targetNode == null) return;

            targetNode.IsWormhole = true;
            targetNode.WormholeTmpDiskPath = wormholeDisk.Path;
            targetNode.WormholeRenamedPath = renamedParentPath;
            targetNode.WormholeCtrlType = wormholeDisk.ControllerTypeString;
            targetNode.WormholeCtrlNum = wormholeDisk.ControllerNumber;
            targetNode.WormholeCtrlLoc = wormholeDisk.ControllerLocation;
            Debug.WriteLine($"[Wormhole] 标记成功: {targetNode.Name}");
        }
        catch (Exception ex) { Debug.WriteLine($"[Wormhole] 检测失败: {ex.Message}"); }
    }
    private async Task<string> GetVhdParentPathAsync(string vhdPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var imgSvc = new ManagementObjectSearcher(
                    @"root\virtualization\v2", "SELECT * FROM Msvm_ImageManagementService");
                using var svcInst = imgSvc.Get().Cast<ManagementObject>().FirstOrDefault();
                if (svcInst == null) return string.Empty;

                var inParams = svcInst.GetMethodParameters("GetVirtualHardDiskSettingData");
                inParams["Path"] = vhdPath;
                var outParams = svcInst.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);

                string xml = outParams["SettingData"]?.ToString() ?? string.Empty;
                var match = Regex.Match(xml,
                    @"<PROPERTY NAME=""ParentPath"" TYPE=""string"">\s*<VALUE>(.*?)</VALUE>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            }
            catch { return string.Empty; }
        });
    }

    private class ScsiDiskEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("ControllerType")]
        public int ControllerType { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ControllerNumber")]
        public int ControllerNumber { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("ControllerLocation")]
        public int ControllerLocation { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("Path")]
        public string Path { get; set; } = string.Empty;

        public string ControllerTypeString => ControllerType == 1 ? "SCSI" : "IDE";
    }

    // ============================================================
    // 虫洞开启
    // ============================================================

    public async Task<(bool Success, string Message)> OpenWormholeAsync(string vmName, SpacetimeNode targetNode)
    {
        if (await IsNodeInCurrentChainAsync(vmName, targetNode.VhdPath))
            return (false, "该节点是当前时空的父链节点，开启虫洞会导致时空悖论");
        if (targetNode.NodeType != SpacetimeNodeType.Snapshot) return (false, "只能对快照节点开启虫洞");
        if (string.IsNullOrEmpty(targetNode.VhdPath)) return (false, "快照磁盘路径无效");
        if (targetNode.IsWormhole) return (false, "该节点已有虫洞开启中");

        string diskDir = Path.GetDirectoryName(targetNode.VhdPath) ?? "";
        string originalAvhdx = targetNode.VhdPath;
        string renamedVhdx = originalAvhdx.Replace(".avhdx", "_renamed.vhdx", StringComparison.OrdinalIgnoreCase);
        string tmpDisk = Path.Combine(diskDir, "_wormhole_tmp.vhdx");

        if (string.IsNullOrEmpty(diskDir)) return (false, "无法确定磁盘目录");
        if (File.Exists(tmpDisk)) File.Delete(tmpDisk);

        var (ctrlType, ctrlNum, ctrlLoc) = await FindFreeScsiSlotAsync(vmName);
        if (ctrlNum == -1) return (false, "没有可用的 SCSI 插槽");

        // 临时改名绕过 WMI .avhdx 扩展名限制
        File.Move(originalAvhdx, renamedVhdx);

        try
        {
            var createResult = await CreateDifferencingDiskAsync(tmpDisk, renamedVhdx);
            if (!createResult.Success)
            {
                File.Move(renamedVhdx, originalAvhdx);
                return (false, $"创建临时差分盘失败: {createResult.Message}");
            }

            string safe = vmName.Replace("'", "''");
            await Utils.Run2(
                $"Add-VMHardDiskDrive -VMName '{safe}' " +
                $"-ControllerType {ctrlType} -ControllerNumber {ctrlNum} -ControllerLocation {ctrlLoc} " +
                $"-Path '{tmpDisk}' -ErrorAction Stop");

            targetNode.IsWormhole = true;
            targetNode.WormholeTmpDiskPath = tmpDisk;
            targetNode.WormholeRenamedPath = renamedVhdx;
            targetNode.WormholeCtrlType = ctrlType;
            targetNode.WormholeCtrlNum = ctrlNum;
            targetNode.WormholeCtrlLoc = ctrlLoc;

            return (true, "虫洞已开启");
        }
        catch (Exception ex)
        {
            if (File.Exists(tmpDisk)) File.Delete(tmpDisk);
            if (File.Exists(renamedVhdx) && !File.Exists(originalAvhdx))
                File.Move(renamedVhdx, originalAvhdx);
            return (false, $"时空异常: {ex.Message}");
        }
    }


    private async Task<bool> IsNodeInCurrentChainAsync(string vmName, string targetVhdPath)
    {
        try
        {
            // 获取当前运行盘路径
            string safe = vmName.Replace("'", "''");
            var currentDisks = await Utils.Run2(
                $"Get-VMHardDiskDrive -VMName '{safe}' | " +
                $"Where-Object {{ $_.ControllerType -ne 'SCSI' -or $_.Path -notlike '*_wormhole_tmp*' }} | " +
                $"Select-Object -ExpandProperty Path");

            string? currentDiskPath = currentDisks
                .Select(o => o?.ToString() ?? "")
                .FirstOrDefault(p => !string.IsNullOrEmpty(p) && !p.Contains("_wormhole_tmp"));

            if (string.IsNullOrEmpty(currentDiskPath)) return false;

            // 沿父链向上追溯，看有没有命中目标路径
            string cur = currentDiskPath;
            for (int i = 0; i < 20; i++)
            {
                // 把 _renamed.vhdx 也还原成 .avhdx 再比较
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
    // 虫洞关闭
    // ============================================================

    public async Task<(bool Success, string Message)> CloseWormholeAsync(string vmName, SpacetimeNode node)
    {
        if (!node.IsWormhole) return (false, "该节点没有开启虫洞");

        try
        {
            string safe = vmName.Replace("'", "''");

            await Utils.Run2(
                $"Remove-VMHardDiskDrive -VMName '{safe}' " +
                $"-ControllerType {node.WormholeCtrlType} " +
                $"-ControllerNumber {node.WormholeCtrlNum} " +
                $"-ControllerLocation {node.WormholeCtrlLoc} " +
                $"-ErrorAction Stop");

            if (File.Exists(node.WormholeTmpDiskPath))
                File.Delete(node.WormholeTmpDiskPath);

            string originalAvhdx = node.WormholeRenamedPath.Replace(
                "_renamed.vhdx", ".avhdx", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(node.WormholeRenamedPath) && !File.Exists(originalAvhdx))
                File.Move(node.WormholeRenamedPath, originalAvhdx);

            node.IsWormhole = false;
            node.WormholeTmpDiskPath = string.Empty;
            node.WormholeRenamedPath = string.Empty;

            return (true, "虫洞已关闭，时间线恢复正常");
        }
        catch (Exception ex) { return (false, $"关闭虫洞异常: {ex.Message}"); }
    }

    // ============================================================
    // 底层辅助
    // ============================================================

    private async Task<(bool Success, string Message)> CreateDifferencingDiskAsync(string newPath, string parentPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var imgSvc = new ManagementObjectSearcher(
                    @"root\virtualization\v2", "SELECT * FROM Msvm_ImageManagementService");
                using var svcInst = imgSvc.Get().Cast<ManagementObject>().FirstOrDefault();
                if (svcInst == null) return (false, "找不到 WMI ImageManagementService");

                using var settingClass = new ManagementClass(
                    @"root\virtualization\v2", "Msvm_VirtualHardDiskSettingData", null);
                using var setting = settingClass.CreateInstance();
                setting["Type"] = 4;
                setting["Format"] = 3;
                setting["Path"] = newPath;
                setting["ParentPath"] = parentPath;
                setting["BlockSize"] = 0u;
                setting["LogicalSectorSize"] = 0u;
                setting["PhysicalSectorSize"] = 0u;
                setting["MaxInternalSize"] = 0ul;

                var inParams = svcInst.GetMethodParameters("CreateVirtualHardDisk");
                inParams["VirtualDiskSettingData"] = setting.GetText(TextFormat.WmiDtd20);

                var outParams = svcInst.InvokeMethod("CreateVirtualHardDisk", inParams, null);
                uint ret = (uint)outParams["ReturnValue"];
                if (ret == 0) return (true, "");

                if (ret == 4096)
                {
                    using var job = new ManagementObject(outParams["Job"].ToString());
                    while (true)
                    {
                        job.Get();
                        if ((ushort)job["JobState"] >= 7) break;
                        System.Threading.Thread.Sleep(200);
                    }
                    ushort err = (ushort)job["ErrorCode"];
                    return err == 0 ? (true, "") : (false, job["ErrorDescription"]?.ToString() ?? "");
                }
                return (false, $"WMI 返回: {ret}");
            }
            catch (Exception ex) { return (false, ex.Message); }
        });
    }

    private async Task<(string, int, int)> FindFreeScsiSlotAsync(string vmName)
    {
        try
        {
            string safe = vmName.Replace("'", "''");
            var hardDisks = await Utils.Run2(
                $"Get-VMHardDiskDrive -VMName '{safe}' | Where-Object {{$_.ControllerType -eq 'SCSI'}} | " +
                $"ForEach-Object {{\"$($_.ControllerNumber)_$($_.ControllerLocation)\"}}");
            var dvdDrives = await Utils.Run2(
                $"Get-VMDvdDrive -VMName '{safe}' | Where-Object {{$_.ControllerType -eq 'SCSI'}} | " +
                $"ForEach-Object {{\"$($_.ControllerNumber)_$($_.ControllerLocation)\"}}");

            var used = hardDisks.Select(o => o?.ToString() ?? "")
                .Concat(dvdDrives.Select(o => o?.ToString() ?? ""))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();

            for (int c = 0; c < 4; c++)
                for (int l = 0; l < 64; l++)
                    if (!used.Contains($"{c}_{l}"))
                        return ("SCSI", c, l);

            return ("SCSI", -1, -1);
        }
        catch { return ("SCSI", -1, -1); }
    }

    // ============================================================
    // 原有方法（保持不变）
    // ============================================================

    public async Task<(bool Success, string Message)> RenameSnapshotAsync(string snapshotPath, string newName)
    {
        try
        {
            using var snapshotObj = new ManagementObject(snapshotPath);
            snapshotObj.Get();
            snapshotObj["ElementName"] = newName;

            using var svc = new ManagementObjectSearcher(@"root\virtualization\v2",
                "SELECT * FROM Msvm_VirtualSystemManagementService");
            using var svcInst = svc.Get().Cast<ManagementObject>().FirstOrDefault();
            if (svcInst == null) return (false, "找不到 WMI 管理服务");

            var inParams = svcInst.GetMethodParameters("ModifySystemSettings");
            inParams["SystemSettings"] = snapshotObj.GetText(TextFormat.WmiDtd20);
            var outParams = svcInst.InvokeMethod("ModifySystemSettings", inParams, null);
            uint returnValue = (uint)outParams["ReturnValue"];

            return (returnValue == 0 || returnValue == 4096)
                ? (true, "名称已成功更新")
                : (false, $"更新失败: WMI 错误代码: {returnValue}");
        }
        catch (Exception ex) { return (false, $"重命名服务异常: {ex.Message}"); }
    }

    public async Task<(bool Success, string Message)> TeleportAsync(SpacetimeNode node, string vmName)
    {
        if (node.NodeType != SpacetimeNodeType.Snapshot) return (false, "只能穿梭至历史快照点");

        try
        {
            string vmWql = $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
            var vmStateData = await WmiTools.QueryAsync(vmWql, obj => (ushort)obj["EnabledState"]);
            ushort initialState = vmStateData.FirstOrDefault();
            bool shouldRestartAfter = (initialState == 2 || initialState == 32768);

            if (initialState != 3 && initialState != 6)
            {
                await _powerService.ExecuteControlActionAsync(vmName, "Save");
                int attempts = 0;
                while (attempts < 30)
                {
                    await Task.Delay(300);
                    var check = await WmiTools.QueryAsync(vmWql, obj => (ushort)obj["EnabledState"]);
                    if (check.FirstOrDefault() == 6) break;
                    attempts++;
                }
            }

            var parameters = new Dictionary<string, object> { { "Snapshot", node.Path } };
            var result = await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "ApplySnapshot", parameters);

            if (result.Success || result.Message == "4096")
            {
                if (shouldRestartAfter)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        for (int i = 0; i < 10; i++)
                        {
                            var sData = await WmiTools.QueryAsync(vmWql, obj => (ushort)obj["EnabledState"]);
                            ushort s = sData.FirstOrDefault();
                            if (s == 3 || s == 6) { await _powerService.ExecuteControlActionAsync(vmName, "Start"); break; }
                            await Task.Delay(1000);
                        }
                    });
                    return (true, "穿梭已启动，正在恢复运行状态...");
                }
                return (true, "穿梭成功，虚拟机已回到指定时空点（已关机）");
            }
            return (false, $"穿梭失败: {result.Message}");
        }
        catch (Exception ex) { return (false, $"时空异常: {ex.Message}"); }
    }

    public async Task<(bool Success, string Message)> CaptureMomentAsync(
        string vmName, SpacetimeMode mode, BitmapSource? externalThumb = null)
    {
        string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
        var vmData = await WmiTools.QueryAsync(vmWql, obj => new {
            Path = obj.Path.ToString(),
            Guid = obj["Name"].ToString()
        });
        var vm = vmData.FirstOrDefault();
        if (vm == null) return (false, "找不到指定的虚拟机载体");

        string originalType = "Production";
        try
        {
            string settingsWql = $"SELECT UserSnapshotType FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var settingsData = await WmiTools.QueryAsync(settingsWql, obj => obj["UserSnapshotType"]?.ToString());
            originalType = settingsData.FirstOrDefault() == "5" ? "Standard" : "Production";
        }
        catch { }

        string snapshotListWql = $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'";
        var existingIds = (await WmiTools.QueryAsync(snapshotListWql, obj => obj["InstanceID"].ToString())).ToHashSet();
        BitmapSource? bitmap = externalThumb ?? await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
        string targetType = mode == SpacetimeMode.Continuous ? "Standard" : "Production";
        string safe = vmName.Replace("'", "''");
        string snapshotName = $"{vmName} - ({DateTime.Now:yyyy/M/d - HH:mm:ss})";

        try
        {
            await Utils.Run2($"Set-VM -Name '{safe}' -CheckpointType {targetType}");
            await Utils.Run2($"Checkpoint-VM -Name '{safe}' -SnapshotName '{snapshotName}'");
        }
        catch (Exception ex)
        {
            try { await Utils.Run2($"Set-VM -Name '{safe}' -CheckpointType {originalType}"); } catch { }
            return (false, $"时空塌陷: {ex.Message}");
        }
        finally
        {
            try { await Utils.Run2($"Set-VM -Name '{safe}' -CheckpointType {originalType}"); } catch { }
        }

        string? newId = null;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(200);
            var currentIds = await WmiTools.QueryAsync(snapshotListWql, obj => obj["InstanceID"].ToString());
            newId = currentIds.FirstOrDefault(id => !existingIds.Contains(id));
            if (newId != null) break;
        }

        if (newId != null && bitmap != null)
        {
            string? snapshotDir = await GetSnapshotDirectoryByGuidAsync(vm.Guid);
            if (!string.IsNullOrEmpty(snapshotDir))
                await SaveThumbnailToDisk(bitmap, snapshotDir, newId);
        }
        return (true, "时空锚点已锚定");
    }

    public async Task<(bool Success, string Message)> AnnihilateAsync(string vmName, SpacetimeNode node)
    {
        if (node.IsLogicalNode) return (false, "起源与当前节点不可湮灭");
        var parameters = new Dictionary<string, object> { { "SnapshotSettingData", node.Path } };
        var result = await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "DestroySnapshotTree", parameters);
        if (result.Success || result.Message == "4096")
        {
            string? snapshotDir = await GetSnapshotDirectoryAsync(vmName);
            if (!string.IsNullOrEmpty(snapshotDir)) DeleteThumbnailFile(snapshotDir, node.Id);
            return (true, "时空分支已彻底湮灭");
        }
        return (false, $"湮灭失败: {result.Message}");
    }

    public async Task<(bool Success, string Message)> ConvergeAsync(string vmName, SpacetimeNode node)
    {
        if (node.IsLogicalNode) return (false, "起源与当前节点不可收束");
        var parameters = new Dictionary<string, object> { { "AffectedSnapshot", node.Path } };
        var result = await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "DestroySnapshot", parameters);
        if (result.Success || result.Message == "4096")
        {
            string? snapshotDir = await GetSnapshotDirectoryAsync(vmName);
            if (!string.IsNullOrEmpty(snapshotDir)) DeleteThumbnailFile(snapshotDir, node.Id);
            return (true, "时间线收束中...");
        }
        return (false, $"收束失败: {result.Message}");
    }

    public async Task<bool> GetCheckpointsEnabledAsync(string vmName)
    {
        try
        {
            string safeName = vmName.Replace("'", "''");
            string vmWql = $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{safeName}'";
            var vmData = await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString());
            string? vmGuid = vmData.FirstOrDefault();
            if (string.IsNullOrEmpty(vmGuid)) return true;

            string settingsWql = $"SELECT UserSnapshotType FROM Msvm_VirtualSystemSettingData " +
                                 $"WHERE VirtualSystemIdentifier = '{vmGuid}' " +
                                 $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var typeData = await WmiTools.QueryAsync(settingsWql, obj => obj["UserSnapshotType"]?.ToString());
            return typeData.FirstOrDefault() != "2";
        }
        catch (Exception ex) { Debug.WriteLine($"[Spacetime] 读取检查点状态失败: {ex.Message}"); return true; }
    }

    public async Task<(bool Success, string Message)> SetCheckpointsEnabledAsync(string vmName, bool enabled)
    {
        try
        {
            string safeName = vmName.Replace("'", "''");
            string vmWql = $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{safeName}'";
            var vmData = await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString());
            string? vmGuid = vmData.FirstOrDefault();
            if (string.IsNullOrEmpty(vmGuid)) return (false, "找不到虚拟机");

            using var searcher = new ManagementObjectSearcher(@"root\virtualization\v2",
                $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                $"WHERE VirtualSystemIdentifier = '{vmGuid}' " +
                $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'");
            using var settingObj = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (settingObj == null) return (false, "找不到虚拟机配置");

            settingObj["UserSnapshotType"] = enabled ? (byte)3 : (byte)2;

            using var svcSearcher = new ManagementObjectSearcher(@"root\virtualization\v2",
                "SELECT * FROM Msvm_VirtualSystemManagementService");
            using var svcInst = svcSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (svcInst == null) return (false, "找不到 WMI 管理服务");

            var inParams = svcInst.GetMethodParameters("ModifySystemSettings");
            inParams["SystemSettings"] = settingObj.GetText(TextFormat.WmiDtd20);
            var outParams = svcInst.InvokeMethod("ModifySystemSettings", inParams, null);
            uint returnValue = (uint)outParams["ReturnValue"];

            return (returnValue == 0 || returnValue == 4096)
                ? (true, enabled ? "已启用检查点" : "已禁用检查点")
                : (false, $"操作失败: WMI 错误代码 {returnValue}");
        }
        catch (Exception ex) { return (false, $"操作异常: {ex.Message}"); }
    }

    // ============================================================
    // 私有辅助
    // ============================================================

    private void DeleteThumbnailFile(string snapshotDir, string nodeId)
    {
        try
        {
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(nodeId)}.jpg");
            if (File.Exists(filePath)) { File.Delete(filePath); Debug.WriteLine($"[Spacetime] 已清理快照截图: {nodeId}"); }
        }
        catch (Exception ex) { Debug.WriteLine($"[Spacetime] 清理截图失败: {ex.Message}"); }
    }

    private async Task<string?> GetSnapshotDirectoryAsync(string vmName)
    {
        try
        {
            string wql = $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = (SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}') AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var results = await WmiTools.QueryAsync(wql, obj => obj["ConfigurationDataRoot"]?.ToString());
            string? root = results.FirstOrDefault();
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Snapshots");
        }
        catch { return null; }
    }

    private async Task<string?> GetSnapshotDirectoryByGuidAsync(string vmGuid)
    {
        try
        {
            string wql = $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmGuid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var results = await WmiTools.QueryAsync(wql, obj => obj["ConfigurationDataRoot"]?.ToString());
            string? root = results.FirstOrDefault();
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Snapshots");
        }
        catch { return null; }
    }

    private void FindDescendantsRecursive(string parentId, List<SpacetimeNode> allNodes, List<SpacetimeNode> results)
    {
        var children = allNodes.Where(n => n.ParentId == parentId).ToList();
        foreach (var child in children) { results.Add(child); FindDescendantsRecursive(child.Id, allNodes, results); }
    }

    private async Task<List<SpacetimeNode>> CreateInitialSpacetimeAsync(string vmName, string snapshotDir)
    {
        var thumb = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
        if (thumb != null && !string.IsNullOrEmpty(snapshotDir)) await SaveThumbnailToDisk(thumb, snapshotDir, SpacetimeNode.GenesisId);
        return new List<SpacetimeNode>
        {
            new() { Id = SpacetimeNode.GenesisId, Name = "起源", NodeType = SpacetimeNodeType.Genesis, IsCurrent = true, CreatedDate = DateTime.Now.AddMinutes(-1), Thumbnail = thumb },
            new() { Id = SpacetimeNode.CurrentId, Name = "当前", NodeType = SpacetimeNodeType.Current, ParentId = SpacetimeNode.GenesisId, IsCurrent = true, CreatedDate = DateTime.Now, Thumbnail = thumb }
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
            await Task.Run(() => {
                using var fileStream = new FileStream(filePath, FileMode.Create);
                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(fileStream);
            });
        }
        catch { }
    }

    private string TraceToGenesisPath(string childPath)
    {
        string currentPath = childPath;
        using var imgSvc = new ManagementObjectSearcher(@"root\virtualization\v2", "SELECT * FROM Msvm_ImageManagementService");
        using var imgInst = imgSvc.Get().Cast<ManagementObject>().FirstOrDefault();
        if (imgInst == null) return childPath;
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var inParams = imgInst.GetMethodParameters("GetVirtualHardDiskSettingData");
                inParams["Path"] = currentPath;
                var outParams = imgInst.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
                string xml = outParams["SettingData"]?.ToString() ?? string.Empty;
                var match = Regex.Match(xml, @"<PROPERTY NAME=""ParentPath"" TYPE=""string"">\s*<VALUE>(.*?)</VALUE>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (match.Success)
                {
                    string parent = match.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(parent)) break;
                    if (!Path.IsPathRooted(parent)) parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentPath)!, parent));
                    if (File.Exists(parent)) currentPath = parent; else break;
                }
                else break;
            }
            catch { break; }
        }
        return currentPath;
    }

    private string? ExtractInstanceId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var match = Regex.Match(path, "InstanceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}