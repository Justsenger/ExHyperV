namespace ExHyperV.Models
{
    /// <summary>一次 CPU 核心采样；先检查 Sample.IsValid，再使用 Usage。虚拟机使用 GUID 归属，名称只用于显示。</summary>
    public class VmCpuCoreMetric
    {
        /// <summary>所属虚拟机的稳定 GUID；宿主核心为 null。</summary>
        public Guid? VmId { get; init; }
        /// <summary>是否为宿主逻辑处理器；不通过 VmName 的保留字判断。</summary>
        public bool IsHost { get; init; }
        /// <summary>该采样所属的虚拟机显示名称。</summary>
        public string VmName { get; init; } = string.Empty;
        /// <summary>虚拟处理器或宿主逻辑处理器编号；无法读取核心数量时为 -1，并返回无效样本。</summary>
        public int CoreId { get; init; }
        /// <summary>使用率百分比；Sample.IsValid 为 true 时有效。</summary>
        public float Usage { get; init; }
        /// <summary>本次采样的有效性、观测时间与问题说明。</summary>
        public PerformanceSampleInfo Sample { get; init; } = new(PerformanceSampleState.NotSampled, default);
    }
}
