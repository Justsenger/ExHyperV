using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>
    /// 虚拟机磁盘容量及性能采样。
    /// 属性更改通过 PropertyChanged 通知订阅方，不持有界面对象。
    /// </summary>
    public partial class VmDiskStatus : ObservableObject
    {
        /// <summary>磁盘显示名称。</summary>
        [ObservableProperty] private string _name = string.Empty;
        /// <summary>磁盘文件或设备路径。</summary>
        [ObservableProperty] private string _path = string.Empty;
        /// <summary>磁盘来源类型标识，例如 Physical 或 Virtual。</summary>
        [ObservableProperty] private string _diskType = string.Empty;
        /// <summary>文件介质为文件长度，物理盘为整盘容量，单位字节；不是文件系统实际分配空间或来宾已用空间，须结合 SizeSample 判断有效性。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsagePercentage))] // 通知进度条刷新
        private long _currentSize;
        /// <summary>磁盘最大或虚拟容量，单位为字节。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsagePercentage))]
        private long _maxSize;
        /// <summary>当前读取速率，单位为字节每秒。</summary>
        [ObservableProperty]
        private long _readBytesPerSecond; // 字节每秒

        /// <summary>当前写入速率，单位为字节每秒。</summary>
        [ObservableProperty]
        private long _writeBytesPerSecond; // 字节每秒

        /// <summary>本次读写速率读取的有效性与时间；无效时速率字段不更新。</summary>
        [ObservableProperty]
        private PerformanceSampleInfo _performanceSample = new(PerformanceSampleState.NotSampled, default);

        /// <summary>最大容量的本次读取状态；无效时不得将 MaxSize 的默认值解释为零容量。</summary>
        [ObservableProperty]
        private PerformanceSampleInfo _capacitySample = new(PerformanceSampleState.NotSampled, default);

        /// <summary>当前文件占用大小的本次读取状态；无效时不得将 CurrentSize 的默认值解释为空磁盘。</summary>
        [ObservableProperty]
        private PerformanceSampleInfo _sizeSample = new(PerformanceSampleState.NotSampled, default);

        /// <summary>物理磁盘的 PnP 设备标识；虚拟磁盘可为空。</summary>
        public string PnpDeviceId { get; set; } = string.Empty; // 物理硬盘的 PNPDeviceID（仅 Physical 类型用）

        /// <summary>CurrentSize 相对 MaxSize 的百分比；文件介质可能超过 100%，物理盘通常为 100%，均不是来宾空间使用率。最大容量不大于零时返回 0，使用前须确认 SizeSample 和 CapacitySample 有效。</summary>
        public double UsagePercentage => MaxSize > 0 ? (double)CurrentSize / MaxSize * 100 : 0;
    }
}
