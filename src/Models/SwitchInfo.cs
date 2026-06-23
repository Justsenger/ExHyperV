namespace ExHyperV.Models
{
    /// <summary>Hyper-V 虚拟交换机的三种网络模式：Bridge=桥接(外部) / NAT=内部+ICS共享 / Isolated=无上游(私有/内部)。</summary>
    public enum SwitchMode { Bridge, NAT, Isolated }

    /// <summary>Hyper-V 虚拟交换机的数据模型（HyperVSwitchService 查询产出，构造后不可变）。</summary>
    public class SwitchInfo
    {
        public string SwitchName { get; init; } = string.Empty;
        public SwitchMode SwitchType { get; init; }
        public bool AllowManagementOS { get; init; }            // 是否允许宿主 OS 共享此交换机
        public string Id { get; init; } = string.Empty;
        public string NetAdapterInterfaceDescription { get; init; } = string.Empty;
    }
}
