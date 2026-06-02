namespace ExHyperV.Models
{
    /// <summary>单核监控数据：CpuMonitorService 周期产出，PageVM 分发到对应 VM 的 Cores。</summary>
    public class CpuCoreMetric
    {
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public float Usage { get; set; }
        public bool IsRunning { get; set; }
    }
}
