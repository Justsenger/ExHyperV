using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public class PciInfoProvider
{
    private const string DefaultPciFilePath = "Assets/pci.ids";
    private readonly string _pciFilePath;
    private class PciVendor
    {
        public string Name { get; set; }
    }
    private static readonly Regex VendorRegex = new Regex(@"^([0-9a-f]{4})\s+(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private Dictionary<string, PciVendor> _vendorDatabase;
    private bool _isInitialized = false;

    public PciInfoProvider()
    {
        _pciFilePath = DefaultPciFilePath;
    }
    public PciInfoProvider(string customPciFilePath)
    {
        _pciFilePath = customPciFilePath;
    }
    public async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }
        string absolutePath = Path.GetFullPath(_pciFilePath);

        if (!File.Exists(absolutePath))
        {
            _vendorDatabase = new Dictionary<string, PciVendor>();
            _isInitialized = true;
            return;
        }

        _vendorDatabase = new Dictionary<string, PciVendor>();

        using (var reader = new StreamReader(absolutePath))
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("\t"))
                {
                    continue;
                }

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
                        _vendorDatabase[vendorId] = new PciVendor { Name = vendorName };
                    }
                }
            }
        }

        _isInitialized = true; 
    }
    public string GetVendorFromInstanceId(string instanceId)
    {
        if (!_isInitialized || string.IsNullOrEmpty(instanceId) || _vendorDatabase.Count == 0)
        {
            return "Unknown";
        }

        var match = Regex.Match(instanceId, @"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})", RegexOptions.IgnoreCase);

        if (match.Success)
        {
            string svid = match.Groups[1].Value.ToLower();
            if (_vendorDatabase.TryGetValue(svid, out var vendor))
            {
                return vendor.Name;
            }
            return $"Unknown";
        }

        return "Unknown";
    }
}