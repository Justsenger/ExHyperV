using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services;

namespace ExHyperV.Models
{
    /// <summary>
    /// VM 存储清单项：物理磁盘或虚拟磁盘均统一表达。
    /// 提供介质身份、容量、控制器位置与当前配置。
    /// </summary>
    public partial class VmStorageAttachment : ObservableObject
    {
        private string _selectionToken = string.Empty;
        /// <summary>当前 VM、挂接和介质配置的不透明身份摘要；移除时原样回传，可随模型序列化，不得自行构造或修改。</summary>
        /// <remarks>这不是授权凭证，也不包含可调用的 WMI 路径。配置改变后旧值失效；未查询而手工构造的模型不能用于移除。无法识别完全相同配置的外部重建。</remarks>
        public string SelectionToken { get => _selectionToken; init => _selectionToken = value; }

        private string _deviceToken = string.Empty;
        /// <summary>VM、控制器和驱动器的身份摘要；修改介质路径时原样回传，可序列化。</summary>
        /// <remarks>不包含当前介质路径，同一驱动器的路径修改仍按本次请求覆盖。不得自行构造；无法识别后端复用完全相同设备身份的重建。移除整个设备仍使用含介质配置的 SelectionToken。</remarks>
        public string DeviceToken { get => _deviceToken; init => _deviceToken = value; }

        internal VmStorageSelection? Selection { get; private set; }
        internal void AttachSelection(VmStorageSelection selection)
        {
            Selection = selection;
            _selectionToken = selection.IsValid ? selection.Token : string.Empty;
            _deviceToken = selection.IsValid ? selection.DeviceToken : string.Empty;
        }

        /// <summary>驱动器类别：HardDisk 为硬盘，DvdDrive 为光驱。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasIsoMedia))]
        private string _driveType = string.Empty;
        /// <summary>介质来源类别：Physical 为物理设备，Virtual 为文件介质，Empty 为未挂载介质。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasIsoMedia))]
        private string _diskType = string.Empty;
        /// <summary>虚拟介质文件路径，或 PhysicalDisk 加宿主磁盘编号的文本；空光驱可为空。数字编号另见 DiskNumber。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasIsoMedia))]
        private string _pathOrDiskNumber = string.Empty;
        /// <summary>磁盘在控制器上的槽位编号。</summary>
        [ObservableProperty] private int _controllerLocation;
        /// <summary>控制器类型标识，例如 IDE 或 SCSI。</summary>
        [ObservableProperty] private string _controllerType = string.Empty;
        /// <summary>控制器编号。</summary>
        [ObservableProperty] private int _controllerNumber;
        /// <summary>宿主物理磁盘编号，用于物理盘来源。</summary>
        [ObservableProperty] private int _diskNumber;
        /// <summary>物理磁盘型号的显示名称。</summary>
        [ObservableProperty] private string _diskModel = string.Empty;

        // 物理盘直通悬空:HostResource 钉的盘已被手动联机、从可直通池(Msvm_DiskDrive)消失，直通失效(Hyper-V 显示"物理驱动器找不到"、VM 开机失败)。展开区显示失效横幅。
        /// <summary>物理盘直通引用是否失效，例如磁盘被联机后从可直通资源池消失。</summary>
        [ObservableProperty]
        private bool _isPassthroughStale;

        /// <summary>物理设备为宿主磁盘容量，文件介质为文件长度，单位 GiB（1024³ 字节）；文件长度不是虚拟容量或来宾已用空间，默认 0 也可能表示未读到。</summary>
        [ObservableProperty]
        private double _diskSizeGiB;

        /// <summary>物理磁盘序列号。</summary>
        [ObservableProperty] private string _serialNumber = string.Empty;

        /// <summary>是否为已设置非空介质路径的虚拟光驱。</summary>
        public bool HasIsoMedia => DriveType == "DvdDrive" && DiskType == "Virtual"
            && !string.IsNullOrWhiteSpace(PathOrDiskNumber);

    }
}
