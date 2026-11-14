
namespace ExHyperV.Models
{
    /// <summary>
    /// 虚拟机磁盘容量及性能采样。
    /// 每个对象保存一次采样的数据及有效性。
    /// </summary>
    public partial class VmDiskStatus
    {
        /// <summary>磁盘显示名称。</summary>
        public string Name { get => _name; set => _name = value; }
        private string _name = string.Empty;
        /// <summary>磁盘文件或设备路径。</summary>
        public string Path { get => _path; set => _path = value; }
        private string _path = string.Empty;
        /// <summary>磁盘来源类型标识，例如 Physical 或 Virtual。</summary>
        public string DiskType { get => _diskType; set => _diskType = value; }
        private string _diskType = string.Empty;
        /// <summary>文件介质的文件长度或物理盘的整盘容量，单位字节；有效性见 SizeSample。</summary>
        public long CurrentSize { get => _currentSize; set => _currentSize = value; }
        private long _currentSize;
        /// <summary>磁盘最大或虚拟容量，单位为字节。</summary>
        public long MaxSize { get => _maxSize; set => _maxSize = value; }
        private long _maxSize;
        /// <summary>当前读取速率，单位为字节每秒。</summary>
        public long ReadBytesPerSecond { get => _readBytesPerSecond; set => _readBytesPerSecond = value; }
        private long _readBytesPerSecond; // 字节每秒

        /// <summary>当前写入速率，单位为字节每秒。</summary>
        public long WriteBytesPerSecond { get => _writeBytesPerSecond; set => _writeBytesPerSecond = value; }
        private long _writeBytesPerSecond; // 字节每秒

        /// <summary>本次读写速率读取的有效性与时间；无效时速率字段不更新。</summary>
        public PerformanceSampleInfo PerformanceSample { get => _performanceSample; set => _performanceSample = value; }
        private PerformanceSampleInfo _performanceSample = new(PerformanceSampleState.NotSampled, default);

        /// <summary>最大容量的本次读取状态；无效时不得将 MaxSize 的默认值解释为零容量。</summary>
        public PerformanceSampleInfo CapacitySample { get => _capacitySample; set => _capacitySample = value; }
        private PerformanceSampleInfo _capacitySample = new(PerformanceSampleState.NotSampled, default);

        /// <summary>当前文件大小的本次读取状态；无效时不得将 CurrentSize 的默认值解释为空磁盘。</summary>
        public PerformanceSampleInfo SizeSample { get => _sizeSample; set => _sizeSample = value; }
        private PerformanceSampleInfo _sizeSample = new(PerformanceSampleState.NotSampled, default);

        /// <summary>物理磁盘的 PnP 设备标识；虚拟磁盘可为空。</summary>
        public string PnpDeviceId { get; set; } = string.Empty; // 物理硬盘的 PNPDeviceID（仅 Physical 类型用）

        /// <summary>CurrentSize 相对 MaxSize 的百分比；文件介质可超过 100%，物理盘通常为 100%。最大容量不大于零时返回 0；有效性见 SizeSample 和 CapacitySample。</summary>
        public double UsagePercentage => MaxSize > 0 ? (double)CurrentSize / MaxSize * 100 : 0;
    }
}
