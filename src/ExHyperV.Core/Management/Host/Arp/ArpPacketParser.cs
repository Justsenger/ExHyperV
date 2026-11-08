using System.Buffers.Binary;
using System.Globalization;

namespace ExHyperV.Services;

/// <summary>从捕获接口给出的完整报文边界解析 Ethernet/IPv4 ARP 发送者。</summary>
internal static class ArpPacketParser
{
    // PacketMonitorCreateRealtimeStream 公布的 PKTMON_PACKET_TYPE 枚举值。
    private const ushort EthernetPacket = 1;
    private const ushort ArpPacket = 7;
    private const ushort ArpEtherType = 0x0806;

    internal static bool TryParse(ReadOnlySpan<byte> packet, ushort packetType,
        out string mac, out string address)
    {
        mac = string.Empty;
        address = string.Empty;

        if (packetType == EthernetPacket)
        {
            if (packet.Length < 14) return false;
            ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(12, 2));
            int offset = 14;

            // 标签中的 EtherType 指向下一层；支持 802.1Q 及 802.1ad 双层标签，
            // 不在 IPv4、未知协议或负载内部搜索类似 ARP 的字节。
            while (etherType is 0x8100 or 0x88a8)
            {
                if (packet.Length - offset < 4) return false;
                etherType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
                offset += 4;
            }
            if (etherType != ArpEtherType) return false;
            packet = packet[offset..];
        }
        else if (packetType != ArpPacket)
        {
            return false;
        }

        // Ethernet ARP：固定头 8 字节，加两组 6 字节 MAC 和 4 字节 IPv4。
        // 抓包被截断时不能把不完整报文当成完整观测；以太网填充允许保留。
        if (packet.Length < 28
            || BinaryPrimitives.ReadUInt16BigEndian(packet[..2]) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2)) != 0x0800
            || packet[4] != 6 || packet[5] != 4
            || BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) is not (1 or 2))
            return false;

        var senderMac = packet.Slice(8, 6);
        if ((senderMac[0] & 1) != 0
            || (senderMac[0] | senderMac[1] | senderMac[2] | senderMac[3] | senderMac[4] | senderMac[5]) == 0)
            return false;

        var senderIp = packet.Slice(14, 4);
        // 不把未指定、本机回环、组播或保留的高地址范围作为 VM 的单播地址。
        // 169.254/16 等合法链路本地地址仍然保留，不在此施加子网或来源优先级规则。
        if (senderIp[0] is 0 or 127 or >= 224) return false;

        mac = Convert.ToHexString(senderMac);
        address = string.Create(CultureInfo.InvariantCulture,
            $"{senderIp[0]}.{senderIp[1]}.{senderIp[2]}.{senderIp[3]}");
        return true;
    }
}
