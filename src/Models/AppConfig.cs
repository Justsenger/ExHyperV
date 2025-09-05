using System.Collections.Generic;
using System.Xml.Serialization;

namespace ExHyperV.Models
{
    /// <summary>
    /// 代表了 config.xml 文件中一个 <Switch> 节点的配置数据。
    /// </summary>
    public class SwitchConfig
    {
        [XmlAttribute("Id")]
        public string Id { get; set; } = string.Empty;

        [XmlElement("Subnet")]
        public string Subnet { get; set; } = string.Empty;

        [XmlElement("NatEnabled")]
        public bool NatEnabled { get; set; } = false;

        [XmlElement("DhcpEnabled")]
        public bool DhcpEnabled { get; set; } = false;
    }

    /// <summary>
    /// 代表整个 config.xml 文件的根节点 (<Configuration>)。
    /// 包含了应用程序的所有持久化设置。
    /// </summary>
    [XmlRoot("Configuration")]
    public class AppConfig
    {
        // === 新增：语言配置 ===
        /// <summary>
        /// 应用程序的显示语言 ("en-US" or "zh-CN")。
        /// </summary>
        [XmlElement("Language")]
        public string Language { get; set; } = string.Empty;


        /// <summary>
        /// 存储所有虚拟交换机配置的列表。
        /// </summary>
        [XmlArray("Switches")]
        [XmlArrayItem("Switch")]
        public List<SwitchConfig> Switches { get; set; } = new List<SwitchConfig>();
    }
}