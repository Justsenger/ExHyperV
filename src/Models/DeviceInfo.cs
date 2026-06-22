using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>可分配的 PCIe 硬件设备（PCIe 页用）。除 Status 外均 init-only 不可变。</summary>
    public partial class DeviceInfo : ObservableObject
    {
        public string FriendlyName { get; init; } = string.Empty;
        public string ClassType { get; init; } = string.Empty;
        public string InstanceId { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Vendor { get; init; } = string.Empty;

        /// <summary>设备当前分配目标：主机（Resources.Host）或某 VM 名；用户在 PCIe 页可改。</summary>
        [ObservableProperty] private string _status = string.Empty;
    }
}
