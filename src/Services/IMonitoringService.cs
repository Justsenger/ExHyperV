// 文件路径: src/Services/IMonitoringService.cs

using ExHyperV.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    /// <summary>
    /// 定义获取性能监控数据的方法。
    /// </summary>
    public interface IMonitoringService
    {
        /// <summary>
        /// 异步获取宿主机 CPU 的使用率。
        /// </summary>
        Task<HostCpuUsage> GetHostCpuUsageAsync();

        /// <summary>
        /// 异步获取所有正在运行的虚拟机的 CPU 负载。
        /// </summary>
        Task<List<VmCpuUsage>> GetVmCpuUsagesAsync();
    }
}