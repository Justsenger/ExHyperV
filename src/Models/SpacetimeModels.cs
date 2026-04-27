using System;
using System.Windows.Media.Imaging;

namespace ExHyperV.Models;

/// <summary>
/// 时空节点类型
/// </summary>
public enum SpacetimeNodeType
{
    /// <summary>
    /// 时空起源 (起点)
    /// </summary>
    Genesis,

    /// <summary>
    /// 快照时空 (历史点)
    /// </summary>
    Snapshot,

    /// <summary>
    /// 当前时空 (正在运行的状态)
    /// </summary>
    Current
}

public class SpacetimeNode
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
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

    // 常量定义
    public const string GenesisId = "GENESIS_ROOT";
    public const string CurrentId = "CURRENT_RUNNING";
}