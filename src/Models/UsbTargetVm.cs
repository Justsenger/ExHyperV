namespace ExHyperV.Models
{
    /// <summary>
    /// USB 转发场景下的"目标虚拟机简表"——只装 Name + Id，
    /// 用于把 USB 设备分配到某个 VM 时的下拉框选项。
    /// 注意：与代表"完整 VM 快照"的 <see cref="VmInstance"/> 不是一回事。
    /// </summary>
    public class UsbTargetVm
    {
        public string Name { get; set; }
        public Guid Id { get; set; }
    }
}
