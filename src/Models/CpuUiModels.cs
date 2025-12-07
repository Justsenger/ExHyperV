// 文件路径: ExHyperV/Models/CpuUiModels.cs

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace ExHyperV.Models
{
    public partial class UiCoreModel : ObservableObject
    {
        // 为了和 ApplyUpdates 中的逻辑匹配，这里使用属性而不是字段
        [ObservableProperty]
        private int _coreId;

        [ObservableProperty]
        private double _usage;

        [ObservableProperty]
        private PointCollection _historyPoints;
    }

    public partial class UiVmModel : ObservableObject
    {
        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private int _columns = 2;

        [ObservableProperty]
        private int _rows;

        [ObservableProperty]
        private double _averageUsage;

        public ObservableCollection<UiCoreModel> Cores { get; } = new ObservableCollection<UiCoreModel>();
    }
}