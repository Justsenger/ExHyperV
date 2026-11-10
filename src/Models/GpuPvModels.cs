namespace ExHyperV.Models
{
    /// <summary>GPU-PV 页面：主机可分区显卡卡片。</summary>
    public class GpuPvHostGpu
    {
        public GpuPvHostGpu(GpuInfo gpu, int assignedCount)
        {
            Source = gpu;
            AssignedCount = assignedCount;
        }

        public GpuInfo Source { get; }

        public string Name => Source.Name;
        public string Manu => Source.Manu;
        public string Vendor => Source.Vendor;
        public string DriverVersion => Source.DriverVersion;
        public string PathDisplay => Source.PathDisplay;
        public string PartitionableGpuPath => Source.PartitionableGpuPath;
        public int AssignedCount { get; }

        /// <summary>只有驱动注册了 GPUPARAV 接口的显卡才能作为分区源。</summary>
        public bool IsPartitionable => !string.IsNullOrEmpty(PartitionableGpuPath);

        public bool IsNotPartitionable => !IsPartitionable;

        public string MemoryDisplay
        {
            get
            {
                if (Source.MemoryBytes == 0) return "N/A";
                double gb = Source.MemoryBytes / (1024.0 * 1024.0 * 1024.0);
                return $"{gb:F1} GB";
            }
        }

        public string AssignedDisplay => AssignedCount == 0 ? "未分配" : $"已分配 {AssignedCount} 个分区";
    }

    /// <summary>GPU-PV 页面：已分配给虚拟机的分区行。</summary>
    public class GpuPvAssignment
    {
        public string VmName { get; init; } = string.Empty;
        public string AdapterId { get; init; } = string.Empty;      // Msvm_GpuPartitionSettingData.InstanceID，移除时用
        public string InstancePath { get; init; } = string.Empty;   // 分配给该分区的物理 GPU 实例路径
        public string GpuName { get; init; } = string.Empty;
        public string Manu { get; init; } = string.Empty;
        public string DriverVersion { get; init; } = string.Empty;
        public ulong MemoryBytes { get; init; }

        /// <summary>匹配到的宿主显卡；驱动复制需要它的 PartitionableGpuPath 与厂商。</summary>
        public GpuInfo? HostGpu { get; init; }

        /// <summary>同一台虚拟机只在第一行显示 MMIO/幽灵分区等整机维护入口。</summary>
        public bool ShowVmMaintenance { get; set; }

        public string MemoryDisplay
        {
            get
            {
                if (MemoryBytes == 0) return "N/A";
                double gb = MemoryBytes / (1024.0 * 1024.0 * 1024.0);
                return $"{gb:F1} GB";
            }
        }
    }
}
