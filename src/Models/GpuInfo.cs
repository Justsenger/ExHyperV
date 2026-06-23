namespace ExHyperV.Models
{
    /// <summary>宿主可分区显卡（GPU-PV 分配源），由 VmGpuService.GetHostGpusAsync 生产。</summary>
    public class GpuInfo
    {
        public string Name { get; init; } = string.Empty;          // 显卡名称
        public string Manu { get; init; } = string.Empty;          // 芯片商（NVIDIA/AMD，匹配图标用）
        public string InstanceId { get; init; } = string.Empty;    // 显卡实例 ID
        public string DriverVersion { get; init; } = string.Empty; // 驱动版本
        public string Vendor { get; init; } = string.Empty;        // 板卡厂商（ASUS/MSI 等，文字显示用）

        public string Pname { get; set; } = string.Empty;   // 可分区路径（GetHostGpusAsync 二次填充）
        public string Ram { get; set; } = string.Empty;     // 显存字节串，如 "4294967296"（二次填充）

        /// <summary>清洗后的设备路径（优先 Pname，回退 InstanceId）：去 \\?\ 前缀、截断 #{guid}、# 还原为 \。</summary>
        public string PathDisplay
        {
            get
            {
                string rawPath = !string.IsNullOrEmpty(Pname) ? Pname : InstanceId;
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
