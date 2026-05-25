using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using ExHyperV.Api;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ExHyperV.Tools;

public class Utils
{
    public static string GetIconPath(string deviceType, string friendlyName)
    {
        switch (deviceType)
        {
            case "Switch":
                return "\xF597";
            case "Upstream":
                return "\uE774";
            case "Display":
                return "\xF211";
            case "Net":
                return "\xE839";
            case "USB":
                return friendlyName.Contains("USB4")
                    ? "\xE945"
                    : "\xECF0";
            case "HIDClass":
                return "\xE928";
            case "SCSIAdapter":
            case "HDC":
                return "\xEDA2";
            default:
                return friendlyName.Contains("Audio")
                    ? "\xE995"
                    : "\xE950";
        }
    }

    public static FontIcon FontIcon1(string classType, string friendlyName)
    {
        return new FontIcon
        {
            FontSize = 24,
            FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
            Glyph = GetIconPath(classType, friendlyName)
        };
    }

    public static string GetGpuImagePath(string Manu, string name)
    {
        string imageName;
        if (Manu.Contains("NVIDIA"))
            imageName = "NVIDIA.png";
        else if (Manu.Contains("Advanced"))
            imageName = "AMD.png";
        else if (Manu.Contains("Microsoft"))
            imageName = "Microsoft.png";
        else if (Manu.Contains("Intel"))
        {
            imageName = "Intel.png";
            if (name.ToLower().Contains("iris")) imageName = "Intel-IrisXe.png";
            if (name.ToLower().Contains("arc")) imageName = "Intel-ARC.png";
            if (name.ToLower().Contains("data")) imageName = "Intel-DataCenter.png";
        }
        else if (Manu.Contains("Moore"))
            imageName = "Moore.png";
        else if (Manu.Contains("Qualcomm"))
            imageName = "Qualcomm.png";
        else if (Manu.Contains("DisplayLink"))
            imageName = "DisplayLink.png";
        else if (Manu.Contains("Silicon"))
            imageName = "Silicon.png";
        else
            imageName = "Default.png";

        return $"pack://application:,,,/Assets/{imageName}";
    }


    public static DateTime GetLinkerTime()
    {
        string filePath = Assembly.GetExecutingAssembly().Location;
        var fileInfo = new System.IO.FileInfo(filePath);
        return fileInfo.LastWriteTime;
    }


    /// <summary>
    /// 添加Hyper-V GPU分配策略注册表项，以允许不受支持的GPU进行分区。
    /// </summary>
    public static void AddGpuAssignmentStrategyReg()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\HyperV");
            key.SetValue("RequireSecureDeviceAssignment", 0, RegistryValueKind.DWord);
            key.SetValue("RequireSupportedDeviceAssignment", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddGpuAssignmentStrategyReg failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 移除Hyper-V GPU分配策略注册表项。
    /// </summary>
    public static void RemoveGpuAssignmentStrategyReg()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\HyperV", writable: true);
            if (key == null) return;
            key.DeleteValue("RequireSecureDeviceAssignment", throwOnMissingValue: false);
            key.DeleteValue("RequireSupportedDeviceAssignment", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RemoveGpuAssignmentStrategyReg failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用 GPU-P 修复补丁。
    /// 该方法通过禁用 Hyper-V 的 GPU 分区严格模式来解决 Windows 更新后的问题。
    /// </summary>
    public static void ApplyGpuPartitionStrictModeFix()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\WindowsNT\CurrentVersion\Virtualization");
            key.SetValue("DisableGpuPartitionStrictMode", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyGpuPartitionStrictModeFix failed: {ex.Message}");
        }
    }

    #region Hyper-V Network Helpers

    public static string SelectBestIpv4Address(string ipCandidates)
    {
        if (string.IsNullOrWhiteSpace(ipCandidates)) return string.Empty;

        var parsedAddresses = ipCandidates
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeIpCandidate)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => IPAddress.TryParse(candidate, out var addr) && addr.AddressFamily == AddressFamily.InterNetwork ? addr : null)
            .Where(addr => addr != null)
            .Cast<IPAddress>()
            .Distinct()
            .ToList();

        if (parsedAddresses.Count == 0) return string.Empty;

        var preferred = parsedAddresses.FirstOrDefault(IsRfc1918PrivateAddress)
            ?? parsedAddresses.FirstOrDefault(addr => !IsLinkLocalOrLoopback(addr))
            ?? parsedAddresses[0];

        return preferred.ToString();
    }

    private static string NormalizeIpCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;
        string trimmed = candidate.Trim().Trim('[', ']');
        int cidrIndex = trimmed.IndexOf('/');
        if (cidrIndex > 0) trimmed = trimmed.Substring(0, cidrIndex);
        return trimmed.Trim();
    }

    private static bool IsLinkLocalOrLoopback(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254));
    }

    private static bool IsRfc1918PrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        return bytes[0] == 192 && bytes[1] == 168;
    }
    public static string FormatMac(string? rawMac)
    {
        if (string.IsNullOrEmpty(rawMac)) return "00:15:5D:00:00:00";
        string clean = Regex.Replace(rawMac.ToUpperInvariant(), "[^0-9A-F]", "");
        if (clean.Length != 12) return rawMac;
        return Regex.Replace(clean, ".{2}", "$0:").TrimEnd(':');
    }

    public static async Task<string> GetVmIpAddressAsync(string vmName, string macAddressWithColons)
    {
        if (string.IsNullOrEmpty(vmName) || string.IsNullOrEmpty(macAddressWithColons))
            return string.Empty;

        // 路径1：WMI Msvm_GuestNetworkAdapterConfiguration
        var vmGuidResp = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj["Name"]?.ToString() ?? string.Empty,
            WmiScope.HyperV);

        if (vmGuidResp.HasData && !string.IsNullOrEmpty(vmGuidResp.Data))
        {
            string vmGuid = vmGuidResp.Data;
            var ipResp = await WmiApi.QueryAsync(
                "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration",
                obj => new
                {
                    InstanceID = obj["InstanceID"]?.ToString() ?? string.Empty,
                    IPs = obj["IPAddresses"] as string[] ?? Array.Empty<string>()
                },
                WmiScope.HyperV);

            if (ipResp.Success && ipResp.Data != null)
            {
                var ips = ipResp.Data
                    .Where(x => x.InstanceID.Contains(vmGuid, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(x => x.IPs)
                    .Where(a => IPAddress.TryParse(a, out var parsed) &&
                                parsed.AddressFamily == AddressFamily.InterNetwork)
                    .ToList();

                if (ips.Count > 0)
                    return string.Join(", ", ips);
            }
        }

        // 路径2：ARP 缓存回退
        return await GetIpFromArpCacheAsync(macAddressWithColons);
    }
    public static async Task<string> GetIpFromArpCacheAsync(string macWithColons)
    {
        if (string.IsNullOrEmpty(macWithColons)) return string.Empty;

        string clean = macWithColons.Replace(":", "").Replace("-", "").ToUpperInvariant();
        string formatted = Regex.Replace(clean, ".{2}", "$0-").TrimEnd('-');

        var resp = await WmiApi.QueryCimAsync(
            $"SELECT IPAddress FROM MSFT_NetNeighbor WHERE LinkLayerAddress = '{formatted}' AND AddressFamily = 2 AND State <> 0",
            obj => obj["IPAddress"]?.ToString() ?? string.Empty,
            WmiScope.StdCimV2);

        if (resp.Success && resp.Data != null)
            return resp.Data.FirstOrDefault(ip => !string.IsNullOrEmpty(ip)) ?? string.Empty;

        return string.Empty;
    }

    #endregion

    public static string GetFriendlyErrorMessage(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage)) return "Storage_Error_Unknown";
        var match = Regex.Match(rawMessage, @"Storage_(Error|Msg)_[A-Za-z0-9_]+");
        if (match.Success) return match.Value;
        string cleanMsg = Regex.Replace(rawMessage.Trim(), @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "").Replace("\r", "").Replace("\n", " ");
        var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return (parts.Count >= 2 && parts.Last().Length > 2) ? parts.Last() + "。" : cleanMsg;
    }

    public static string GetFriendlyErrorMessages(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage)) return string.Empty;

        string message = rawMessage;
        var guidInParensRegex = new Regex(@"\s*[\(（].*?[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}.*?[\)）]");
        string[] lines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var distinctLines = lines
            .Select(line => guidInParensRegex.Replace(line, ""))
            .Select(line => line.Trim().Trim('"', '\u201c', '\u201d').TrimEnd('.', '。'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal);

        string finalMessage = string.Join(Environment.NewLine, distinctLines);
        return string.IsNullOrWhiteSpace(finalMessage) ? rawMessage.Trim() : finalMessage;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "Invalid size";
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
        int unitIndex = (int)Math.Floor(Math.Log(bytes, 1024));
        double number = bytes / Math.Pow(1024, unitIndex);
        string format = (unitIndex == 0) ? "F0" : "F2";
        return $"{number.ToString(format)} {units[unitIndex]}";
    }

    public static readonly List<string> SupportedOsTypes = new()
    {
        "Windows","Ubuntu","ArchLinux","Debian","CentOS","Kali", "Linux", "Android", "ChromeOS", "FydeOS",
        "MacOS", "FreeBSD", "OpenWrt", "FnOS","iStoreOS","TrueNAS","Unraid","NixOS","Manjaro","LinuxMint","Fedora","Deepin"
    };

    public static string GetOsImageName(string osType)
    {
        if (string.IsNullOrWhiteSpace(osType)) return "Windows.png";
        string lower = osType.ToLower();
        return SupportedOsTypes.Any(t => t.Equals(lower, StringComparison.OrdinalIgnoreCase))
            ? $"{lower}.png"
            : "Windows.png";
    }

    public static string GetTagValue(string text, string tagName)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string prefix = $"[{tagName}:";
        int start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start == -1) return string.Empty;
        start += prefix.Length;
        int end = text.IndexOf("]", start);
        return end == -1 ? string.Empty : text.Substring(start, end - start);
    }

    public static string UpdateTagValue(string text, string tagName, string newValue)
    {
        text = text ?? string.Empty;
        string tagPrefix = $"[{tagName}:";
        string newTag = $"[{tagName}:{newValue}]";
        int startIndex = text.IndexOf(tagPrefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex != -1)
        {
            int endIndex = text.IndexOf("]", startIndex);
            if (endIndex != -1)
                return text.Remove(startIndex, endIndex - startIndex + 1).Insert(startIndex, newTag);
        }
        return string.IsNullOrWhiteSpace(text) ? newTag : $"{text.Trim()} {newTag}";
    }

    public static string Version =>
        $"V{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0"}";

    public static string Author => "Justsenger";

}