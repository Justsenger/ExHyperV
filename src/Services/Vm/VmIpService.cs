using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ExHyperV.Api;

namespace ExHyperV.Services
{
    /// <summary>
    /// VM IP 查询：优先走 WMI Msvm_GuestNetworkAdapterConfiguration（要求集成服务在线），
    /// 失败回退 ARP 缓存（StdCimV2 MSFT_NetNeighbor）。
    /// </summary>
    public static class VmIpService
    {
        /// <summary>
        /// 查询指定 VM（按名称）对应 MAC 的 IPv4 地址。
        /// 返回逗号分隔的 IPv4 列表；若 WMI/ARP 都拿不到，返回空字符串。
        /// </summary>
        public static async Task<string> Lookup(string vmName, string macAddressWithColons)
        {
            if (string.IsNullOrEmpty(vmName) || string.IsNullOrEmpty(macAddressWithColons))
                return string.Empty;

            // 路径 1：WMI Msvm_GuestNetworkAdapterConfiguration（需 guest 内集成服务在线）
            var vmGuidResp = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString() ?? string.Empty,
                WmiScope.HyperV);

            if (vmGuidResp.HasData && !string.IsNullOrEmpty(vmGuidResp.Data))
            {
                string vmGuid = vmGuidResp.Data;
                var ipResp = await WmiApi.QueryAsync(
                    "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration",
                    obj => new
                    {
                        InstanceID = obj["InstanceID"]?.ToString() ?? string.Empty,
                        IPs = obj["IPAddresses"] as string[] ?? Array.Empty<string>()
                    },
                    WmiScope.HyperV);

                if (ipResp.Success && ipResp.Data != null)
                {
                    var ips = ipResp.Data
                        .Where(x => x.InstanceID.Contains(vmGuid, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(x => x.IPs)
                        .Where(a => IPAddress.TryParse(a, out var parsed) &&
                                    parsed.AddressFamily == AddressFamily.InterNetwork)
                        .ToList();

                    if (ips.Count > 0)
                        return string.Join(", ", ips);
                }
            }

            // 路径 2：ARP 缓存回退
            return await FromArpCache(macAddressWithColons);
        }

        private static async Task<string> FromArpCache(string macWithColons)
        {
            if (string.IsNullOrEmpty(macWithColons)) return string.Empty;

            string clean = macWithColons.Replace(":", "").Replace("-", "").ToUpperInvariant();
            string formatted = Regex.Replace(clean, ".{2}", "$0-").TrimEnd('-');

            var resp = await WmiApi.QueryCimAsync(
                $"SELECT IPAddress FROM MSFT_NetNeighbor WHERE LinkLayerAddress = '{formatted}' AND AddressFamily = 2 AND State <> 0",
                obj => obj["IPAddress"]?.ToString() ?? string.Empty,
                WmiScope.StdCimV2);

            if (resp.Success && resp.Data != null)
                return resp.Data.FirstOrDefault(ip => !string.IsNullOrEmpty(ip)) ?? string.Empty;

            return string.Empty;
        }
    }
}
