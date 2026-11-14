
namespace ExHyperV.Models
{
    /// <summary>虚拟机 MMIO 地址空间设置（Msvm_VirtualSystemSettingData，单位 MiB）。</summary>
    public partial class VmMmioSettings
    {
        /// <summary>低位 MMIO 空间大小，单位为 MiB（1024² 字节）；null 表示未取得。</summary>
        public ulong? LowSizeMiB { get => _lowSizeMiB; set => _lowSizeMiB = value; }
        private ulong? _lowSizeMiB;
        /// <summary>高位 MMIO 空间大小，单位为 MiB（1024² 字节）；null 表示未取得。</summary>
        public ulong? HighSizeMiB { get => _highSizeMiB; set => _highSizeMiB = value; }
        private ulong? _highSizeMiB;
        /// <summary>高位 MMIO 地址基址，单位为 MiB（1024² 字节）；null 表示未取得。</summary>
        public ulong? HighBaseMiB { get => _highBaseMiB; set => _highBaseMiB = value; }
        private ulong? _highBaseMiB;

        /// <summary>复制当前值为独立模型。</summary>
        /// <returns>包含当前三个 MMIO 值的新模型。</returns>
        public VmMmioSettings Clone() => new()
        {
            _lowSizeMiB = _lowSizeMiB,
            _highSizeMiB = _highSizeMiB,
            _highBaseMiB = _highBaseMiB
        };

        /// <summary>将备份值恢复到当前编辑模型。</summary>
        /// <param name="other">要恢复的设置快照；null 时不做操作。</param>
        public void Restore(VmMmioSettings other)
        {
            if (other is null) return;
            LowSizeMiB = other.LowSizeMiB;
            HighSizeMiB = other.HighSizeMiB;
            HighBaseMiB = other.HighBaseMiB;
        }
    }
}
