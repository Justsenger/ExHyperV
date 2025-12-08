// 文件路径: ExHyperV/Models/CpuUiModels.cs

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ExHyperV.Services; // 引用 CoreType

namespace ExHyperV.Models
{
    // --- 1. 定义数据传输对象 (Service -> ViewModel) ---
    public class CpuCoreMetric
    {
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public float Usage { get; set; }
        public bool IsRunning { get; set; } // 标识开机/关机状态
    }

    // --- 2. 定义 UI 核心模型 ---
    public partial class UiCoreModel : ObservableObject
    {
        [ObservableProperty]
        private int _coreId;

        [ObservableProperty]
        private double _usage;

        [ObservableProperty]
        private PointCollection _historyPoints;

        // 核心类型 (P-Core/E-Core)
        public CoreType CoreType { get; init; } = CoreType.Unknown;
    }

    // --- 3. 定义 UI 虚拟机模型 ---
    public partial class UiVmModel : ObservableObject
    {
        [ObservableProperty]
        private string _name;

        // 新增：标识整台 VM 是否在运行
        [ObservableProperty]
        private bool _isRunning = true;

        [ObservableProperty]
        private int _columns = 2;

        [ObservableProperty]
        private int _rows;

        [ObservableProperty]
        private double _averageUsage;

        public ObservableCollection<UiCoreModel> Cores { get; } = new ObservableCollection<UiCoreModel>();
    }
}