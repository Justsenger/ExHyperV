using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    public partial class UiDriveModel : ObservableObject
    {
        [ObservableProperty]
        private string _driveType;

        [ObservableProperty]
        private string _diskType;

        [ObservableProperty]
        private string _pathOrDiskNumber;

        [ObservableProperty]
        private int _controllerLocation;

        [ObservableProperty]
        private string _controllerType; // 新增

        [ObservableProperty]
        private int _controllerNumber; // 新增

        public string DisplayName => DriveType == "HardDisk" ? "硬盘" : "光驱";
        public string SourceTypeDisplayName => DiskType == "Physical" ? "物理磁盘" : "虚拟文件";
        public string Icon => DriveType == "HardDisk" ? "\uEDA2" : "\uE958";
    }
}