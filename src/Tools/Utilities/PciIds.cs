using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

namespace ExHyperV.Tools
{
    public class PciIds
    {

        private readonly Uri _pciResourceUri = new Uri("/assets/pci.ids", UriKind.Relative);
        private static readonly Regex VendorRegex = new Regex(@"^([0-9a-f]{4})\s+(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 字段处初始化:没调 EnsureInitializedAsync 就查也只是空表返回 Unknown,不 NRE
        private Dictionary<string, string> _vendorDatabase = new();
        private bool _isInitialized = false;

        public PciIds() { }

        public async Task EnsureInitializedAsync()
        {
            if (_isInitialized) return;

            _vendorDatabase = new Dictionary<string, string>();

            var resourceInfo = Application.GetResourceStream(_pciResourceUri);

            if (resourceInfo == null)
            {
                throw new FileNotFoundException(Properties.Resources.Error_EmbeddedWpfResourceNotFound, _pciResourceUri.ToString());
            }

            using (var stream = resourceInfo.Stream)
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
        /// <summary>
        /// PCI 设备优先使用 SUBSYS 中的子系统厂商，再回退到 VEN；
        /// ACPI 和其他总线没有 PCI 子系统语义，直接使用 Windows 返回的制造商。
        /// </summary>
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
