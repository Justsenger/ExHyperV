using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>
    /// 代表一个可分配硬件设备的核心数据模型 (Model)。
    /// 这是我们应用的“图纸”，只定义一个“设备”应该包含哪些信息。
    /// </summary>
    public class DeviceInfo : ObservableObject
    {
        // 这些属性在设备被识别后通常是固定不变的，所以设为只读。
        public string FriendlyName { get; }
        public string ClassType { get; }
        public string InstanceId { get; }
        public string Path { get; }
        public string Vendor { get; }

        // 这个私有字段用来存储 Status 的值。
        private string _status;

        /// <summary>
        /// 获取或设置设备的当前分配状态 (例如 "Host", "VMName", "Removed")。
        /// 它被设计成“可通知的”，这样当它的值在后台被更新后，UI可以自动刷新。
        /// </summary>
        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // 构造函数：用来创建一个新的 DeviceInfo 实例。
        public DeviceInfo(string friendlyName, string status, string classType, string instanceId, string path, string vendor)
        {
            FriendlyName = friendlyName;
            _status = status; // 直接为私有字段赋值
            ClassType = classType;
            InstanceId = instanceId;
            Path = path;
            Vendor = vendor;
        }
    }
}