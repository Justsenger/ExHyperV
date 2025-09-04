using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Xml.Linq;

namespace ExHyperV.Models
{
    public class DhcpConfig
    {
        public bool Enabled { get; set; }
        public string InterfaceName { get; set; }
        public IPAddress PoolStart { get; set; }
        public IPAddress PoolEnd { get; set; }
        public IPAddress ServerAddress { get; set; }
        public IPAddress SubnetMask { get; set; }
        public IPAddress Router { get; set; }
        public List<IPAddress> DnsServers { get; set; }

        private DhcpConfig()
        {
            DnsServers = new List<IPAddress>();
        }

        public static DhcpConfig Load(string filePath)
        {
            try
            {
                var doc = XDocument.Load(filePath);
                var dhcpElement = doc.Root?.Element("DHCP");

                if (dhcpElement == null)
                {
                    Console.WriteLine("警告: 配置文件中未找到 <DHCP> 配置节。");
                    return null;
                }

                var config = new DhcpConfig
                {
                    Enabled = bool.TryParse(dhcpElement.Element("Enabled")?.Value, out var enabled) && enabled,
                    InterfaceName = dhcpElement.Element("InterfaceName")?.Value,
                    PoolStart = IPAddress.Parse(dhcpElement.Element("PoolStart")?.Value ?? "0.0.0.0"),
                    PoolEnd = IPAddress.Parse(dhcpElement.Element("PoolEnd")?.Value ?? "0.0.0.0"),
                    ServerAddress = IPAddress.Parse(dhcpElement.Element("ServerAddress")?.Value ?? "0.0.0.0"),
                    SubnetMask = IPAddress.Parse(dhcpElement.Element("SubnetMask")?.Value ?? "0.0.0.0"),
                    Router = IPAddress.Parse(dhcpElement.Element("Router")?.Value ?? "0.0.0.0"),
                };

                var dnsElements = dhcpElement.Element("DNSServers")?.Elements("DNS");
                if (dnsElements != null)
                {
                    foreach (var dns in dnsElements)
                    {
                        if (IPAddress.TryParse(dns.Value, out var dnsIp))
                        {
                            config.DnsServers.Add(dnsIp);
                        }
                    }
                }

                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误: 加载DHCP配置失败 - {ex.Message}");
                return null;
            }
        }
    }
}