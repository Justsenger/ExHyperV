namespace ExHyperV.Models
{
    /// <summary>单核监控数据：CpuMonitorService 周期产出，PageVM 分发到对应 VM 的 Cores。</summary>
    public class VmCoreMetric
    {
        public string VmName { get; init; } = string.Empty;
        public int CoreId { get; init; }
        public float Usage { get; init; }
    }
}
