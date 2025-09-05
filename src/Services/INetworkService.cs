using ExHyperV.Models;

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
        Task<(List<SwitchInfo> Switches, List<PhysicalAdapterInfo> PhysicalAdapters)> GetNetworkInfoAsync();

        /// <summary>
        /// 异步获取连接到特定虚拟交换机的所有网络适配器（包括虚拟机和主机）的详细状态。
        /// </summary>
        Task<List<AdapterInfo>> GetFullSwitchNetworkStateAsync(string switchName);

        /// <summary>
        /// 异步更新一个虚拟交换机的配置。
        /// </summary>
        Task UpdateSwitchConfigurationAsync(string switchName, string mode, string? adapterDescription, bool allowManagementOS);
        /// <summary>
        /// 异步创建一个新的虚拟交换机。
        /// </summary>
        Task CreateSwitchAsync(string name, string type, string? adapterDescription);

        /// <summary>
        /// 异步删除一个指定的虚拟交换机。
        /// </summary>
        Task DeleteSwitchAsync(string switchName);
    }
}