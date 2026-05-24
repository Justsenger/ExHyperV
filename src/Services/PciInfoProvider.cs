using System.IO;
using System.Text.RegularExpressions;
using System.Windows;

public class PciInfoProvider
{

    private readonly Uri _pciResourceUri = new Uri("/assets/pci.ids", UriKind.Relative);
    private static readonly Regex VendorRegex = new Regex(@"^([0-9a-f]{4})\s+(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private Dictionary<string, string> _vendorDatabase;
    private bool _isInitialized = false;

    public PciInfoProvider() { }

    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;

        _vendorDatabase = new Dictionary<string, string>();

        var resourceInfo = Application.GetResourceStream(_pciResourceUri);

        if (resourceInfo == null)
        {
            throw new FileNotFoundException(ExHyperV.Properties.Resources.Error_EmbeddedWpfResourceNotFound, _pciResourceUri.ToString());
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
    public string GetVendorFromInstanceId(string instanceId, string? deviceClass = null)
    {
        var venMatch = Regex.Match(instanceId, @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        string? vid = venMatch.Success ? venMatch.Groups[1].Value.ToLower() : null;

        // Intel 设备直接用 VEN，不看 SUBSYS
        bool trySubsys = vid != "8086"
            && string.Equals(deviceClass, "Display", StringComparison.OrdinalIgnoreCase);

        if (trySubsys)
        {
            var subsysMatch = Regex.Match(instanceId, @"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})", RegexOptions.IgnoreCase);
            if (subsysMatch.Success)
            {
                string svid = subsysMatch.Groups[1].Value.ToLower();
                if (_vendorDatabase.TryGetValue(svid, out var subsysVendor))
                    return subsysVendor;
            }
        }

        if (vid != null && _vendorDatabase.TryGetValue(vid, out var vendorName))
            return vendorName;

        return "Unknown";
    }
}