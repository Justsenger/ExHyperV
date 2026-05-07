using System;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel; // 必须引用这个
using CommunityToolkit.Mvvm.Input;
namespace ExHyperV.Models;

public enum SpacetimeMode
{
    Continuous, // 连续时空 - 标准检查点 SnapshotType=1
    Still       // 静止时空 - 生产检查点 SnapshotType=2
}

/// <summary>
/// 时空节点类型
/// </summary>
public enum SpacetimeNodeType
{
    Genesis,
    Snapshot,
    Current
}

// 核心改动 1：加上 partial 和 继承 ObservableObject
public partial class SpacetimeNode : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }

    // 核心改动 2：将 Name 改为 ObservableProperty，这样修改名称时 UI 才会动
    [ObservableProperty]
    private string _name = string.Empty;

    public DateTime CreatedDate { get; set; }
    public BitmapSource? Thumbnail { get; set; }

    /// <summary>
    /// 是否为当前正在运行的指针指向的节点
    /// </summary>
    public bool IsCurrent { get; set; }

    public string Path { get; set; } = string.Empty;
    public string VirtualSystemType { get; set; } = string.Empty;

    /// <summary>
    /// 节点逻辑类型
    /// </summary>
    public SpacetimeNodeType NodeType { get; set; }

    /// <summary>
    /// 是否为逻辑构造节点
    /// </summary>
    public bool IsLogicalNode => NodeType == SpacetimeNodeType.Genesis || NodeType == SpacetimeNodeType.Current;


    [ObservableProperty]
    private bool _isEditing; // 控制是否显示输入框

    [ObservableProperty]
    private string _editedName = string.Empty; // 存储输入框里的临时文本

    public void StartEditing()
    {
        if (IsLogicalNode) return;
        EditedName = Name;
        IsEditing = true;
    }
    [RelayCommand] private void StartEdit() => StartEditing();
    // 常量定义
    public const string GenesisId = "GENESIS_ROOT";
    public const string CurrentId = "CURRENT_RUNNING";
}