namespace ExHyperV.Models
{
    /// <summary>已挂载到某控制器上的驱动器信息（由 VmStorageService 查询填充）。</summary>
    public class AttachedDriveInfo
    {
        public int ControllerLocation { get; set; }
        public string DriveType { get; set; }
        public string DiskType { get; set; }
        public string PathOrDiskNumber { get; set; }
        public string DiskModel { get; set; }
        public double DiskSizeGB { get; set; }
        public string SerialNumber { get; set; }
        public int DiskNumber { get; set; } // 专门存储物理磁盘号
    }
}
