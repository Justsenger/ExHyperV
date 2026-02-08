namespace ExHyperV.Models
{
    public class GPUInfo
    {
        public string Name { get; set; } //显卡名称
        public string Valid { get; set; } //是否联机
        public string Manu { get; set; } //厂商 (用于图标查找)
        public string InstanceId { get; set; } //显卡实例id
        public string Pname { get; set; } //可分区的显卡路径
        public string Ram { get; set; } //显存大小 (原始字符串, 如 "4294967296")
        public string DriverVersion { get; set; } //驱动版本
        public string Vendor { get; set; } //制造商 (如 NVIDIA, AMD)

        // ✅ 新增：格式化后的显存显示 (用于 UI)
        public string RamDisplay
        {
            get
            {
                if (long.TryParse(Ram, out long bytes))
                {
                    if (bytes == 0) return "未知";
                    double gb = bytes / (1024.0 * 1024.0 * 1024.0);
                    if (gb >= 1.0) return $"{gb:0.##} GB";
                    return $"{bytes / (1024.0 * 1024.0):0.##} MB";
                }
                return "未知";
            }
        }

        public string PathDisplay
        {
            get
            {
                // 优先使用 Pname (分区路径)，如果没有则用 InstanceId
                string rawPath = !string.IsNullOrEmpty(Pname) ? Pname : InstanceId;

                if (string.IsNullOrWhiteSpace(rawPath)) return "未知路径";

                try
                {
                    string cleanId = rawPath;

                    // 1. 去掉前面的符号链接前缀 \\?\
                    if (cleanId.StartsWith(@"\\?\"))
                    {
                        cleanId = cleanId.Substring(4);
                    }

                    // 2. 截取到 GUID (#{...) 之前的部分
                    int guidIndex = cleanId.IndexOf("#{");
                    if (guidIndex != -1)
                    {
                        cleanId = cleanId.Substring(0, guidIndex);
                    }

                    // 3. 将所有 # 替换为 \，还原成标准的 PnP ID 格式
                    return cleanId.Replace('#', '\\');
                }
                catch
                {
                    return rawPath; // 万一解析出错，返回原始路径保底
                }
            }
        }
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