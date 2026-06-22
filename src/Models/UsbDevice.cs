namespace ExHyperV.Models
{
    /// <summary>USB 设备原始数据（来自 usbipd-win 列表查询，每次刷新重建，不可变）。</summary>
    public class UsbDevice
    {
        public string BusId { get; init; } = string.Empty;
        public string VidPid { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }
}
