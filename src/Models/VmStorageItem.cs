using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Properties;

namespace ExHyperV.Models
{
    /// <summary>
    /// VM 存储清单项（绑定 Storage Settings 列表）：物理磁盘或虚拟磁盘均统一表达。
    /// 含计算属性 DisplayName/Icon/SizeDisplay/SourceTypeDisplayName 给 UI 直接绑。
    /// </summary>
    public partial class VmStorageItem : ObservableObject
    {
        [ObservableProperty] private string _driveType;
        [ObservableProperty] private string _diskType;
        [ObservableProperty] private string _pathOrDiskNumber;
        [ObservableProperty] private int _controllerLocation;
        [ObservableProperty] private string _controllerType;
        [ObservableProperty] private int _controllerNumber;
        [ObservableProperty] private bool _isOptimizing;
        [ObservableProperty] private int _diskNumber;
        [ObservableProperty] private string _diskModel;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeDisplay))] // 当 DiskSizeGB 改变时通知 SizeDisplay 更新
        private double _diskSizeGB;

        [ObservableProperty] private string _serialNumber;

        public string DisplayName
        {
            get
            {
                if (_diskType == "Physical" && !string.IsNullOrEmpty(_diskModel))
                    return _diskModel;

                if (_diskType == "Virtual" && !string.IsNullOrEmpty(_pathOrDiskNumber))
                {
                    try { return Path.GetFileName(_pathOrDiskNumber); }
                    catch { return Resources.Model_Drive_VirtualDisk; }
                }

                return _driveType == "HardDisk" ? Resources.Model_Drive_VirtualHardDisk : Resources.Model_Drive_OpticalDrive;
            }
        }

        public string SourceTypeDisplayName => _diskType == "Physical" ? Resources.Model_Drive_SourcePhysical : Resources.Model_Drive_SourceVirtual;

        public string Icon => _driveType == "HardDisk" ? "" : "";

        public string SizeDisplay
        {
            get
            {
                if (DiskSizeGB <= 0) return Resources.Common_Unknown;
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
