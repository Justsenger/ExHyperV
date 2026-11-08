using ExHyperV.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    /// <summary>
    /// VM 每个 CPU 核心的显示项（绑定 ItemsSource）：
    /// CoreId / 实时 Usage / 滚动 HistoryPoints / 核心类型 / 选中状态。
    /// </summary>
    public partial class VmCoreItem : ObservableObject
    {
        [ObservableProperty] private int _coreId;
        [ObservableProperty] private double _usage;
        [ObservableProperty] private PointCollection _historyPoints = new();
        [ObservableProperty] private CpuCoreType _coreType = CpuCoreType.Unknown;
        [ObservableProperty] private bool _isSelected;
    }
}
