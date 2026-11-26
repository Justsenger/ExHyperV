namespace ExHyperV.Models
{
    /// <summary>虚拟机已分配 GPU 的可变查询模型。</summary>
    public partial class VmGpuAssignment
    {
        /// <summary>虚拟 GPU 分区适配器的标识，供移除操作定位。</summary>
        public string AdapterId { get; internal set; } = string.Empty;
        /// <summary>匹配到的宿主显卡 PnP 实例 ID；无法匹配时为空。</summary>
        public string HostDeviceInstanceId { get; internal set; } = string.Empty;
        /// <summary>宿主 GPU 型号的显示名称。</summary>
        public string Name { get => _name; set => _name = value; }
        private string _name = string.Empty;           // 型号全名
        /// <summary>GPU 芯片商名称，用于厂商分类。</summary>
        public string Manu { get => _manu; set => _manu = value; }
        private string _manu = string.Empty;           // 芯片商 (NVIDIA/AMD) -> 匹配图标用
        /// <summary>板卡制造商的显示名称。</summary>
        public string Vendor { get => _vendor; set => _vendor = value; }
        private string _vendor = string.Empty;         // 制造商 (ASUS/MSI) -> 文字显示用
        /// <summary>分配所引用的宿主 GPU 分区资源路径。</summary>
        internal string PartitionableGpuPath { get; set; } = string.Empty;
        /// <summary>匹配到的宿主显卡驱动版本。</summary>
        public string DriverVersion { get => _driverVersion; set => _driverVersion = value; }
        private string _driverVersion = string.Empty;
        /// <summary>最近一次已部署到来宾系统的驱动版本；未部署、已跳过、挂载安装光盘待装或无记录时为 null。</summary>
        public string? GuestDriverVersion { get => _guestDriverVersion; set => _guestDriverVersion = value; }
        private string? _guestDriverVersion;
        /// <summary>来宾驱动是否需要更新或补装（宿主已知驱动版本且与已部署来宾版本不一致）。</summary>
        public bool HasDriverUpdate =>
            !string.IsNullOrWhiteSpace(DriverVersion) &&
            !string.Equals(DriverVersion.Trim(), GuestDriverVersion?.Trim(), StringComparison.OrdinalIgnoreCase);
        /// <summary>匹配到的宿主 GPU 专用显存容量，单位为字节；null 表示未知，0 是有效容量。</summary>
        public ulong? MemoryBytes { get => _memoryBytes; set => _memoryBytes = value; }
        private ulong? _memoryBytes;
    }
}
