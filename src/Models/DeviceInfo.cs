using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>可分配的 PCIe 硬件设备（PCIe 页用）。除 Status 外均 init-only 不可变。</summary>
    public partial class DeviceInfo : ObservableObject
    {
        public string FriendlyName { get; init; } = string.Empty;
        /// <summary>Windows 驱动提供的原始 PnP 类别，供直通逻辑判断。</summary>
        public string ClassType { get; init; } = string.Empty;
        /// <summary>界面类别；System 设备可附带其非 PCI 子设备的类别集合。</summary>
        public string DisplayClassType { get; init; } = string.Empty;
        public string InstanceId { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Vendor { get; init; } = string.Empty;

        /// <summary>设备当前分配目标：主机（Resources.Host）或某 VM 名；用户在 PCIe 页可改。</summary>
        [ObservableProperty] private string _status = string.Empty;
    }
}
