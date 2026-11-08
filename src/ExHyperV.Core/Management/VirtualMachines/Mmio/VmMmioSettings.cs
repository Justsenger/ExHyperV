using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>虚拟机 MMIO 地址空间设置（Msvm_VirtualSystemSettingData，单位 MiB）。</summary>
    public partial class VmMmioSettings : ObservableObject
    {
        /// <summary>低位 MMIO 空间大小，单位为 MiB（1024² 字节）；null 表示未取得。</summary>
        [ObservableProperty] private ulong? _lowSizeMiB;
        /// <summary>高位 MMIO 空间大小，单位为 MiB（1024² 字节）；null 表示未取得。</summary>
        [ObservableProperty] private ulong? _highSizeMiB;
        /// <summary>高位 MMIO 地址基址，单位为 MiB（1024² 字节）；null 表示未取得。</summary>
        [ObservableProperty] private ulong? _highBaseMiB;

        // A value snapshot must not inherit the source object's notification subscribers.
        /// <summary>复制当前值为独立模型，不复制属性变化事件订阅者。</summary>
        /// <returns>包含当前三个 MMIO 值的新模型。</returns>
        public VmMmioSettings Clone() => new()
        {
            _lowSizeMiB = _lowSizeMiB,
            _highSizeMiB = _highSizeMiB,
            _highBaseMiB = _highBaseMiB
        };

        /// <summary>通过属性设置器恢复本地模型并触发相关通知；不提交 Hyper-V 配置。</summary>
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
