using CommunityToolkit.Mvvm.ComponentModel;
namespace ExHyperV.Models
{
    public partial class VmGpuAssignment : ObservableObject
    {
        [ObservableProperty] private string _adapterId = string.Empty;
        [ObservableProperty] private string _name = string.Empty;           // 型号全名
        [ObservableProperty] private string _manu = string.Empty;           // 芯片商 (NVIDIA/AMD) -> 匹配图标用
        [ObservableProperty] private string _vendor = string.Empty;         // 制造商 (ASUS/MSI) -> 文字显示用
        [ObservableProperty] private string _partitionableGpuPath = string.Empty;
        [ObservableProperty] private string _driverVersion = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
        private ulong _memoryBytes;

        public string MemoryDisplay
        {
            get
            {
                if (MemoryBytes == 0) return "N/A";
                double mb = MemoryBytes / (1024.0 * 1024.0);
                return $"{mb:F0} MB";
            }
        }
    }
}
