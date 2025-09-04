using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using DotNetProjects.DhcpServer;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public class DhcpService : IDisposable
    {
        private readonly DhcpConfig _config;
        private DHCPServer _server;
        private readonly Dictionary<string, IPAddress> _leases = new Dictionary<string, IPAddress>();
        private uint _nextIpSuffix;
        private uint _poolEndSuffix;

        public DhcpService(DhcpConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            var startBytes = _config.PoolStart.GetAddressBytes();
            var endBytes = _config.PoolEnd.GetAddressBytes();

            _nextIpSuffix = startBytes[3];
            _poolEndSuffix = endBytes[3];
        }

        public bool Start()
        {
            if (!_config.Enabled)
            {
                Console.WriteLine("DHCP服务在配置文件中被禁用。");
                return true;
            }

            var targetInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => x.Name == _config.InterfaceName);

            if (targetInterface == null)
            {
                Console.WriteLine($"错误: 找不到名为 '{_config.InterfaceName}' 的网络接口。");
                return false;
            }

            _server = new DHCPServer();
            _server.ServerName = "ExHyperVDHCP";
            _server.OnDataReceived += OnDhcpRequestReceived;
            _server.BroadcastAddress = IPAddress.Broadcast;
            _server.SendDhcpAnswerNetworkInterface = targetInterface;

            _server.Start();

            Console.WriteLine($"DHCP服务已在接口 '{_config.InterfaceName}' 上启动。");
            Console.WriteLine($"IP地址池: {_config.PoolStart} - {_config.PoolEnd}");
            Console.WriteLine($"服务器地址: {_config.ServerAddress}");
            return true;
        }

        private void OnDhcpRequestReceived(DHCPRequest dhcpRequest)
        {
            try
            {
                var msgType = dhcpRequest.GetMsgType();
                var macAddress = ByteArrayToString(dhcpRequest.GetChaddr());

                if (!_leases.TryGetValue(macAddress, out IPAddress clientIp))
                {
                    clientIp = GetNextAvailableIp();
                    if (clientIp == null)
                    {
                        Console.WriteLine($"警告: IP地址池已满，无法为 {macAddress} 分配地址。");
                        return;
                    }
                    _leases[macAddress] = clientIp;
                }

                Console.WriteLine($"{DateTime.Now}: 收到来自 {macAddress} 的 {msgType} 请求, 分配/确认 IP: {clientIp}");

                var replyOptions = new DHCPReplyOptions
                {
                    SubnetMask = _config.SubnetMask,
                    DomainName = "exhyperv.local",
                    ServerIdentifier = _config.ServerAddress,
                    RouterIP = _config.Router,
                    DomainNameServers = _config.DnsServers.ToArray(),
                    IPAddressLeaseTime = 3600,
                };

                if (msgType == DHCPMsgType.DHCPDISCOVER)
                {
                    dhcpRequest.SendDHCPReply(DHCPMsgType.DHCPOFFER, clientIp, replyOptions);
                }
                else if (msgType == DHCPMsgType.DHCPREQUEST)
                {
                    dhcpRequest.SendDHCPReply(DHCPMsgType.DHCPACK, clientIp, replyOptions);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理DHCP请求时出错: {ex}");
            }
        }

        private IPAddress GetNextAvailableIp()
        {
            if (_nextIpSuffix > _poolEndSuffix)
            {
                return null;
            }

            var startBytes = _config.PoolStart.GetAddressBytes();
            var newIpBytes = new byte[] { startBytes[0], startBytes[1], startBytes[2], (byte)_nextIpSuffix };

            _nextIpSuffix++;

            return new IPAddress(newIpBytes);
        }

        private static string ByteArrayToString(byte[] ar)
        {
            return string.Join(":", ar.Select(b => b.ToString("X2")));
        }

        public void Dispose()
        {
            _server?.Dispose();
        }
    }
}