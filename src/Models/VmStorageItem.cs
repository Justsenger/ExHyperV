using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>
    /// VM 存储清单项（绑定 Storage Settings 列表）：物理磁盘或虚拟磁盘均统一表达。
    /// 含计算属性 DisplayName/Icon/SizeDisplay/SourceTypeDisplayName 给 UI 直接绑。
    /// </summary>
    public partial class VmStorageItem : ObservableObject
    {
        [ObservableProperty] private string _driveType = string.Empty;
        [ObservableProperty] private string _diskType = string.Empty;
        [ObservableProperty] private string _pathOrDiskNumber = string.Empty;
        [ObservableProperty] private int _controllerLocation;
        [ObservableProperty] private string _controllerType = string.Empty;
        [ObservableProperty] private int _controllerNumber;
        [ObservableProperty] private bool _isOptimizing;
        [ObservableProperty] private int _diskNumber;
        [ObservableProperty] private string _diskModel = string.Empty;

        // 物理盘直通悬空:HostResource 钉的盘已被手动联机、从可直通池(Msvm_DiskDrive)消失，直通失效(Hyper-V 显示"物理驱动器找不到"、VM 开机失败)。展开区显示失效横幅。
        [ObservableProperty]
        private bool _isPassthroughStale;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeDisplay))] // 当 DiskSizeGB 改变时通知 SizeDisplay 更新
        private double _diskSizeGB;

        [ObservableProperty] private string _serialNumber = string.Empty;

        public string DisplayName
        {
            get
            {
                if (DiskType == "Physical" && !string.IsNullOrEmpty(DiskModel))
                    return DiskModel;

                if (DiskType == "Virtual" && !string.IsNullOrEmpty(PathOrDiskNumber))
                {
                    try { return Path.GetFileName(PathOrDiskNumber); }
                    catch { return Properties.Resources.Model_Drive_VirtualDisk; }
                }

                return DriveType == "HardDisk" ? Properties.Resources.Model_Drive_VirtualHardDisk : Properties.Resources.Model_Drive_OpticalDrive;
            }
        }

        public string SourceTypeDisplayName => DiskType == "Physical" ? Properties.Resources.Model_Drive_SourcePhysical : Properties.Resources.Model_Drive_SourceVirtual;

        public string Icon => DriveType == "HardDisk" ? "" : "";

        public string SizeDisplay
        {
            get
            {
                if (DiskSizeGB <= 0) return Properties.Resources.Common_Unknown;
                if (DiskSizeGB < 1.0)
                {
                    double sizeMB = DiskSizeGB * 1024.0;
                    if (sizeMB < 1.0)
                    {
                        return $"{sizeMB * 1024.0:N0} KB";
                    }
                    return $"{sizeMB:N1} MB";
                }
                return $"{DiskSizeGB:N1} GB";
            }
        }
    }
}
