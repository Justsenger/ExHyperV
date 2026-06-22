namespace ExHyperV.Models
{
    /// <summary>宿主物理磁盘（用于"添加物理盘到 VM"的下拉选择）。</summary>
    public class HostDiskInfo
    {
        public int Number { get; init; }
        public string FriendlyName { get; init; } = string.Empty;
        public double SizeGB { get; init; }
    }
}
