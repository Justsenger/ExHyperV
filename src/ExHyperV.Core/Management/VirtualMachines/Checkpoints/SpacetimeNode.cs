using System;

using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models;

/// <summary>检查点时间线的保存模式。</summary>
public enum SpacetimeMode
{
    /// <summary>连续模式，保留运行状态的检查点流程。</summary>
    Continuous,
    /// <summary>静态模式，Running 状态下请求生产检查点；其他状态请求标准检查点。不支持快照的磁盘可被生产检查点跳过。</summary>
    Still
}

/// <summary>时间线节点代表的对象种类。</summary>
public enum SpacetimeNodeType
{
    /// <summary>时间线的逻辑起点。</summary>
    Genesis,
    /// <summary>可操作的真实检查点。</summary>
    Snapshot,
    /// <summary>时间线中标记当前运行位置的逻辑节点。</summary>
    Current
}

/// <summary>检查点时间线节点。修改模型不会修改检查点；重命名、回退和历史磁盘挂接均由服务执行。</summary>
public partial class SpacetimeNode : ObservableObject
{
    /// <summary>检查点 GUID 文本或 GenesisId、CurrentId 逻辑节点标识，不包含 WMI 实例路径。</summary>
    public string Id { get; set; } = string.Empty;
    internal string InstanceId { get; set; } = string.Empty;
    internal string VmId { get; set; } = string.Empty;
    internal string? ParentInstanceId { get; set; }
    /// <summary>父节点标识；null 表示没有父节点。</summary>
    public string? ParentId { get; set; }

    /// <summary>节点当前显示名称。</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>真实检查点的本地创建时间；Genesis 为推算的时间线起点，Current 为本次读取时间，均不能作为 VM 创建时间。</summary>
    public DateTime CreatedDate { get; set; }
    /// <summary>节点缩略图；null 表示未提供图像。</summary>
    public VmScreenshotImage? Thumbnail { get; set; }
    /// <summary>是否标记为当前时间线位置；Current 节点和它的直接父节点都会为 true，因此并非唯一选中标记。</summary>
    public bool IsCurrent { get; set; }
    /// <summary>对应检查点设置对象的 WMI 路径。</summary>
    internal string Path { get; set; } = string.Empty;
    /// <summary>Hyper-V 返回的虚拟系统类型字符串。</summary>
    internal string VirtualSystemType { get; set; } = string.Empty;
    /// <summary>真实检查点、起点或当前状态节点的分类。</summary>
    public SpacetimeNodeType NodeType { get; set; }
    /// <summary>是否为起点或当前状态逻辑节点；这两类节点不能作为真实检查点修改。</summary>
    public bool IsLogicalNode => NodeType == SpacetimeNodeType.Genesis || NodeType == SpacetimeNodeType.Current;

    /// <summary>
    /// 查询选出的一个磁盘文件路径，可能为 .avhdx；供历史磁盘挂接使用，不是检查点的完整磁盘清单。
    /// </summary>
    public string VhdPath { get; set; } = string.Empty;

    // ── 虫洞状态（运行时检测，不持久化）────────────────────────
    /// <summary>
    /// 该节点当前是否开着虫洞（从实际挂载状态检测）
    /// </summary>
    private bool _isWormhole;

    /// <summary>查询或操作后确认的历史磁盘挂接状态；由 Core 更新。</summary>
    public bool IsWormhole
    {
        get => _isWormhole;
        internal set => SetProperty(ref _isWormhole, value);
    }

    /// <summary>临时差分盘路径，关闭虫洞时摘除+删除</summary>
    internal string WormholeTmpDiskPath { get; set; } = string.Empty;

    /// <summary>父盘改名后的路径（_renamed.vhdx），关闭虫洞时改回 .avhdx</summary>
    internal string WormholeRenamedPath { get; set; } = string.Empty;

    // 关闭时先移除介质、再移除驱动器；中途失败后用它核对并清理已空的驱动器。
    internal string WormholeDriveInstanceId { get; set; } = string.Empty;

    /// <summary>虫洞盘挂载的控制器类型</summary>
    internal string WormholeCtrlType { get; set; } = "SCSI";

    /// <summary>虫洞盘挂载的控制器编号</summary>
    internal int WormholeCtrlNum { get; set; } = 0;

    /// <summary>虫洞盘挂载的控制器位置</summary>
    internal int WormholeCtrlLoc { get; set; } = 0;

    // ── 常量 ─────────────────────────────────────────────────
    /// <summary>时间线起点的固定逻辑标识 GENESIS_ROOT。</summary>
    public const string GenesisId = "GENESIS_ROOT";
    /// <summary>当前运行位置的固定逻辑标识 CURRENT_RUNNING。</summary>
    public const string CurrentId = "CURRENT_RUNNING";
}
