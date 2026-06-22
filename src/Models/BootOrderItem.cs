using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>虚拟机引导项（一条启动设备/启动源），由 VmBootService 生产，绑定引导顺序列表。</summary>
    public partial class BootOrderItem : ObservableObject
    {
        [ObservableProperty] private string _name;          // 显示名称
        [ObservableProperty] private string _description;   // 详细描述（路径/地址）
        [ObservableProperty] private string _icon;          // 图标字形码
        [ObservableProperty] private bool _isLast;          // 辅助 UI：末项不画箭头

        /// <summary>回写引导顺序用的原始标识：Gen1=设备码(int)，Gen2=固件路径(string)。</summary>
        public object Reference { get; set; }
    }
}
