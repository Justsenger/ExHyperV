using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal class VmSpacetimeService
{
    private const string SnapshotServiceWql = "SELECT * FROM Msvm_VirtualSystemSnapshotService";

    /// <summary>
    /// 获取虚拟机的完整时空谱系（快照树）
    /// </summary>
    public async Task<List<SpacetimeNode>> GetSpacetimeNodesAsync(string vmName)
    {
        try
        {
            // 1. 获取虚拟机 GUID
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
            var vmList = await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString());
            string? vmGuid = vmList.FirstOrDefault();

            if (string.IsNullOrEmpty(vmGuid)) return new List<SpacetimeNode>();

            // 2. 查询所有相关的 SystemSettingData (包括快照和当前状态)
            string settingWql = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmGuid}'";
            var allSettings = await WmiTools.QueryAsync(settingWql, obj => new SpacetimeNode
            {
                Id = obj["InstanceID"]?.ToString() ?? "",
                Name = obj["ElementName"]?.ToString() ?? "",
                CreatedDate = ManagementDateTimeConverter.ToDateTime(obj["CreationTime"]?.ToString() ?? DateTime.MinValue.ToString()),
                ParentId = ExtractParentId(obj["Parent"]?.ToString()),
                VirtualSystemType = obj["VirtualSystemType"]?.ToString() ?? "",
                Path = obj.Path.ToString()
            });

            // 3. 确定“现世”定位 (Realized 节点的父节点就是当前指针位置)
            var realizedNode = allSettings.FirstOrDefault(n => n.VirtualSystemType == "Microsoft:Hyper-V:System:Realized");
            if (realizedNode != null)
            {
                var nowParentId = realizedNode.ParentId;
                foreach (var node in allSettings)
                {
                    if (node.Id == nowParentId) node.IsCurrent = true;
                }
            }

            // 返回除 Realized 节点本身以外的所有时空锚点
            return allSettings.Where(n => n.VirtualSystemType.Contains("Snapshot")).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"时空检索失败: {ex.Message}");
            return new List<SpacetimeNode>();
        }
    }

    /// <summary>
    /// 穿梭：应用指定的快照
    /// </summary>
    public async Task<(bool Success, string Message)> TeleportAsync(SpacetimeNode node)
    {
        var parameters = new Dictionary<string, object>
        {
            { "SnapshotSettings", node.Path }
        };
        return await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "ApplySnapshot", parameters);
    }

    /// <summary>
    /// 捕捉瞬间：创建新快照
    /// </summary>
    public async Task<(bool Success, string Message)> CaptureMomentAsync(string vmName)
    {
        string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
        var vmPaths = await WmiTools.QueryAsync(vmWql, obj => obj.Path.ToString());
        string? vmPath = vmPaths.FirstOrDefault();

        if (string.IsNullOrEmpty(vmPath)) return (false, "找不到指定的虚拟机载体");

        var parameters = new Dictionary<string, object>
        {
            { "AffectedSystem", vmPath },
            { "SnapshotType", (ushort)2 } // 2 = Standard Snapshot
        };

        return await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "CreateSnapshot", parameters);
    }

    /// <summary>
    /// 湮灭：删除快照（包括其子树，Hyper-V 默认合并）
    /// </summary>
    public async Task<(bool Success, string Message)> AnnihilateAsync(SpacetimeNode node)
    {
        var parameters = new Dictionary<string, object>
        {
            { "SnapshotSettings", node.Path }
        };
        // DestroySnapshot 会触发合并逻辑
        return await WmiTools.ExecuteMethodAsync(SnapshotServiceWql, "DestroySnapshot", parameters);
    }

    /// <summary>
    /// 解析 WMI Parent 路径中的 InstanceID
    /// </summary>
    private string? ExtractParentId(string? parentPath)
    {
        if (string.IsNullOrEmpty(parentPath)) return null;

        // 匹配 InstanceID="......"
        var match = Regex.Match(parentPath, "InstanceID=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}