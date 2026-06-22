namespace ExHyperV.Models
{
    /// <summary>
    /// USB 转发的"目标虚拟机简表"：Name 供下拉/按名查找，Id(Guid) 供 VMBus 隧道（StartTunnelAsync）。
    /// 与代表"完整 VM 快照"的 <see cref="VmInstance"/> 不是一回事；构造后不可变。
    /// </summary>
    public class UsbTargetVm
    {
        public string Name { get; init; } = string.Empty;
        public Guid Id { get; init; }
    }
}
