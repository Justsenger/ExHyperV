namespace ExHyperV.Tools
{
    /// <summary>
    /// OS 类型 → 资源文件名（PNG）。支持的类型列表 + 一个查找方法。
    /// </summary>
    public static class OsImages
    {
        /// <summary>支持的 OS 类型字符串列表。VM 创建/识别用此白名单。</summary>
        public static readonly List<string> SupportedTypes = new()
        {
            "Windows","Ubuntu","ArchLinux","Debian","CentOS","Kali","Linux","Android","ChromeOS","FydeOS",
            "MacOS","FreeBSD","OpenWrt","FnOS","iStoreOS","TrueNAS","Unraid","NixOS","Manjaro","LinuxMint","Fedora","Deepin"
        };

        /// <summary>查 OS 类型对应的资源文件名（不含路径），无匹配回退 Windows.png。</summary>
        public static string GetFileName(string osType)
        {
            if (string.IsNullOrWhiteSpace(osType)) return "Windows.png";
            string lower = osType.ToLower();
            return SupportedTypes.Any(t => t.Equals(lower, StringComparison.OrdinalIgnoreCase))
                ? $"{lower}.png"
                : "Windows.png";
        }
    }
}
