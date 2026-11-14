namespace ExHyperV.Tools
{
    /// <summary>维护固定操作系统类型名称及其标准写法。</summary>
    public static class OsImages
    {
        /// <summary>内置只读的操作系统类型白名单；名称可用于对应的显示资源键。</summary>
        public static readonly IReadOnlyList<string> SupportedTypes = Array.AsReadOnly(new[]
        {
            "Windows","Ubuntu","ArchLinux","Debian","CentOS","Kali","Linux","Android","ChromeOS","FydeOS",
            "MacOS","FreeBSD","OpenWrt","FnOS","iStoreOS","TrueNAS","Unraid","NixOS","Manjaro","LinuxMint","Fedora","Deepin",
            "openEuler","Kylin","openSUSE","RockyLinux","AlmaLinux","RHEL","Alpine"
        });

        /// <summary>按不区分大小写的方式匹配内置操作系统类型。</summary>
        /// <returns>标准类型名称；未知或 null 输入回退为 Windows。</returns>
        /// <param name="osType">待规范化的操作系统类型名称。</param>
        internal static string Canonical(string? osType) =>
            SupportedTypes.FirstOrDefault(t => t.Equals(osType, StringComparison.OrdinalIgnoreCase)) ?? "Windows";
    }
}
