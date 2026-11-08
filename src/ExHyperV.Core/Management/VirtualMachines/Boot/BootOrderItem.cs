using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>启动设备的业务类别，不包含界面图标或列表布局状态。</summary>
    public enum BootDeviceKind
    {
        /// <summary>无法识别的启动设备。</summary>
        Unknown,
        /// <summary>软盘驱动器。</summary>
        Floppy,
        /// <summary>光盘驱动器。</summary>
        OpticalDrive,
        /// <summary>虚拟或物理硬盘。</summary>
        HardDisk,
        /// <summary>网络启动设备。</summary>
        Network,
        /// <summary>固件文件引导项。</summary>
        FirmwareFile,
        /// <summary>固件内置引导项。</summary>
        Firmware
    }

    /// <summary>虚拟机引导项，由 VmBootService 读取；写回时必须使用同一虚拟机读取的对象。</summary>
    public partial class BootOrderItem : ObservableObject
    {
        /// <summary>启动设备的显示名称。</summary>
        [ObservableProperty] private string _name = string.Empty;          // 显示名称
        /// <summary>启动设备的路径或地址说明，用于显示。</summary>
        [ObservableProperty] private string _description = string.Empty;   // 详细描述（路径/地址）
        /// <summary>启动设备的业务类别。</summary>
        [ObservableProperty] private BootDeviceKind _kind;

        internal Guid OwnerVmId { get; set; }

        /// <summary>回写引导顺序用的原始标识：Gen1=设备码(int)，Gen2=固件路径(string)。</summary>
        internal object Reference { get; set; } = null!;

        /// <summary>Gen2 稳定匹配键（FirmwareDevicePath）：写入时按它把用户顺序映射到当前有效的启动源路径，避开会重建的 InstanceID。</summary>
        internal string? FwPath { get; set; }
    }
}
