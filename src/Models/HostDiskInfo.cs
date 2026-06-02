namespace ExHyperV.Models
{
    /// <summary>宿主物理磁盘信息（用于添加物理磁盘到 VM 的下拉选择）。</summary>
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
