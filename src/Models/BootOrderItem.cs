using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    public partial class BootOrderItem : ObservableObject
    {
        [ObservableProperty] private string _name;          // 显示名称
        [ObservableProperty] private string _description;   // 详细描述（路径/地址）
        [ObservableProperty] private string _icon;          // 图标代码
        [ObservableProperty] private bool _isLast;          // 辅助 UI 箭头显示

        // Gen1 存整数 Code，Gen2 存 Msvm_BootSourceSettingData 的 WMI 路径
        public object Reference { get; set; }

        // 标记是否为第二代虚拟机
        public bool IsGen2 { get; set; }
    }
}