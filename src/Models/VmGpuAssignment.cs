using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace ExHyperV.Models
{
    public partial class VmGpuAssignment : ObservableObject
    {
        [ObservableProperty] private string _adapterId;
        [ObservableProperty] private string _name;           // 型号全名
        [ObservableProperty] private string _manu;           // 芯片商 (NVIDIA/AMD) -> 匹配图标用
        [ObservableProperty] private string _vendor;         // 制造商 (ASUS/MSI) -> 文字显示用
        [ObservableProperty] private string _instanceId;
        [ObservableProperty] private string _pName;
        [ObservableProperty] private string _driverVersion;
        [ObservableProperty] private string _ram;

        public string RamDisplay
        {
            get
            {
                if (long.TryParse(Ram, out long bytes) && bytes > 0)
                {
                    // ✅ 修正：计算为 MB 并取整
                    double mb = bytes / (1024.0 * 1024.0);
                    return $"{mb:F0} MB";
                }
                return "N/A";
            }
        }
    }
}