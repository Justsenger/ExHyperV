
namespace ExHyperV.Models
{
    public class AttachedDriveInfo
    {
        public int ControllerLocation { get; set; }
        public string DriveType { get; set; } 
        public string DiskType { get; set; }  
        public string PathOrDiskNumber { get; set; }
        public string DiskModel { get; set; }
        public double DiskSizeGB { get; set; }
        public string SerialNumber { get; set; }
        public int DiskNumber { get; set; } // 新增：专门存储物理磁盘号
    }

    public class VmStorageControllerInfo
    {
        public string VMName { get; set; }
        public int Generation { get; set; }
        public string ControllerType { get; set; } 
        public int ControllerNumber { get; set; }
        public List<AttachedDriveInfo> AttachedDrives { get; set; } = new();
    }
    public class HostDiskInfo
    {
        public int Number { get; set; }
        public string FriendlyName { get; set; }
        public double SizeGB { get; set; }
        public bool IsOffline { get; set; }
        public bool IsSystem { get; set; }
        public string OperationalStatus { get; set; }
    }
}