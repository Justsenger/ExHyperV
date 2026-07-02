namespace ExHyperV.Models
{
    /// <summary>宿主物理磁盘（用于"添加物理盘到 VM"的列表选择）。全部硬盘都列出，不可直通的带状态标签并在 UI 置灰。</summary>
    public class HostDiskInfo
    {
        public int Number { get; init; }
        public string FriendlyName { get; init; } = string.Empty;
        public double SizeGB { get; init; }

        /// <summary>状态标签（可直通 / 系统盘 / 已分配给某 VM / 在线需脱机 / 只读 / USB 不支持）。</summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>能否直通。false 时 UI 置灰、不可选中。</summary>
        public bool CanPassthrough { get; init; }
    }
}
