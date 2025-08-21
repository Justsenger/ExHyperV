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
            throw new FileNotFoundException("无法找到嵌入的 WPF 资源。", _pciResourceUri.ToString());
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
    public string GetVendorFromInstanceId(string instanceId)
    {
        if (!_isInitialized || string.IsNullOrEmpty(instanceId) || _vendorDatabase.Count == 0) return "Unknown";
        var match = Regex.Match(instanceId, @"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string svid = match.Groups[1].Value.ToLower();
            if (_vendorDatabase.TryGetValue(svid, out var vendorName))
            {
                return vendorName;
            }
        }
        return "Unknown";
    }
}