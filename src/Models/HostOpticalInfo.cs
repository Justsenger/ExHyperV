namespace ExHyperV.Models
{
    /// <summary>宿主物理光驱（用于"物理光驱直通到第 1 代 VM"的下拉选择）。
    /// 仅第 1 代支持物理光驱直通——微软 by design，第 2 代只能挂 ISO。</summary>
    public class HostOpticalInfo
    {
        public string PnpDeviceId { get; init; } = string.Empty;   // 直通时作为 DVD 的 SASD HostResource
        public string Drive { get; init; } = string.Empty;          // 盘符，如 "E:"（显示用）
        public string Model { get; init; } = string.Empty;          // 型号/Caption（显示用）
    }
}
