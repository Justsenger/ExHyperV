namespace ExHyperV.Models
{
    /// <summary>具有 PnP 身份的宿主光驱查询结果；物理光驱配置用于第 1 代虚拟机，第 2 代使用 ISO 映像。</summary>
    public class HostOpticalDriveInfo
    {
        /// <summary>物理光驱的 PnP 设备标识，用于第 1 代虚拟机的光驱直通。</summary>
        /// <fielddoc>
        /// <meaning>物理光驱的 PnP 设备标识，用于第 1 代虚拟机的光驱直通。</meaning>
        /// </fielddoc>
        public string PnpDeviceId { get; init; } = string.Empty;   // 直通时作为 DVD 的 SASD HostResource
        /// <summary>宿主为光驱分配的盘符。</summary>
        /// <fielddoc>
        /// <meaning>宿主为光驱分配的盘符。</meaning>
        /// <values kind="format">字母与冒号，例如 E:。</values>
        /// </fielddoc>
        public string Drive { get; init; } = string.Empty;          // 盘符，如 "E:"（显示用）
        /// <summary>光驱型号或设备显示名称。</summary>
        /// <fielddoc>
        /// <meaning>光驱型号或设备显示名称。</meaning>
        /// </fielddoc>
        public string Model { get; init; } = string.Empty;          // 型号/Caption（显示用）
    }
}
