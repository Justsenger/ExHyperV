namespace ExHyperV.Models
{
    public class VMInfo
    {
        public string Name { get; set; } //虚拟机名称
        public string LowMMIO { get; set; } //低位内存空间大小
        public string HighMMIO { get; set; } //高位内存空间大小
        public string GuestControlled { get; set; } //控制缓存
        public Dictionary<string, string> GPUs { get; set; } //存储显卡适配器列表

        // 构造函数
        public VMInfo(string name, string low, string high, string guest, Dictionary<string, string> gpus)
        {
            Name = name;
            LowMMIO = low;
            HighMMIO = high;
            GuestControlled = guest;
            GPUs = gpus;
        }
    }
}