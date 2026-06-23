using System;

namespace ExHyperV.Models 
{
    public enum OperatingSystemType
    {
        Unknown,
        Windows,
        Linux,
        EFI,
        Other
    }

    public class PartitionInfo
    {
        public int PartitionNumber { get; }
        public ulong SizeInBytes { get; }
        public OperatingSystemType OsType { get; }
        public string TypeDescription { get; }

        public string DiskPath { get; set; } = string.Empty;        // 所属 VHDX 路径或物理磁盘编号
        public string DiskDisplayName { get; set; } = string.Empty; // 友好显示：如 "Disk 0 (System.vhdx)"
        public bool IsPhysicalDisk { get; set; }    // 标记是否为物理直通盘


        public PartitionInfo(int number, ulong size, OperatingSystemType osType, string typeDescription)
        {
            PartitionNumber = number;
            SizeInBytes = size;
            OsType = osType;
            TypeDescription = typeDescription;
        }

        public double SizeInGb => SizeInBytes / (1024.0 * 1024.0 * 1024.0);

        public string DisplayName => string.Format(Properties.Resources.Format_PartitionDesc, DiskDisplayName, PartitionNumber, SizeInGb);

        // 分区图标复用全局矢量资源（Vector.Windows / Vector.Linux），与全应用一致，不再用 PNG。
        public System.Windows.Media.ImageSource IconPath => OsType switch
        {
            OperatingSystemType.Windows => ExHyperV.Tools.VectorIcons.TryGet("Windows"),
            OperatingSystemType.Linux => ExHyperV.Tools.VectorIcons.TryGet("Linux"),
            _ => null
        };
    }
}