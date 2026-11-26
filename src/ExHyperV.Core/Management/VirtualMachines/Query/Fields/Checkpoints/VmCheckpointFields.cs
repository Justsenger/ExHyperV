using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>快照的独立可查询字段，可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmCheckpointFields
{
    /// <summary>快照身份：快照或当前配置节点的标识。</summary>
    /// <fielddoc>
    /// <meaning>快照或当前配置的唯一标识，用于区分同一虚拟机的不同配置节点。</meaning>
    /// <source>配置与快照的 VirtualSystemSettingData 及父节点关联，读取或派生 Id。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Id = new("Checkpoint.Id", "快照身份", "快照", VmQueryCollection.Checkpoints, "Checkpoints", "Id");

    /// <summary>快照名称：快照或当前配置节点的显示名称。</summary>
    /// <fielddoc>
    /// <meaning>快照或当前配置节点的显示名称。</meaning>
    /// <source>配置与快照的 VirtualSystemSettingData 及父节点关联，读取或派生 Name。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="supported" input="node、newName" submission="单快照提交">适用于历史快照，当前实时节点不属于可重命名的快照。<see cref="VmCheckpointService.RenameSnapshotAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Checkpoint.Name", "快照名称", "快照", VmQueryCollection.Checkpoints, "Checkpoints", "Name");

    /// <summary>快照创建时间：快照或当前配置节点的创建时间。</summary>
    /// <fielddoc>
    /// <meaning>快照或当前配置节点的创建时间。</meaning>
    /// <unit>日期与时间</unit>
    /// <values>宿主本地日期与时间。</values>
    /// <source>配置与快照的 VirtualSystemSettingData 及父节点关联，读取或派生 CreatedDate。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<DateTime> CreatedDate = new("Checkpoint.CreatedDate", "快照创建时间", "快照", VmQueryCollection.Checkpoints, "Checkpoints", "CreatedDate");

    /// <summary>父快照身份：在本次节点集合中解析出的父配置标识，未解析到时为空字符串。</summary>
    /// <fielddoc>
    /// <meaning>在本次节点集合中解析出的父配置标识，空字符串也可能表示父配置未包含在结果中。</meaning>
    /// <source>配置与快照的 VirtualSystemSettingData 及父节点关联，读取或派生 ParentId。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> ParentId = new("Checkpoint.ParentId", "父快照身份", "快照", VmQueryCollection.Checkpoints, "Checkpoints", "ParentId");

    /// <summary>节点类型：节点的类型，用于区分当前配置和已保存的快照。</summary>
    /// <fielddoc>
    /// <meaning>节点的类型，用于区分当前配置和已保存的快照。</meaning>
    /// <source>配置与快照的 VirtualSystemSettingData 及父节点关联，读取或派生 NodeType。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<CheckpointNodeType> NodeType = new("Checkpoint.NodeType", "节点类型", "快照", VmQueryCollection.Checkpoints, "Checkpoints", "NodeType");

    /// <summary>当前所在位置：节点是否为当前配置或其直接父快照，用于标记当前分支位置。</summary>
    /// <fielddoc>
    /// <meaning>节点是否为当前配置或其直接父快照，用于标记当前分支位置。</meaning>
    /// <values>当前配置节点和其直接父快照均可为 true。</values>
    /// <source>配置与快照的 VirtualSystemSettingData 及父节点关联，读取或派生 IsCurrent。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="operation" submission="操作流程">随快照应用而变化。<see cref="VmCheckpointService.ApplySnapshotAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsCurrent = new("Checkpoint.IsCurrent", "当前所在位置", "快照", VmQueryCollection.Checkpoints, "Checkpoints", "IsCurrent");

    /// <summary>历史磁盘路径：当前节点关联的存储介质路径列表。</summary>
    /// <fielddoc>
    /// <meaning>当前节点直接关联的全部存储介质路径，可能包含光驱镜像；不展开虚拟磁盘父链。</meaning>
    /// <values>返回本节点存储分配记录中的全部介质路径；没有关联路径时为空列表。</values>
    /// <source>快照或当前配置关联的 StorageAllocationSettingData.HostResource。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<string>> DiskPaths = new("Checkpoint.DiskPaths", "历史磁盘路径", "快照", VmQueryCollection.Checkpoints, "CheckpointDisks", "DiskPaths");

    /// <summary>历史磁盘临时挂接状态：是否检测到该节点历史磁盘的临时挂接。</summary>
    /// <fielddoc>
    /// <meaning>是否检测到该节点历史磁盘的临时挂接。</meaning>
    /// <source>快照配置的 StorageAllocationSettingData，临时挂接存在时进一步核对磁盘链，读取或派生 IsHistoricalDiskMounted。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="operation" submission="操作流程">随历史磁盘临时挂接的打开或关闭而变化。<see cref="VmCheckpointService.MountHistoricalDiskAsync"/>、<see cref="VmCheckpointService.DismountHistoricalDiskAsync"/></modification>
    /// <values kind="options"><c>true</c>：检测到历史磁盘的临时挂接；<c>false</c>：未检测到。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsHistoricalDiskMounted = new("Checkpoint.IsHistoricalDiskMounted", "历史磁盘临时挂接状态", "快照", VmQueryCollection.Checkpoints, "CheckpointDisks", "IsHistoricalDiskMounted");

    internal static readonly VmQueryField[] Items = [Id, Name, CreatedDate, ParentId, NodeType, IsCurrent, DiskPaths, IsHistoricalDiskMounted];
}
