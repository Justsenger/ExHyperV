using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class DeviceViewModel : ObservableObject
    {
        private readonly DeviceInfo _device;
        public List<string> AssignmentOptions { get; }
        public DeviceViewModel(DeviceInfo device, List<string> allVmNames)
        {
            _device = device;
            IconGlyph = Utils.GetIconPath(device.ClassType, device.FriendlyName);
            AssignmentOptions = new List<string> { Resources.Host }; // 1. 首先添加“主机”
            if (allVmNames != null)
            {
                AssignmentOptions.AddRange(allVmNames); // 2. 然后添加所有虚拟机名称
            }
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
}