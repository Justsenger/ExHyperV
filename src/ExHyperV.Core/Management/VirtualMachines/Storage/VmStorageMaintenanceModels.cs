namespace ExHyperV.Models;

/// <summary>挂接的访问状态。Unavailable 表示证据不足或处于电源过渡状态。</summary>
public enum VmDiskAccessKind
{
    /// <summary>当前挂接可访问。</summary>
    Normal,
    /// <summary>磁盘或所需路径缺失。</summary>
    Missing,
    /// <summary>虚拟机处于保存或暂停状态。</summary>
    VmSavedOrPaused,
    /// <summary>虚拟机正在运行。</summary>
    VmRunning,
    /// <summary>宿主已挂载此映像。</summary>
    HostMounted,
    /// <summary>文件被其他使用者锁定。</summary>
    LockedExternal,
    /// <summary>磁盘作为父盘被引用。</summary>
    ParentInUse,
    /// <summary>当前证据不足，无法确认状态。</summary>
    Unavailable
}

/// <summary>存储详检结果和当前状态允许的操作。实际提交时重新检查。</summary>
/// <param name="Kind">当前挂接的访问状态。</param>
/// <param name="Message">本次检查的结果说明或受限原因。</param>
/// <param name="VmState">核实时观察到的虚拟机电源状态。</param>
public sealed record VmDiskInspection(VmDiskAccessKind Kind, string Message, VmState VmState)
{
    /// <summary>当前条件是否允许打开介质文件。</summary>
    public bool CanOpenFile { get; internal init; }
    /// <summary>当前条件是否允许扩展容量。</summary>
    public bool CanExpand { get; internal init; }
    /// <summary>当前条件是否允许收缩容量。</summary>
    public bool CanShrink { get; internal init; }
    /// <summary>是否允许转换磁盘分配类型。</summary>
    public bool CanChangeType { get; internal init; }
    /// <summary>是否允许压缩磁盘。</summary>
    public bool CanCompact { get; internal init; }
    /// <summary>是否允许转换为 VHDX。</summary>
    public bool CanUpgradeVhdx { get; internal init; }
    /// <summary>是否允许导出独立磁盘。</summary>
    public bool CanExport { get; internal init; }
    /// <summary>是否允许合并差分磁盘。</summary>
    public bool CanMerge { get; internal init; }
    /// <summary>是否允许更改父盘路径。</summary>
    public bool CanEditParentPath { get; internal init; }
    /// <summary>是否允许移除挂接并删除介质。</summary>
    public bool CanDeleteFile { get; internal init; }
    /// <summary>是否允许迁移控制器槽位。</summary>
    public bool CanEditSlot { get; internal init; }
    /// <summary>是否允许更换介质。</summary>
    public bool CanEditMedia { get; internal init; }
}

/// <summary>单个文件介质的元数据读取状态。未取得的数值保持为空。</summary>
public enum VmDiskMetadataState
{
    /// <summary>此介质不适用文件元数据查询。</summary>
    NotApplicable,
    /// <summary>已确认可用。</summary>
    Available,
    /// <summary>磁盘或所需路径缺失。</summary>
    Missing,
    /// <summary>磁盘文件被占用，无法完成读取。</summary>
    Locked,
    /// <summary>当前证据不足，无法确认状态。</summary>
    Unavailable
}

/// <summary>链检查的终止原因。仅 Complete 的节点可供选择合并目标。</summary>
public enum VmDiskChainStatus
{
    /// <summary>已从叶盘确认到根盘。</summary>
    Complete,
    /// <summary>磁盘或所需路径缺失。</summary>
    Missing,
    /// <summary>磁盘文件被占用，无法完成读取。</summary>
    Locked,
    /// <summary>宿主已挂载此映像。</summary>
    HostMounted,
    /// <summary>磁盘链中存在不兼容的容器格式。</summary>
    FormatMismatch,
    /// <summary>父盘标识与子盘记录不一致。</summary>
    IdMismatch,
    /// <summary>父链包含循环。</summary>
    Cycle,
    /// <summary>当前证据不足，无法确认状态。</summary>
    Unavailable
}

/// <summary>深度 0 为所选磁盘。LinkageId 是当前磁盘的链身份，ParentIdentifier 是子盘记录的预期父身份。</summary>
/// <param name="Depth">在父链中的深度，叶盘为 0。</param>
/// <param name="Path">当前节点的磁盘路径。</param>
/// <param name="Format">当前磁盘容器格式。</param>
/// <param name="VhdType">当前磁盘的分配类型。</param>
/// <param name="LinkageId">当前磁盘用于核验父链的身份。</param>
/// <param name="ParentIdentifier">子盘记录的预期父盘标识。</param>
/// <param name="IdMismatchIgnored">本次检查是否显式忽略该链接的身份差异。</param>
public sealed record VmDiskChainNode(int Depth, string Path, string? Format = null, string? VhdType = null,
    Guid? LinkageId = null, Guid? ParentIdentifier = null, bool IdMismatchIgnored = false);

/// <summary>从叶盘到根盘的检查结果。失败时保留出错路径及之前已解析的节点。</summary>
/// <param name="Status">父链检查的终止状态。</param>
/// <param name="Nodes">从叶盘到根盘依次确认的节点。</param>
/// <param name="Message">本次检查的结果说明或受限原因。</param>
public sealed record VmDiskChainInspection(VmDiskChainStatus Status, IReadOnlyList<VmDiskChainNode> Nodes, string Message)
{
    /// <summary>是否已确认整条父链。</summary>
    public bool IsComplete => Status == VmDiskChainStatus.Complete;
}

/// <summary>只读分区探测的结果。Empty 的未初始化原始盘仍可能没有可验证的安全下限。</summary>
public enum VmPartitionLayoutMode
{
    /// <summary>当前证据不足，无法确认状态。</summary>
    Unavailable,
    /// <summary>已确认常规分区布局。</summary>
    Regular,
    /// <summary>磁盘尾部没有可供收缩的空间。</summary>
    NoTailSpace,
    /// <summary>未发现已分配的分区。</summary>
    Empty,
    /// <summary>仅能根据已知边界给出保守限制。</summary>
    Conservative,
    /// <summary>分区表不完整或损坏。</summary>
    Corrupt
}

/// <summary>只在可靠读到完整边界时提供收缩下限。容量与边界均基于字节计算。</summary>
/// <param name="Access">容量检查时取得的挂接访问记录。</param>
/// <param name="CurrentCapacityBytes">当前虚拟容量，单位字节，未知时为 null。</param>
/// <param name="MaximumGiB">格式支持的最大虚拟容量，单位 GiB。</param>
/// <param name="MinimumInternalSizeBytes">提供者报告的最小内部容量，单位字节。</param>
/// <param name="LastPartitionEndBytes">已核实的末分区结束边界，单位字节。</param>
/// <param name="SafeMinimumGiB">按已确认边界计算的安全收缩下限，单位 GiB。</param>
/// <param name="Layout">分区布局探测结果。</param>
/// <param name="Message">本次检查的结果说明或受限原因。</param>
public sealed record VmResizeSafety(VmDiskInspection Access, long? CurrentCapacityBytes, long MaximumGiB,
    long? MinimumInternalSizeBytes, long? LastPartitionEndBytes, long? SafeMinimumGiB,
    VmPartitionLayoutMode Layout, string Message)
{
    /// <summary>当前虚拟容量，单位 GiB，未知时为 null。</summary>
    public double? CurrentCapacityGiB => CurrentCapacityBytes / 1073741824d;
    /// <summary>当前条件是否允许扩展容量。</summary>
    public bool CanExpand => Access.CanExpand && CurrentCapacityBytes < MaximumGiB * 1073741824L;
    /// <summary>当前条件是否允许收缩容量。</summary>
    public bool CanShrink => Access.CanShrink && CurrentCapacityBytes is > 0 && SafeMinimumGiB is >= 1 &&
        SafeMinimumGiB <= (CurrentCapacityBytes.Value - 1) / 1073741824L;
}

/// <summary>稳定排序按可用、已分配和不可直通排列。OwnerVmId 可与当前虚拟机比较。</summary>
public enum VmPassthroughAvailability
{
    /// <summary>已确认可用。</summary>
    Available,
    /// <summary>设备已分配给虚拟机。</summary>
    Assigned,
    /// <summary>当前证据不足，无法确认状态。</summary>
    Unavailable
}
/// <summary>Disk 与 OpticalDrive 仅一个有值。已分配的设备附带归属虚拟机标识。</summary>
/// <param name="Disk">物理硬盘候选，光驱条目中为空。</param>
/// <param name="OpticalDrive">物理光驱候选，硬盘条目中为空。</param>
/// <param name="Availability">设备可用性与分配状态。</param>
/// <param name="OwnerVmId">已确认占用此设备的虚拟机 GUID。</param>
/// <param name="Message">本次检查的结果说明或受限原因。</param>
public sealed record VmPassthroughCandidate(HostDiskInfo? Disk, HostOpticalDriveInfo? OpticalDrive,
    VmPassthroughAvailability Availability, Guid? OwnerVmId, string Message);
