// 文件路径: src/Models/VmCpuUsage.cs

using System.Collections.Generic;

namespace ExHyperV.Models
{
    /// <summary>
    /// 存储单个 Hyper-V 虚拟机 CPU 的性能数据。
    /// </summary>
    public class VmCpuUsage
    {
        /// <summary>
        /// 虚拟机的名称。
        /// </summary>
        public string VmName { get; set; }

        /// <summary>
        /// 所有 vCPU 的平均负载 (%)。
        /// </summary>
        public double AverageUsage { get; set; }

        /// <summary>
        /// 每个 vCPU 的负载 (%) 列表。
        /// </summary>
        public List<double> VcpuUsages { get; set; } = new();
    }
}