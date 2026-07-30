using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>虚拟机 MMIO 地址空间设置（Msvm_VirtualSystemSettingData，单位 MB）。</summary>
    public partial class VmMmioSettings : ObservableObject
    {
        [ObservableProperty] private ulong? _lowSizeMb;
        [ObservableProperty] private ulong? _highSizeMb;
        [ObservableProperty] private ulong? _highBaseMb;

        public VmMmioSettings Clone() => (VmMmioSettings)MemberwiseClone();

        public void Restore(VmMmioSettings other)
        {
            if (other is null) return;
            LowSizeMb = other.LowSizeMb;
            HighSizeMb = other.HighSizeMb;
            HighBaseMb = other.HighBaseMb;
        }
    }
}
