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

    /// <summary>
    /// 获取一个安全的文件名（去掉 WMI ID 中的非法冒号，防止产生 NTFS 备用流文件）
    /// </summary>
    private string GetSafeId(string id) => id.Replace(":", "_");

    public async Task<List<SpacetimeNode>> GetSpacetimeNodesAsync(string vmName)
    {
        try
        {
            // 1. 获取虚拟机基本信息
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
            var vmData = await WmiTools.QueryAsync(vmWql, obj => new {
                Guid = obj["Name"]?.ToString(),
                Path = obj.Path.ToString()
            });

            var vm = vmData.FirstOrDefault();
            if (vm == null || string.IsNullOrEmpty(vm.Guid)) return new List<SpacetimeNode>();

            // 获取配置根目录（截图存放的 Snapshots 目录）
            string configRootWql = $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var configData = await WmiTools.QueryAsync(configRootWql, obj => obj["ConfigurationDataRoot"]?.ToString());
            string configRoot = configData.FirstOrDefault() ?? string.Empty;
            string snapshotDir = Path.Combine(configRoot, "Snapshots");

            // 2. 查询 SettingData
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

            // 3. 追溯主时空时间
            DateTime genesisTime = snapshots.Any() ? snapshots.Min(s => s.CreatedDate).AddMinutes(-1) : DateTime.Now;
            if (realizedNode != null)
            {
                string sasdWql = $"SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31";
                var sasdList = await WmiTools.QueryAsync(sasdWql, obj => new { Id = obj["InstanceID"]?.ToString() ?? "", Path = (obj["HostResource"] as string[])?.FirstOrDefault() });
                string currentVhdPath = sasdList.FirstOrDefault(d => d.Id.Contains(realizedNode.Id))?.Path ?? string.Empty;
                if (!string.IsNullOrEmpty(currentVhdPath))
                {
                    string genesisPath = await Task.Run(() => TraceToGenesisPath(currentVhdPath));
                    if (File.Exists(genesisPath)) genesisTime = File.GetLastWriteTime(genesisPath);
                }
            }

            // 4. 加载资产：快照截图
            foreach (var node in snapshots)
            {
                node.NodeType = SpacetimeNodeType.Snapshot;
                node.Thumbnail = LoadThumbnailFromDisk(snapshotDir, node.Id);
            }

            // 主时空截图：若不存在则初始化
            var genesisThumbnail = LoadThumbnailFromDisk(snapshotDir, SpacetimeNode.GenesisId);
            if (genesisThumbnail == null)
            {
                genesisThumbnail = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
                if (genesisThumbnail != null) await SaveThumbnailToDisk(genesisThumbnail, snapshotDir, SpacetimeNode.GenesisId);
            }

            var genesisNode = new SpacetimeNode { Id = SpacetimeNode.GenesisId, Name = "主时空", NodeType = SpacetimeNodeType.Genesis, CreatedDate = genesisTime, Thumbnail = genesisThumbnail };

            // 当前时空截图：实时抓取画面
            var currentNode = new SpacetimeNode { Id = SpacetimeNode.CurrentId, Name = "当前", NodeType = SpacetimeNodeType.Current, IsCurrent = true, CreatedDate = DateTime.Now, Thumbnail = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160) };

            // 5. 维护谱系
            foreach (var s in snapshots)
            {
                if (string.IsNullOrEmpty(s.ParentId)) s.ParentId = SpacetimeNode.GenesisId;
            }
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
            return result;
        }
        catch (Exception ex) { Debug.WriteLine($"时空检索失败: {ex.Message}"); return new List<SpacetimeNode>(); }
    }

    public async Task<(bool Success, string Message)> TeleportAsync(SpacetimeNode node)
    {
        if (node.NodeType != SpacetimeNodeType.Snapshot) return (false, "只能穿梭至历史快照点");

        // 文档：ApplySnapshot -> 参数名：Snapshot
        var parameters = new Dictionary<string, object> { { "Snapshot", node.Path } };
        var result = await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "ApplySnapshot", parameters);

        if (result.Success || result.Message == "4096") return (true, "正在扭转时空...");
        return (false, $"穿梭失败: {result.Message}");
    }
    public async Task<(bool Success, string Message)> CaptureMomentAsync(string vmName)
    {
        string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
        var vmData = await WmiTools.QueryAsync(vmWql, obj => new { Path = obj.Path.ToString(), Guid = obj["Name"].ToString() });
        var vm = vmData.FirstOrDefault();
        if (vm == null) return (false, "找不到指定的虚拟机载体");

        var bitmap = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
        var parameters = new Dictionary<string, object> { { "AffectedSystem", vm.Path }, { "SnapshotType", (ushort)2 } };
        var result = await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "CreateSnapshot", parameters);

        if (result.Success && bitmap != null)
        {
            string latestWql = $"SELECT InstanceID, ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vm.Guid}' AND VirtualSystemType LIKE '%Snapshot%'";
            var nodes = await WmiTools.QueryAsync(latestWql, obj => new { Id = obj["InstanceID"].ToString(), Root = obj["ConfigurationDataRoot"].ToString() });
            var latest = nodes.OrderByDescending(n => n.Id).FirstOrDefault();
            if (latest != null) await SaveThumbnailToDisk(bitmap, Path.Combine(latest.Root, "Snapshots"), latest.Id);
        }
        return result;
    }

    public async Task<(bool Success, string Message)> AnnihilateAsync(string vmName, SpacetimeNode node)
    {
        if (node.IsLogicalNode) return (false, "主时空与当前节点不可湮灭");

        // 文档：DestroySnapshotTree -> 参数名：SnapshotSettingData
        // 核心修复：这里绝对不能写 AffectedSnapshot，必须写 SnapshotSettingData
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
        if (node.IsLogicalNode) return (false, "主时空与当前节点不可收束");

        // 文档：DestroySnapshot -> 参数名：AffectedSnapshot
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
    // --- 辅助方法：截图物理删除 ---
    private void DeleteThumbnailFile(string snapshotDir, string nodeId)
    {
        try
        {
            string filePath = Path.Combine(snapshotDir, $"{GetSafeId(nodeId)}.jpg");
            if (File.Exists(filePath))
            {
                // 如果文件被占用（可能正在加载），尝试延迟删除
                File.Delete(filePath);
                Debug.WriteLine($"[Spacetime] 已清理快照截图: {nodeId}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Spacetime] 清理截图失败: {ex.Message}");
        }
    }

    // --- 辅助方法：获取 VM 的快照目录 ---
    private async Task<string?> GetSnapshotDirectoryAsync(string vmName)
    {
        try
        {
            // 查找 Realized 状态的 SettingData 以获取最准确的配置根目录
            string wql = $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = (SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}') AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
            var results = await WmiTools.QueryAsync(wql, obj => obj["ConfigurationDataRoot"]?.ToString());
            string? root = results.FirstOrDefault();
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Snapshots");
        }
        catch { return null; }
    }

    // 递归查找子孙的辅助方法保持不变
    private void FindDescendantsRecursive(string parentId, List<SpacetimeNode> allNodes, List<SpacetimeNode> results)
    {
        var children = allNodes.Where(n => n.ParentId == parentId).ToList();
        foreach (var child in children)
        {
            results.Add(child);
            FindDescendantsRecursive(child.Id, allNodes, results);
        }
    }
    
    
 
    private async Task<List<SpacetimeNode>> CreateInitialSpacetimeAsync(string vmName, string snapshotDir)
    {
        var thumb = await VmThumbnailProvider.GetThumbnailAsync(vmName, 280, 160);
        if (thumb != null && !string.IsNullOrEmpty(snapshotDir)) await SaveThumbnailToDisk(thumb, snapshotDir, SpacetimeNode.GenesisId);
        return new List<SpacetimeNode>
        {
            new() { Id = SpacetimeNode.GenesisId, Name = "主时空", NodeType = SpacetimeNodeType.Genesis, IsCurrent = true, CreatedDate = DateTime.Now.AddMinutes(-1), Thumbnail = thumb },
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