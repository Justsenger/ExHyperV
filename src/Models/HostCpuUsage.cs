// 文件路径: src/Models/HostCpuUsage.cs

using System.Collections.Generic;

namespace ExHyperV.Models
{
    /// <summary>
    /// 存储宿主机 CPU 的性能数据。
    /// </summary>
    public class HostCpuUsage
    {
        /// <summary>
        /// CPU 总体使用率 (%)。
        /// </summary>
        public double TotalUsage { get; set; }

        /// <summary>
        /// 每个逻辑核心的使用率 (%) 列表。
        /// </summary>
        public List<double> CoreUsages { get; set; } = new();
    }
}