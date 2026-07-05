using System.Net;
using System.Net.Sockets;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 从一串逗号/分号/换行分隔的 IP 候选里挑出最佳 IPv4：
    /// 优先 RFC1918 私网（10.* / 172.16-31.* / 192.168.*），其次非链路本地/非环回，最后兜底第一个。
    /// </summary>
    public static class Ipv4
    {
        public static string SelectBest(string ipCandidates)
        {
            if (string.IsNullOrWhiteSpace(ipCandidates)) return string.Empty;

            var parsedAddresses = ipCandidates
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeCandidate)
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                // 只认标准四段点分十进制:回写须与输入一致。IPAddress.TryParse 仍沿用 inet_aton 老语义,会把简写/整数/进制/前导零悄悄改写成另一个地址("192.168.1"→192.168.0.1、"10"→0.0.0.10),回写比对可挡下
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
