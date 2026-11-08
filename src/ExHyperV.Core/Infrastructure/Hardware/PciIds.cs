using System.IO;
using System.Text.RegularExpressions;


namespace ExHyperV.Tools
{
    /// <summary>从 Core 内嵌 pci.ids 数据读取 PCI 厂商名称；实例需先完成初始化。</summary>
    internal class PciIds
    {

        private const string ResourceName = "ExHyperV.Assets.pci.ids";
        private static readonly Regex VendorRegex = new Regex(@"^([0-9a-f]{4})\s+(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 字段处初始化:没调 EnsureInitializedAsync 就查也只是空表返回 Unknown,不 NRE
        private Dictionary<string, string> _vendorDatabase = new();
        private bool _isInitialized = false;

        /// <summary>创建尚未加载厂商数据库的解析器；此时查询只能使用回退来源。</summary>
        public PciIds() { }

        /// <summary>按需加载内嵌 PCI 厂商数据库，完成后同一实例复用数据。</summary>
        /// <remarks>资源缺失抛出 FileNotFoundException；调用方应避免在初始化期间并发初始化或查询同一实例。</remarks>
        public async Task EnsureInitializedAsync()
        {
            if (_isInitialized) return;

            _vendorDatabase = new Dictionary<string, string>();

            var resourceInfo = typeof(PciIds).Assembly.GetManifestResourceStream(ResourceName);

            if (resourceInfo == null)
            {
                throw new FileNotFoundException(Properties.Resources.Error_EmbeddedWpfResourceNotFound, ResourceName);
            }

            using (var stream = resourceInfo)
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("\t")) continue;
                    Match match = VendorRegex.Match(line);
                    if (match.Success)
                    {
                        string vendorId = match.Groups[1].Value;
                        string vendorName = match.Groups[2].Value.Trim();
                        int commentIndex = vendorName.IndexOf(" (");
                        if (commentIndex > 0)
                        {
                            vendorName = vendorName.Substring(0, commentIndex);
                        }
                        if (!_vendorDatabase.ContainsKey(vendorId))
                        {
                            _vendorDatabase[vendorId] = vendorName;
                        }
                    }
                }
            }
            _isInitialized = true;
        }
        /// <summary>对 PCI 设备优先匹配子系统厂商，其次匹配 VEN；其他总线使用 Windows 厂商说明。</summary>
        /// <remarks>查询不会自行加载数据库，应先 await EnsureInitializedAsync。</remarks>
        /// <returns>识别的厂商名称，或 Windows 厂商回退；都不可用时为 Unknown。</returns>
        /// <param name="instanceId">完整 PnP 设备实例 ID。</param>
        /// <param name="windowsManufacturer">Windows 已提供的制造商说明，可为 null。</param>
        public string GetVendorFromInstanceId(string instanceId, string? windowsManufacturer = null)
        {
            if (!instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(windowsManufacturer) ? "Unknown" : windowsManufacturer.Trim();

            var venMatch = Regex.Match(instanceId, @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase);
            string? vid = venMatch.Success ? venMatch.Groups[1].Value.ToLower() : null;

            var subsysMatch = Regex.Match(
                instanceId,
                @"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})",
                RegexOptions.IgnoreCase);
            if (subsysMatch.Success)
            {
                string svid = subsysMatch.Groups[1].Value.ToLower();
                if (svid != "0000" && svid != "ffff"
                    && _vendorDatabase.TryGetValue(svid, out var subsysVendor))
                    return subsysVendor;
            }

            if (vid != null && _vendorDatabase.TryGetValue(vid, out var vendorName))
                return vendorName;

            return string.IsNullOrWhiteSpace(windowsManufacturer) ? "Unknown" : windowsManufacturer.Trim();
        }
    }
}
