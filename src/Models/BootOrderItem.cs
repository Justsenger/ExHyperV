using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>虚拟机引导项（一条启动设备/启动源），由 VmBootService 生产，绑定引导顺序列表。</summary>
    public partial class BootOrderItem : ObservableObject
    {
        [ObservableProperty] private string _name = string.Empty;          // 显示名称
        [ObservableProperty] private string _description = string.Empty;   // 详细描述（路径/地址）
        [ObservableProperty] private string _icon = string.Empty;          // 图标字形码
        [ObservableProperty] private bool _isLast;          // 辅助 UI：末项不画箭头

        /// <summary>回写引导顺序用的原始标识：Gen1=设备码(int)，Gen2=固件路径(string)。</summary>
        public object Reference { get; set; } = null!;

        /// <summary>Gen2 稳定匹配键（FirmwareDevicePath）：写入时按它把用户顺序映射到当前有效的启动源路径，避开会重建的 InstanceID。</summary>
        public string? FwPath { get; set; }
    }
}
