using System.Net;
using System.Net.Sockets;

namespace ExHyperV.Tools
{
    /// <summary>从文本候选集合选择有效 IPv4，优先私网地址，再考虑非链路本地/非环回地址。</summary>
    internal static class Ipv4
    {
        /// <summary>解析逗号、分号或换行分隔的地址候选，规范化后按优先级选择 IPv4。</summary>
        /// <returns>最佳 IPv4；没有有效候选时为空字符串。</returns>
        /// <param name="ipCandidates">地址候选文本；可含外围方括号和 CIDR 后缀，IPv6 与 IPv4 缩写会被过滤。</param>
        public static string SelectBest(string ipCandidates)
        {
            if (string.IsNullOrWhiteSpace(ipCandidates)) return string.Empty;

            var parsedAddresses = ipCandidates
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeCandidate)
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                // TryParse 沿用 inet_aton 老语义,"192.168.1"、"10" 等简写会被解析成别的地址;回写与输入一致才收
                .Select(candidate => IPAddress.TryParse(candidate, out var addr)
                                     && addr.AddressFamily == AddressFamily.InterNetwork
                                     && addr.ToString() == candidate ? addr : null)
                .Where(addr => addr != null)
                .Cast<IPAddress>()
                .Distinct()
                .ToList();

            if (parsedAddresses.Count == 0) return string.Empty;

            var preferred = parsedAddresses.FirstOrDefault(IsRfc1918Private)
                ?? parsedAddresses.FirstOrDefault(addr => !IsLinkLocalOrLoopback(addr))
                ?? parsedAddresses[0];

            return preferred.ToString();
        }

        private static string NormalizeCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;
            string trimmed = candidate.Trim().Trim('[', ']');
            int cidrIndex = trimmed.IndexOf('/');
            if (cidrIndex > 0) trimmed = trimmed.Substring(0, cidrIndex);
            return trimmed.Trim();
        }

        private static bool IsLinkLocalOrLoopback(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254));
        }

        private static bool IsRfc1918Private(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return false;
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            return bytes[0] == 192 && bytes[1] == 168;
        }
    }
}
