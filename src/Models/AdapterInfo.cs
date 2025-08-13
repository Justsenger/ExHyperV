namespace ExHyperV.Models
{
    /// <summary>
    /// 表示一个网络适配器（虚拟机的或主机的）的数据模型。
    /// </summary>
    public class AdapterInfo
    {
        public string VMName { get; set; }
        public string MacAddress { get; set; }
        public string Status { get; set; }
        public string IPAddresses { get; set; }

        public AdapterInfo(string vMName, string macAddress, string status, string ipAddresses)
        {
            VMName = vMName;
            MacAddress = macAddress;
            Status = status;
            IPAddresses = ipAddresses;
        }
    }
}