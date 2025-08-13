namespace ExHyperV.Models
{
    /// <summary>
    /// 表示一个物理网络适配器的数据模型，主要用于存储其描述信息。
    /// </summary>
    public class PhysicalAdapterInfo
    {
        public string InterfaceDescription { get; private set; }

        public PhysicalAdapterInfo(string desc)
        {
            InterfaceDescription = desc;
        }
    }
}