namespace ExHyperV.Models
{
    /// <summary>
    /// USB 设备原始数据（来自 usbipd-win 列表查询）。
    /// </summary>
    public class UsbDevice
    {
        public string BusId { get; set; }
        public string VidPid { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
    }
}
