using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Properties;
using ExHyperV.Tools;
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

        public string DisplayName
        {
            get
            {
                if (DiskType == "Physical" && !string.IsNullOrEmpty(DiskModel))
                    return DiskModel;

                if (DiskType == "Virtual" && !string.IsNullOrEmpty(PathOrDiskNumber))
                {
                    try { return Path.GetFileName(PathOrDiskNumber); }
                    catch { return Resources.Model_Drive_VirtualDisk; }
                }

                return DriveType == "HardDisk" ? Resources.Model_Drive_VirtualHardDisk : Resources.Model_Drive_OpticalDrive;
            }
        }

        public string SourceTypeDisplayName => DiskType == "Physical" ? Resources.Model_Drive_SourcePhysical : Resources.Model_Drive_SourceVirtual;

        public string Icon => DriveType == "HardDisk" ? "\uEDA2" : "\uE958";

        public string SizeDisplay
        {
            get
            {
                if (_diskSizeGB <= 0)
                {
                    return "";
                }

                const long bytesPerGb = 1024L * 1024L * 1024L;
                long totalBytes = (long)(_diskSizeGB * bytesPerGb);

                return Utils.FormatBytes(totalBytes);
            }
        }
    }
}