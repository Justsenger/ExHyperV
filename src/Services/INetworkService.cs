using ExHyperV.Models; // 引入我们第一步创建的模型
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    /// <summary>
    /// 定义网络相关操作的服务接口。
    /// 这是 ViewModel 与底层数据获取逻辑之间的契约。
    /// </summary>
    public interface INetworkService
    {
        /// <summary>
        /// 异步获取主机的网络信息，包括所有虚拟交换机和物理网卡。
        /// </summary>
        /// <returns>一个包含交换机列表和物理网卡列表的元组。</returns>
        Task<(List<SwitchInfo> Switches, List<PhysicalAdapterInfo> PhysicalAdapters)> GetNetworkInfoAsync();

        /// <summary>
        /// 异步获取连接到特定虚拟交换机的所有网络适配器（包括虚拟机和主机）的详细状态。
        /// </summary>
        /// <param name="switchName">虚拟交换机的名称。</param>
        /// <returns>适配器信息列表。</returns>
        Task<List<AdapterInfo>> GetFullSwitchNetworkStateAsync(string switchName);

        /// <summary>
        /// 异步更新一个虚拟交换机的配置。
        /// 注意：在你的代码中，这个功能是放在 Utils 里的，这里为了演示服务层的完整性而包含它。
        /// 我们可以直接在服务实现中调用 Utils.UpdateSwitchConfigurationAsync。
        /// </summary>
        /// <param name="switchName">要更新的交换机名称。</param>
        /// <param name="mode">新的网络模式 (e.g., "Bridge", "NAT", "Isolated")。</param>
        /// <param name="adapterDescription">当模式为 Bridge 或 NAT 时，选择的上游物理网卡描述。</param>
        /// <param name="allowManagementOS">是否允许主机操作系统共享此网络适配器。</param>
        /// <param name="enableDhcp">是否启用 DHCP（此功能在你的原代码中似乎未被完全使用，但保留以保持一致）。</param>
        Task UpdateSwitchConfigurationAsync(string switchName, string mode, string? adapterDescription, bool allowManagementOS, bool enableDhcp);
    }
}