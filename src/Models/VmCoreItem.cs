using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ExHyperV.Models
{
    /// <summary>CPU 核心类型：未知 / 性能核 / 能效核（用于异构 CPU 显示）。</summary>
    public enum CoreType { Unknown, Performance, Efficient }

    /// <summary>
    /// VM 每个 CPU 核心的显示项（绑定 ItemsSource）：
    /// CoreId / 实时 Usage / 滚动 HistoryPoints / 核心类型 / 选中状态。
    /// </summary>
    public partial class VmCoreItem : ObservableObject
    {
        [ObservableProperty] private int _coreId;
        [ObservableProperty] private double _usage;
        [ObservableProperty] private PointCollection _historyPoints = new();
        [ObservableProperty] private CoreType _coreType = CoreType.Unknown;
        [ObservableProperty] private bool _isSelected;
    }
}
