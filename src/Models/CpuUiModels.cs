using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace ExHyperV.Models
{
    public partial class UiCoreModel : ObservableObject
    {
        public int CoreId { get; set; }

        [ObservableProperty]
        private double _usage;

        [ObservableProperty]
        private PointCollection _historyPoints;

        public System.Collections.Generic.LinkedList<double> RawHistory { get; } = new();
    }

    public partial class UiVmModel : ObservableObject
    {
        public string Name { get; set; }

        // 新增：动态控制该虚拟机在界面上显示几列
        [ObservableProperty]
        private int _columns = 2;

        public ObservableCollection<UiCoreModel> Cores { get; } = new ObservableCollection<UiCoreModel>();
    }
}