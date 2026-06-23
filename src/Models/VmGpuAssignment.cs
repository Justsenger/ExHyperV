using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace ExHyperV.Models
{
    public partial class VmGpuAssignment : ObservableObject
    {
        [ObservableProperty] private string _adapterId = string.Empty;
        [ObservableProperty] private string _name = string.Empty;           // 型号全名
        [ObservableProperty] private string _manu = string.Empty;           // 芯片商 (NVIDIA/AMD) -> 匹配图标用
        [ObservableProperty] private string _vendor = string.Empty;         // 制造商 (ASUS/MSI) -> 文字显示用
        [ObservableProperty] private string _pName = string.Empty;
        [ObservableProperty] private string _driverVersion = string.Empty;
        [ObservableProperty] private string _ram = string.Empty;

        public string RamDisplay
        {
            get
            {
                if (long.TryParse(Ram, out long bytes) && bytes > 0)
                {
                    double mb = bytes / (1024.0 * 1024.0);
                    return $"{mb:F0} MB";
                }
                return "N/A";
            }
        }
    }
}