using System;

namespace ExHyperV.Models;

public class SpacetimeNode
{
    public string Id { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }

    public System.Windows.Media.Imaging.BitmapSource? Thumbnail { get; set; }
    /// <summary>
    /// 是否为当前正在运行的“现世”指针指向的节点
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// 原始 WMI 路径，用于后续操作
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 虚拟系统类型 (Snapshot 或 Realized)
    /// </summary>
    public string VirtualSystemType { get; set; } = string.Empty;
}