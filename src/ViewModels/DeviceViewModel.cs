using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Properties;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// 包装 DeviceInfo Model，并为其添加额外的、专门用于UI显示的属性，
    /// 例如图标字形码和它自己的可分配目标列表。
    /// </summary>
    public partial class DeviceViewModel : ObservableObject
    {
        private readonly DeviceInfo _device;

        /// <summary>
        /// 专属于此设备的菜单项列表 (例如 ["主机", "VM1", "VM2"])。
        /// </summary>
        public List<string> AssignmentOptions { get; }

        /// <summary>
        /// 构造函数，接收核心数据模型和一份完整的虚拟机名称列表。
        /// </summary>
        public DeviceViewModel(DeviceInfo device, List<string> allVmNames)
        {
            _device = device;
            IconGlyph = Utils.GetIconPath(device.ClassType, device.FriendlyName);

            // 在这里构建专属于此设备的菜单项列表
            AssignmentOptions = new List<string> { Resources.Host }; // 1. 首先添加“主机”
            if (allVmNames != null)
            {
                AssignmentOptions.AddRange(allVmNames); // 2. 然后添加所有虚拟机名称
            }
        }

        // 通过 "=>" 代理来自内部 _device 对象的只读属性
        public string FriendlyName => _device.FriendlyName;
        public string ClassType => _device.ClassType;
        public string InstanceId => _device.InstanceId;
        public string Path => _device.Path;
        public string Vendor => _device.Vendor;

        // 对于需要通知UI更新的 Status 属性，需要完整的实现
        public string Status
        {
            get => _device.Status;
            set => SetProperty(_device.Status, value, _device, (d, v) => d.Status = v);
        }

        /// <summary>
        /// 为UI准备的图标字形码字符串。
        /// </summary>
        public string IconGlyph { get; }
    }
}