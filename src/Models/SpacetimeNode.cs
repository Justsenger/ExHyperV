using System;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ExHyperV.Models;

public enum SpacetimeMode
{
    Continuous,
    Still
}

public enum SpacetimeNodeType
{
    Genesis,
    Snapshot,
    Current
}

public partial class SpacetimeNode : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    public DateTime CreatedDate { get; set; }
    public BitmapSource? Thumbnail { get; set; }
    public bool IsCurrent { get; set; }
    public string Path { get; set; } = string.Empty;
    public string VirtualSystemType { get; set; } = string.Empty;
    public SpacetimeNodeType NodeType { get; set; }
    public bool IsLogicalNode => NodeType == SpacetimeNodeType.Genesis || NodeType == SpacetimeNodeType.Current;

    /// <summary>
    /// 该快照节点对应的磁盘文件路径（.avhdx），由 GetSpacetimeNodesAsync 填充，用于虫洞匹配
    /// </summary>
    public string VhdPath { get; set; } = string.Empty;

    // ── 虫洞状态（运行时检测，不持久化）────────────────────────
    /// <summary>
    /// 该节点当前是否开着虫洞（从实际挂载状态检测）
    /// </summary>
    [ObservableProperty]
    private bool _isWormhole;

    /// <summary>临时差分盘路径，关闭虫洞时摘除+删除</summary>
    public string WormholeTmpDiskPath { get; set; } = string.Empty;

    /// <summary>父盘改名后的路径（_renamed.vhdx），关闭虫洞时改回 .avhdx</summary>
    public string WormholeRenamedPath { get; set; } = string.Empty;

    /// <summary>虫洞盘挂载的控制器类型</summary>
    public string WormholeCtrlType { get; set; } = "SCSI";

    /// <summary>虫洞盘挂载的控制器编号</summary>
    public int WormholeCtrlNum { get; set; } = 0;

    /// <summary>虫洞盘挂载的控制器位置</summary>
    public int WormholeCtrlLoc { get; set; } = 0;

    // ── 编辑态 ───────────────────────────────────────────────
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editedName = string.Empty;

    public void StartEditing()
    {
        if (IsLogicalNode) return;
        EditedName = Name;
        IsEditing = true;
    }

    [RelayCommand] private void StartEdit() => StartEditing();

    // ── 常量 ─────────────────────────────────────────────────
    public const string GenesisId = "GENESIS_ROOT";
    public const string CurrentId = "CURRENT_RUNNING";
}