namespace ExHyperV.Models
{
    /// <summary>宿主当前存在的显示适配器，由 HostGpuService.GetHostGpusAsync 查询产生；IsPartitionable 表示当前是否存在可分区资源。</summary>
    public class HostGpuInfo
    {
        /// <summary>宿主显示适配器的设备名称。</summary>
        /// <fielddoc>
        /// <meaning>宿主显示适配器的设备名称。</meaning>
        /// </fielddoc>
        public string Name { get; init; } = string.Empty;          // 显卡名称
        /// <summary>显示适配器的制造商名称，由 Windows 设备属性或 PCI 厂商信息提供。</summary>
        /// <fielddoc>
        /// <meaning>显示适配器的制造商名称，由 Windows 设备属性或 PCI 厂商信息提供。</meaning>
        /// </fielddoc>
        public string Manu { get; init; } = string.Empty;          // 芯片商（NVIDIA/AMD，匹配图标用）
        /// <summary>显示适配器的 PnP 设备实例标识，用于匹配 GPU。</summary>
        /// <fielddoc>
        /// <meaning>显示适配器的 PnP 设备实例标识，用于匹配 GPU。</meaning>
        /// </fielddoc>
        public string InstanceId { get; init; } = string.Empty;    // 显卡实例 ID
        /// <summary>宿主为该 GPU 安装的驱动版本。</summary>
        /// <fielddoc>
        /// <meaning>宿主为该 GPU 安装的驱动版本。</meaning>
        /// <values kind="format">版本字符串。</values>
        /// </fielddoc>
        public string DriverVersion { get; init; } = string.Empty; // 驱动版本
        /// <summary>板卡厂商名称，优先使用 PCI 子系统厂商信息。</summary>
        /// <fielddoc>
        /// <meaning>板卡厂商名称，优先使用 PCI 子系统厂商信息。</meaning>
        /// </fielddoc>
        public string Vendor { get; init; } = string.Empty;        // 板卡厂商（ASUS/MSI 等，文字显示用）

        /// <summary>匹配的 Msvm_PartitionableGpu.Name；空字符串表示未找到可分区资源。</summary>
        internal string PartitionableGpuPath { get; set; } = string.Empty;
        /// <summary>当前是否找到与该 GPU 对应的可分区资源。</summary>
        /// <fielddoc>
        /// <meaning>当前是否找到与该 GPU 对应的可分区资源。</meaning>
        /// <values kind="options">true：存在可分区资源；false：未找到。</values>
        /// </fielddoc>
        public bool IsPartitionable => !string.IsNullOrWhiteSpace(PartitionableGpuPath);
        /// <summary>GPU 的专用显存容量。</summary>
        /// <fielddoc>
        /// <meaning>GPU 的专用显存容量。</meaning>
        /// <values kind="format">单位为字节；0 表示没有专用显存，null 表示未取得。</values>
        /// </fielddoc>
        public ulong? MemoryBytes { get; set; }

        /// <summary>清洗后的设备路径（优先 PartitionableGpuPath，回退 InstanceId）：去 \\?\ 前缀、截断 #{guid}、# 还原为 \。</summary>
        internal string PathDisplay
        {
            get
            {
                string rawPath = !string.IsNullOrEmpty(PartitionableGpuPath) ? PartitionableGpuPath : InstanceId;
                if (string.IsNullOrWhiteSpace(rawPath)) return Properties.Resources.Common_UnknownPath;
                try
                {
                    string cleanId = rawPath;
                    if (cleanId.StartsWith(@"\\?\")) cleanId = cleanId.Substring(4);
                    int guidIndex = cleanId.IndexOf("#{");
                    if (guidIndex != -1) cleanId = cleanId.Substring(0, guidIndex);
                    return cleanId.Replace('#', '\\');
                }
                catch { return rawPath; }
            }
        }
    }
}
