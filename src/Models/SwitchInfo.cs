﻿namespace ExHyperV.Models
{
    /// <summary>
    /// 表示一个 Hyper-V 虚拟交换机的数据模型。
    /// </summary>
    public class SwitchInfo
    {
        public string SwitchName { get; set; }
        public string SwitchType { get; set; }
        public string AllowManagementOS { get; set; }
        public string Id { get; set; }
        public string NetAdapterInterfaceDescription { get; set; }

        // 提供一个构造函数，方便创建实例
        public SwitchInfo(string switchName, string switchType, string host, string id, string phydesc)
        {
            SwitchName = switchName;
            SwitchType = switchType;
            AllowManagementOS = host;
            Id = id;
            NetAdapterInterfaceDescription = phydesc;
        }
    }
}