namespace ExHyperV.Models
{
    /// <summary>VM 的某个存储控制器及其挂载的全部驱动器。</summary>
    public class VmStorageControllerInfo
    {
        public string VMName { get; set; }
        public int Generation { get; set; }
        public string ControllerType { get; set; }
        public int ControllerNumber { get; set; }
        public List<AttachedDriveInfo> AttachedDrives { get; set; } = new();
    }
}
