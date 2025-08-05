namespace ExHyperV.Models
{
    public class GPUInfo
    {
        public string Name { get; set; } //显卡名称
        public string Valid { get; set; } //是否联机
        public string Manu { get; set; } //厂商
        public string InstanceId { get; set; } //显卡实例id
        public string Pname { get; set; } //可分区的显卡路径
        public string Ram { get; set; } //显存大小
        public string DriverVersion { get; set; } //驱动版本
        public string Vendor { get; set; } //制造商

        // 构造函数
        public GPUInfo(string name, string valid, string manu, string instanceId, string pname, string ram, string driverversion, string vendor)
        {
            Name = name;
            Valid = valid;
            Manu = manu;
            InstanceId = instanceId;
            Pname = pname;
            Ram = ram;
            DriverVersion = driverversion;
            Vendor = vendor;
        }
    }
}