using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>检查点的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmCheckpointFields
{
    /// <summary>检查点身份：检查点或当前配置节点的标识。</summary>
    /// <fielddoc>
    /// <meaning>检查点或当前配置的唯一标识，用于区分同一虚拟机的不同配置节点。</meaning>
    /// <source>配置与检查点的 VirtualSystemSettingData 及父节点关联，读取或派生 Id。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Id = new("Checkpoint.Id", "检查点身份", "检查点", VmQueryCollection.Checkpoints, "Checkpoints", "Id");

    /// <summary>检查点名称：检查点或当前配置节点的显示名称。</summary>
    /// <fielddoc>
    /// <meaning>检查点或当前配置节点的显示名称。</meaning>
    /// <source>配置与检查点的 VirtualSystemSettingData 及父节点关联，读取或派生 Name。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="supported" input="node、newName" submission="单检查点提交">适用于历史检查点；当前实时节点不属于可重命名的检查点。<see cref="VmSpacetimeService.RenameSnapshotAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Checkpoint.Name", "检查点名称", "检查点", VmQueryCollection.Checkpoints, "Checkpoints", "Name");

    /// <summary>检查点创建时间：检查点或当前配置节点的创建时间。</summary>
    /// <fielddoc>
    /// <meaning>检查点或当前配置节点的创建时间。</meaning>
    /// <unit>日期与时间</unit>
    /// <values>宿主本地日期与时间。</values>
    /// <source>配置与检查点的 VirtualSystemSettingData 及父节点关联，读取或派生 CreatedDate。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<DateTime> CreatedDate = new("Checkpoint.CreatedDate", "检查点创建时间", "检查点", VmQueryCollection.Checkpoints, "Checkpoints", "CreatedDate");

    /// <summary>父检查点身份：在本次节点集合中解析出的父配置标识；未解析到时为空字符串。</summary>
    /// <fielddoc>
    /// <meaning>在本次节点集合中解析出的父配置标识；空字符串也可能表示父配置未包含在结果中。</meaning>
    /// <source>配置与检查点的 VirtualSystemSettingData 及父节点关联，读取或派生 ParentId。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> ParentId = new("Checkpoint.ParentId", "父检查点身份", "检查点", VmQueryCollection.Checkpoints, "Checkpoints", "ParentId");

    /// <summary>节点类型：节点的类型，用于区分当前配置和已保存的检查点。</summary>
    /// <fielddoc>
    /// <meaning>节点的类型，用于区分当前配置和已保存的检查点。</meaning>
    /// <source>配置与检查点的 VirtualSystemSettingData 及父节点关联，读取或派生 NodeType。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<SpacetimeNodeType> NodeType = new("Checkpoint.NodeType", "节点类型", "检查点", VmQueryCollection.Checkpoints, "Checkpoints", "NodeType");

    /// <summary>当前所在位置：节点是否为当前配置或其直接父检查点，用于标记当前分支位置。</summary>
    /// <fielddoc>
    /// <meaning>节点是否为当前配置或其直接父检查点，用于标记当前分支位置。</meaning>
    /// <values>当前配置节点和其直接父检查点均可为 true。</values>
    /// <source>配置与检查点的 VirtualSystemSettingData 及父节点关联，读取或派生 IsCurrent。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="operation" submission="操作流程">随检查点恢复而变化。<see cref="VmSpacetimeService.TeleportAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsCurrent = new("Checkpoint.IsCurrent", "当前所在位置", "检查点", VmQueryCollection.Checkpoints, "Checkpoints", "IsCurrent");

    /// <summary>历史磁盘路径：当前节点关联的存储介质路径列表。</summary>
    /// <fielddoc>
    /// <meaning>当前节点直接关联的全部存储介质路径，可能包含光驱镜像；不展开虚拟磁盘父链。</meaning>
    /// <values>返回本节点存储分配记录中的全部介质路径；没有关联路径时为空列表。</values>
    /// <source>检查点或当前配置关联的 StorageAllocationSettingData.HostResource。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<string>> DiskPaths = new("Checkpoint.DiskPaths", "历史磁盘路径", "检查点", VmQueryCollection.Checkpoints, "CheckpointDisks", "DiskPaths");

    /// <summary>历史磁盘临时挂接状态：是否检测到该节点历史磁盘的临时挂接。</summary>
    /// <fielddoc>
    /// <meaning>是否检测到该节点历史磁盘的临时挂接。</meaning>
    /// <source>检查点配置的 StorageAllocationSettingData；临时挂接存在时进一步核对磁盘链，读取或派生 IsWormhole。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="operation" submission="操作流程">随历史磁盘临时挂接的打开或关闭而变化。<see cref="VmSpacetimeService.OpenWormholeAsync"/>、<see cref="VmSpacetimeService.CloseWormholeAsync"/></modification>
    /// <values kind="options"><c>true</c>：检测到历史磁盘的临时挂接；<c>false</c>：未检测到。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsWormhole = new("Checkpoint.IsWormhole", "历史磁盘临时挂接状态", "检查点", VmQueryCollection.Checkpoints, "CheckpointDisks", "IsWormhole");

    internal static readonly VmQueryField[] Items = [Id, Name, CreatedDate, ParentId, NodeType, IsCurrent, DiskPaths, IsWormhole];
}
