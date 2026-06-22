namespace ExHyperV.Models
{
    /// <summary>Hyper-V 虚拟交换机的数据模型（HyperVSwitchService 查询产出，构造后不可变）。</summary>
    public class SwitchInfo
    {
        public string SwitchName { get; init; } = string.Empty;
        public string SwitchType { get; init; } = string.Empty;
        public bool AllowManagementOS { get; init; }            // 是否允许宿主 OS 共享此交换机
        public string Id { get; init; } = string.Empty;
        public string NetAdapterInterfaceDescription { get; init; } = string.Empty;
    }
}
