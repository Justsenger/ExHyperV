using System;

namespace ExHyperV.Models
{
    /// <summary>扫描磁盘分区得到的系统或分区用途分类。</summary>
    public enum OperatingSystemType
    {
        /// <summary>未能识别系统类型。</summary>
        Unknown,
        /// <summary>分区类型是否属于 Windows 数据分区。</summary>
        Windows,
        /// <summary>分区类型是否属于 Linux 用途。</summary>
        Linux,
        /// <summary>EFI 系统分区。</summary>
        EFI,
        /// <summary>已识别为其他用途的分区。</summary>
        Other
    }

    /// <summary>磁盘分区的扫描结果；可补充所属磁盘信息。</summary>
    public class DiskPartitionInfo
    {
        internal Guid ScannedVmId { get; set; }
        internal string ScannedDiskPath { get; set; } = string.Empty;
        internal bool ScannedPhysical { get; set; }
        internal ExHyperV.Services.HostDiskSnapshot? ScannedDiskIdentity { get; set; }
        /// <summary>扫描器按分区表位置分配的编号；GPT 条目和 MBR 主分区从 1 开始，MBR 逻辑分区从 5 开始。</summary>
        /// <fielddoc><meaning>分区在磁盘分区表中的编号。</meaning><values kind="format">GPT 条目和 MBR 主分区从 1 开始；MBR 逻辑分区从 5 开始。</values></fielddoc>
        public int PartitionNumber { get; }
        /// <summary>分区容量，单位为字节。</summary>
        /// <fielddoc><meaning>分区容量。</meaning><values kind="format">非负整数，单位为字节。</values></fielddoc>
        public ulong SizeInBytes { get; }
        /// <summary>由分区类型 GUID 或 MBR 类型号推断的用途，不检查分区中的系统文件。</summary>
        public OperatingSystemType OsType { get; }
        /// <summary>文件系统或分区类型的显示说明。</summary>
        public string TypeDescription { get; }

        /// <summary>虚拟磁盘文件路径；物理直通盘则为十进制磁盘编号字符串。</summary>
        /// <fielddoc><meaning>分区所属的磁盘。</meaning><values kind="format">虚拟磁盘使用宿主文件路径，例如 <c>D:\VMs\Disk.vhdx</c>；物理直通盘使用十进制磁盘编号，例如 <c>2</c>。结合 <see cref="IsPhysicalDisk"/> 区分。</values></fielddoc>
        public string DiskPath { get; set; } = string.Empty;        // 所属 VHDX 路径或物理磁盘编号
        /// <summary>所属磁盘的友好显示名称。</summary>
        public string DiskDisplayName { get; set; } = string.Empty; // 友好显示：如 "Disk 0 (System.vhdx)"
        /// <summary>是否为物理直通磁盘上的分区。</summary>
        /// <fielddoc><meaning>所属磁盘的来源类型。</meaning><values kind="options"><c>true</c>：物理直通盘；<c>false</c>：虚拟磁盘文件。</values></fielddoc>
        public bool IsPhysicalDisk { get; set; }    // 标记是否为物理直通盘

        // BitLocker 加密分区(分区首扇区 OEM ID = "-FVE-FS-")。分区类型 GUID 层面加密卷与普通
        // 基本数据分区无差别、挂载后卷的文件系统字段表现也不一,只有首扇区签名可靠;
        // 上层据此明确提示"分区已加密,请先在主机解锁",替代误导性的"不是有效的系统分区"。
        /// <summary>是否检测到 BitLocker 分区首扇区签名；为 true 时 GPU 部署要求先解锁。</summary>
        /// <fielddoc><meaning>根据分区首扇区识别 BitLocker；GPU 部署前需要解锁此类分区。</meaning><values kind="options"><c>true</c>：检测到 BitLocker 签名；<c>false</c>：未检测到。</values></fielddoc>
        public bool IsBitLocker { get; set; }


        /// <summary>建立分区事实记录；磁盘路径和物理盘标记由调用方另行填充。</summary>
        /// <param name="number">分区编号。</param>
        /// <param name="size">分区容量，单位为字节。</param>
        /// <param name="osType">扫描识别的系统类型。</param>
        /// <param name="typeDescription">分区类型显示说明。</param>
        public DiskPartitionInfo(int number, ulong size, OperatingSystemType osType, string typeDescription)
        {
            PartitionNumber = number;
            SizeInBytes = size;
            OsType = osType;
            TypeDescription = typeDescription;
        }

        /// <summary>按 1024³ 字节换算的分区容量，单位为 GiB。</summary>
        /// <fielddoc><meaning>换算为 GiB 的分区容量。</meaning><values kind="format">单位为 GiB，1 GiB = 1,073,741,824 字节。</values></fielddoc>
        public double SizeGiB => SizeInBytes / (1024.0 * 1024.0 * 1024.0);

        /// <summary>包含磁盘名称、分区编号和容量的本地化显示文本。</summary>
        public string DisplayName => string.Format(Properties.Resources.Format_PartitionDesc, DiskDisplayName, PartitionNumber, SizeGiB);

    }
}
