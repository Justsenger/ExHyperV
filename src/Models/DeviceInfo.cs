using CommunityToolkit.Mvvm.ComponentModel;
namespace ExHyperV.Models
{
    //可分配硬件设备的数据模型。
    public class DeviceInfo : ObservableObject
    {
        public string FriendlyName { get; }
        public string ClassType { get; }
        public string InstanceId { get; }
        public string Path { get; }
        public string Vendor { get; }

        private string _status;
        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // 构造函数
        public DeviceInfo(string friendlyName, string status, string classType, string instanceId, string path, string vendor)
        {
            FriendlyName = friendlyName;
            _status = status;
            ClassType = classType;
            InstanceId = instanceId;
            Path = path;
            Vendor = vendor;
        }
    }
}