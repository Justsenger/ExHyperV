namespace ExHyperV.Models
{
    public class VirtualMachineMemoryInfo
    {
        public string VMName { get; set; }
        public string State { get; set; }
        public bool DynamicMemoryEnabled { get; set; }
        public long StartupMB { get; set; }
        public long MinimumMB { get; set; }
        public long MaximumMB { get; set; }
        public int Buffer { get; set; } // 内存缓冲区 (%)
        public int Priority { get; set; } // 内存权重 (优先级)
    }
}