using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>
    /// 虚拟机磁盘的实时数据（带 UI 计算属性）：
    /// Service 写入大小/速率字段时，UI 通过本类自身的 PropertyChanged 自动刷新。
    /// </summary>
    public partial class VmDiskItem : ObservableObject
    {
        [ObservableProperty] private string _name;
        [ObservableProperty] private string _path;
        [ObservableProperty] private string _diskType;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsagePercentage))] // 关键：通知进度条刷新
        [NotifyPropertyChangedFor(nameof(UsageText))]       // 关键：通知百分比文字刷新
        private long _currentSize;
        [ObservableProperty] private long _maxSize;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IoSpeedText))]
        private long _readSpeedBps; // 字节每秒

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IoSpeedText))]
        private long _writeSpeedBps; // 字节每秒

        public string PnpDeviceId { get; set; } // 物理硬盘的 PNPDeviceID（仅 Physical 类型用）

        public string IoSpeedText => $"↑ {FormatIoSpeed(ReadSpeedBps)}   ↓ {FormatIoSpeed(WriteSpeedBps)} ";

        public double UsagePercentage => MaxSize > 0 ? (double)CurrentSize / MaxSize * 100 : 0;
        public string UsageText => $"{FormatBytes(CurrentSize)} / {FormatBytes(MaxSize)}";

        private string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            double dblSByte = bytes;
            while (dblSByte >= 1024 && i < suffixes.Length - 1)
            {
                dblSByte /= 1024;
                i++;
            }
            return $"{dblSByte:0.##} {suffixes[i]}";
        }

        private string FormatIoSpeed(long bps)
        {
            string[] suffixes = { "B/s", "KB/s", "MB/s", "GB/s" };
            int i = 0;
            double dblSpeed = bps;
            while (dblSpeed >= 1024 && i < suffixes.Length - 1)
            {
                dblSpeed /= 1024;
                i++;
            }
            return $"{dblSpeed:0.#} {suffixes[i]}";
        }
    }
}
