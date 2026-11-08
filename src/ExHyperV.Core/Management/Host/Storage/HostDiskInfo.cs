namespace ExHyperV.Models
{
    /// <summary>宿主磁盘的身份、容量与直通候选状态，包含已挂载的虚拟磁盘。</summary>
    public class HostDiskInfo
    {
        /// <summary>该磁盘的身份摘要，用于后续联机、只读和分区盘符修改。</summary>
        /// <fielddoc>
        /// <meaning>该磁盘的身份摘要，用于后续联机、只读和分区盘符修改。</meaning>
        /// <references><see cref="Services.HostDiskReference"/>。</references>
        /// </fielddoc>
        public Services.HostDiskReference Reference => new(Number, UniqueId, SerialNumber, SizeBytes);
        /// <summary>Windows 为该磁盘分配的编号。</summary>
        /// <fielddoc>
        /// <meaning>Windows 为该磁盘分配的编号。</meaning>
        /// <values kind="format">非负整数，例如 0 表示磁盘 0。</values>
        /// </fielddoc>
        public int Number { get; init; }
        /// <summary>磁盘型号或友好名称。</summary>
        /// <fielddoc>
        /// <meaning>磁盘型号或友好名称。</meaning>
        /// </fielddoc>
        public string FriendlyName { get; init; } = string.Empty;
        /// <summary>以 GiB 表示的磁盘容量。</summary>
        /// <fielddoc>
        /// <meaning>以 GiB 表示的磁盘容量。</meaning>
        /// <values kind="format">1 GiB = 1,073,741,824 字节，保留两位小数。精确字节数见 SizeBytes。</values>
        /// </fielddoc>
        public double SizeGiB { get; init; }
        /// <summary>磁盘的总容量。</summary>
        /// <fielddoc>
        /// <meaning>磁盘的总容量。</meaning>
        /// <values kind="format">单位为字节。</values>
        /// </fielddoc>
        public long SizeBytes { get; init; }
        /// <summary>存储系统提供的磁盘标识，用于核对修改目标。</summary>
        /// <fielddoc>
        /// <meaning>存储系统提供的磁盘标识，用于核对修改目标。</meaning>
        /// </fielddoc>
        public string UniqueId { get; init; } = string.Empty;
        /// <summary>磁盘的硬件序列号。</summary>
        /// <fielddoc>
        /// <meaning>磁盘的硬件序列号。</meaning>
        /// <values kind="format">序列号文本；未取得时为空字符串。</values>
        /// </fielddoc>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>磁盘的主要状态说明，按系统盘、已分配、只读等优先级选取。</summary>
        /// <fielddoc>
        /// <meaning>磁盘的主要状态说明，按系统盘、已分配、只读等优先级选取。</meaning>
        /// <values kind="format">本地化文本。</values>
        /// </fielddoc>
        public string Status { get; init; } = string.Empty;

        /// <summary>该磁盘是否可以进入直通前的进一步检查。</summary>
        /// <fielddoc>
        /// <meaning>该磁盘是否可以进入直通前的进一步检查。</meaning>
        /// <values kind="options">true：未识别为系统盘、启动盘或已分配磁盘；false：被上述条件排除。</values>
        /// </fielddoc>
        public bool CanPassthrough { get; init; }
    }
}
