using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ExHyperV.Services; 

namespace ExHyperV.Models
{
    public class CpuCoreMetric
    {
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public float Usage { get; set; }
        public bool IsRunning { get; set; } 
    }

    public partial class UiCoreModel : ObservableObject
    {
        [ObservableProperty]
        private int _coreId;

        [ObservableProperty]
        private double _usage;

        [ObservableProperty]
        private PointCollection _historyPoints;

        public CoreType CoreType { get; init; } = CoreType.Unknown;
    }

    public partial class UiVmModel : ObservableObject
    {
        [ObservableProperty]
        private string _name;

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