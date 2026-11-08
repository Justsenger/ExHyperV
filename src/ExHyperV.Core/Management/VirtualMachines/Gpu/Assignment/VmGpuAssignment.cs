using CommunityToolkit.Mvvm.ComponentModel;
namespace ExHyperV.Models
{
    /// <summary>虚拟机已分配 GPU 的可变查询模型；修改字段不会自动改变 Hyper-V 分配。</summary>
    public partial class VmGpuAssignment : ObservableObject
    {
        /// <summary>虚拟 GPU 分区适配器的标识，供移除操作定位。</summary>
        internal string AdapterId { get; set; } = string.Empty;
        /// <summary>匹配到的宿主显卡 PnP 实例 ID；无法匹配时为空。</summary>
        public string HostDeviceInstanceId { get; internal set; } = string.Empty;
        /// <summary>宿主 GPU 型号的显示名称。</summary>
        [ObservableProperty] private string _name = string.Empty;           // 型号全名
        /// <summary>GPU 芯片商名称，用于厂商分类。</summary>
        [ObservableProperty] private string _manu = string.Empty;           // 芯片商 (NVIDIA/AMD) -> 匹配图标用
        /// <summary>板卡制造商的显示名称。</summary>
        [ObservableProperty] private string _vendor = string.Empty;         // 制造商 (ASUS/MSI) -> 文字显示用
        /// <summary>分配所引用的宿主 GPU 分区资源路径。</summary>
        internal string PartitionableGpuPath { get; set; } = string.Empty;
        /// <summary>匹配到的宿主显卡驱动版本。</summary>
        [ObservableProperty] private string _driverVersion = string.Empty;
        /// <summary>匹配到的宿主 GPU 专用显存容量，单位为字节；null 表示未知，0 是有效容量，不是虚拟机分区配额。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MemoryDisplay))]
        private ulong? _memoryBytes;

        /// <summary>显存显示文本，以 MiB 换算并标为 MB；未知容量返回 N/A。</summary>
        public string MemoryDisplay
        {
            get
            {
                if (!MemoryBytes.HasValue) return "N/A";
                double mb = MemoryBytes.Value / (1024.0 * 1024.0);
                return $"{mb:F0} MB";
            }
        }
    }
}
