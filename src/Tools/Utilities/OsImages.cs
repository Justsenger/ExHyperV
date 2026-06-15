namespace ExHyperV.Tools
{
    /// <summary>OS 类型相关辅助。</summary>
    public static class OsImages
    {
        /// <summary>支持的 OS 类型字符串列表。VM 创建/识别用此白名单，也是矢量资源键名（Vector.{类型}）。</summary>
        public static readonly List<string> SupportedTypes = new()
        {
            "Windows","Ubuntu","ArchLinux","Debian","CentOS","Kali","Linux","Android","ChromeOS","FydeOS",
            "MacOS","FreeBSD","OpenWrt","FnOS","iStoreOS","TrueNAS","Unraid","NixOS","Manjaro","LinuxMint","Fedora","Deepin",
            "openEuler","Kylin","openSUSE","RockyLinux","AlmaLinux","RHEL","Alpine"
        };

        /// <summary>把任意大小写/未知的 OS 类型规范化为 SupportedTypes 中的标准写法，未知回退 "Windows"。</summary>
        public static string Canonical(string? osType) =>
            SupportedTypes.FirstOrDefault(t => t.Equals(osType, StringComparison.OrdinalIgnoreCase)) ?? "Windows";
    }
}
