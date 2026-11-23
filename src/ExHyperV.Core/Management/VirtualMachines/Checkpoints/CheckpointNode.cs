using System;

namespace ExHyperV.Models;

/// <summary>检查点树节点代表的对象种类。</summary>
public enum CheckpointNodeType
{
    /// <summary>检查点树的初始起点。</summary>
    Genesis,
    /// <summary>可操作的真实检查点。</summary>
    Snapshot,
    /// <summary>检查点树中标记当前运行位置的逻辑节点。</summary>
    Current
}

/// <summary>检查点树节点。</summary>
public partial class CheckpointNode
{
    /// <summary>检查点 GUID 文本或 GenesisId、CurrentId 逻辑节点标识，不包含 WMI 实例路径。</summary>
    public string Id { get; set; } = string.Empty;
    internal string InstanceId { get; set; } = string.Empty;
    internal string VmId { get; set; } = string.Empty;
    internal string? ParentInstanceId { get; set; }
    /// <summary>父节点标识；null 表示没有父节点。</summary>
    public string? ParentId { get; set; }

    /// <summary>节点当前显示名称。</summary>
    public string Name { get => _name; set => _name = value; }
        private string _name = string.Empty;

    /// <summary>检查点的本地创建时间；Genesis 为推算的起点时间，Current 为本次读取时间。</summary>
    public DateTime CreatedDate { get; set; }
    /// <summary>是否标记为当前位置；Current 节点和它的直接父节点都会为 true，因此并非唯一选中标记。</summary>
    public bool IsCurrent { get; set; }
    /// <summary>对应检查点设置对象的 WMI 路径。</summary>
    internal string Path { get; set; } = string.Empty;
    /// <summary>Hyper-V 返回的虚拟系统类型字符串。</summary>
    internal string VirtualSystemType { get; set; } = string.Empty;
    /// <summary>真实检查点、起点或当前状态节点的分类。</summary>
    public CheckpointNodeType NodeType { get; set; }
    /// <summary>是否为起点或当前状态逻辑节点；这两类节点不能作为真实检查点修改。</summary>
    public bool IsLogicalNode => NodeType == CheckpointNodeType.Genesis || NodeType == CheckpointNodeType.Current;

    /// <summary>
    /// 查询选出的一个磁盘文件路径，可能为 .avhdx，用于历史磁盘挂接。
    /// </summary>
    public string VhdPath { get; set; } = string.Empty;

    // ── 历史磁盘挂接状态（运行时检测，不持久化）─────────────────
    private bool _isWormhole;

    /// <summary>查询或操作后确认的历史磁盘挂接状态；由 Core 更新。</summary>
    public bool IsWormhole
    {
        get => _isWormhole;
        internal set => _isWormhole = value;
    }

    /// <summary>临时差分盘路径，卸载时摘除并删除。</summary>
    internal string WormholeTmpDiskPath { get; set; } = string.Empty;

    /// <summary>父盘改名后的路径（_renamed.vhdx），卸载时改回 .avhdx。</summary>
    internal string WormholeRenamedPath { get; set; } = string.Empty;

    // 卸载时先移除介质、再移除驱动器；中途失败后用它核对并清理已空的驱动器。
    internal string WormholeDriveInstanceId { get; set; } = string.Empty;

    /// <summary>历史磁盘挂接使用的控制器类型。</summary>
    internal string WormholeCtrlType { get; set; } = "SCSI";

    /// <summary>历史磁盘挂接使用的控制器编号。</summary>
    internal int WormholeCtrlNum { get; set; } = 0;

    /// <summary>历史磁盘挂接使用的控制器槽位。</summary>
    internal int WormholeCtrlLoc { get; set; } = 0;

    // ── 常量 ─────────────────────────────────────────────────
    /// <summary>检查点起点的固定逻辑标识 GENESIS_ROOT。</summary>
    public const string GenesisId = "GENESIS_ROOT";
    /// <summary>当前运行位置的固定逻辑标识 CURRENT_RUNNING。</summary>
    public const string CurrentId = "CURRENT_RUNNING";
}
