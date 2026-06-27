    using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class DeviceViewModel : ObservableObject
    {
        private readonly DeviceInfo _device;
        public List<AssignmentTarget> AssignmentOptions { get; }
        public DeviceViewModel(DeviceInfo device, List<string> allVmNames)
        {
            _device = device;
            IconGlyph = DeviceIcons.GetGlyph(device.ClassType, device.FriendlyName);
            // 主机：显示“主机（物理机）”以与同名虚拟机区分；身份用 HostKey（零宽空格，永不与真实虚拟机名相等）
            AssignmentOptions = new List<AssignmentTarget>
            {
                new(Properties.Resources.Host_Physical, ExHyperV.Services.PCIeService.HostKey)
            };
            if (allVmNames != null)
                AssignmentOptions.AddRange(allVmNames.Select(n => new AssignmentTarget(n, n))); // 虚拟机：显示名即身份名
        }
        public string FriendlyName => _device.FriendlyName;
        public string ClassType => _device.ClassType;
        public string InstanceId => _device.InstanceId;
        public string Path => _device.Path;
        public string Vendor => _device.Vendor;
        public string Status
        {
            get => _device.Status;
            set => SetProperty(_device.Status, value, _device, (d, v) => d.Status = v);
        }
        public string IconGlyph { get; }
    }

    /// <summary>PCIe 分配下拉项：Display 用于界面显示，Key 用于身份判别（主机用 PCIeService.HostKey，虚拟机用其名）。</summary>
    public record AssignmentTarget(string Display, string Key);
}