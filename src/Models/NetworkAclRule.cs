namespace ExHyperV.Models
{
    /// <summary>虚拟网络端口的访问控制规则（绑定到 VmNetworkAdapter.AclRules）。</summary>
    public class NetworkAclRule
    {
        public string Name { get; set; }
        public string Direction { get; set; } // "Incoming" or "Outgoing"
        public string Action { get; set; }    // "Allow", "Deny", or "Meter"
        public string RemoteAddress { get; set; } // IP or MAC address
        public string LocalAddress { get; set; }
    }
}
