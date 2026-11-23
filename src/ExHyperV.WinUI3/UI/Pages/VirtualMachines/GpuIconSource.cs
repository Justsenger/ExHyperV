using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

internal static class GpuIconSource
{
    private static readonly Dictionary<string, ImageSource> Images = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? manu, string? name)
        => Get(manu, name, out _);

    public static ImageSource? Get(string? manu, string? name, out string? fileName)
    {
        fileName = ResolveFileName(manu, name);
        if (fileName == null) return null;
        if (!Images.TryGetValue(fileName, out ImageSource? image))
        {
            image = new SvgImageSource(new Uri($"ms-appx:///Assets/Gpu/{fileName}"));
            Images[fileName] = image;
        }
        return image;
    }

    public static bool IsTransparentBackdrop(string? fileName)
        => string.Equals(fileName, "nvidia.svg", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveFileName(string? manu, string? name)
    {
        string normalizedManu = (manu ?? string.Empty).ToUpperInvariant();
        string normalizedName = (name ?? string.Empty).ToLowerInvariant();

        if (normalizedManu.Contains("NVIDIA", StringComparison.Ordinal) ||
            normalizedName.Contains("nvidia", StringComparison.Ordinal) ||
            normalizedName.Contains("geforce", StringComparison.Ordinal) ||
            normalizedName.Contains("rtx", StringComparison.Ordinal))
        {
            return "nvidia.svg";
        }

        if (normalizedManu.Contains("AMD", StringComparison.Ordinal) ||
            normalizedManu.Contains("ADVANCED", StringComparison.Ordinal) ||
            normalizedName.Contains("radeon", StringComparison.Ordinal) ||
            normalizedName.Contains("amd", StringComparison.Ordinal))
        {
            return "amd.svg";
        }

        if (normalizedManu.Contains("QUALCOMM", StringComparison.Ordinal) ||
            normalizedName.Contains("qualcomm", StringComparison.Ordinal) ||
            normalizedName.Contains("adreno", StringComparison.Ordinal))
        {
            return "qualcomm.svg";
        }

        if (normalizedManu.Contains("LISUAN", StringComparison.Ordinal) ||
            normalizedName.Contains("lisuan", StringComparison.Ordinal))
        {
            return "lisuan.svg";
        }

        if (normalizedManu.Contains("INTEL", StringComparison.Ordinal) ||
            normalizedName.Contains("intel", StringComparison.Ordinal))
        {
            if (normalizedName.Contains("iris", StringComparison.Ordinal)) return "intel-irisxe.svg";
            if (normalizedName.Contains("arc", StringComparison.Ordinal)) return "intel-arc.svg";
            if (normalizedName.Contains("data", StringComparison.Ordinal)) return "intel-datacenter.svg";
            return "intel.svg";
        }

        return null;
    }
}
