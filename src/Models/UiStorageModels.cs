using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace ExHyperV.Models
{
    public partial class UiDriveModel : ObservableObject
    {
        [ObservableProperty] private string _driveType;
        [ObservableProperty] private string _diskType;
        [ObservableProperty] private string _pathOrDiskNumber;
        [ObservableProperty] private int _controllerLocation;
        [ObservableProperty] private string _controllerType;
        [ObservableProperty] private int _controllerNumber;

        [ObservableProperty] private int _diskNumber;
        [ObservableProperty] private string _diskModel;
        [ObservableProperty] private double _diskSizeGB;
        [ObservableProperty] private string _serialNumber;

        // 增强版显示名称
        public string DisplayName
        {
            get
            {
                if (DiskType == "Physical" && !string.IsNullOrEmpty(DiskModel))
                    return DiskModel;

                if (DiskType == "Virtual" && !string.IsNullOrEmpty(PathOrDiskNumber))
                {
                    try { return Path.GetFileName(PathOrDiskNumber); }
                    catch { return "虚拟磁盘"; }
                }

                return DriveType == "HardDisk" ? "虚拟硬盘" : "光驱";
            }
        }

        public string SourceTypeDisplayName => DiskType == "Physical" ? "物理设备" : "虚拟文件";
        public string Icon => DriveType == "HardDisk" ? "\uEDA2" : "\uE958";

        // 统一容量显示
        public string SizeDisplay => DiskSizeGB > 0 ? $"{DiskSizeGB} GB" : "";
    }
}