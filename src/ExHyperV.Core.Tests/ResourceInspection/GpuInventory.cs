using System.Text.RegularExpressions;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class GpuInventory
{
    // The input comes from Core's Win32Api.GetPresentDisplayAdapters: present PCI / ACPI
    // display devices, excluding remote display adapters and disconnected devices.
    internal static ResourceResult Describe(
        IEnumerable<(string InstanceId, string Name, string Manufacturer, string DriverVersion)> adapters)
    {
        var devices = adapters
            .Where(adapter => !string.IsNullOrWhiteSpace(adapter.Name))
            .Select(adapter => (Adapter: adapter, Vendor: IdentifyVendor(adapter.InstanceId, adapter.Manufacturer)))
            .Where(device => device.Vendor is not null)
            .ToArray();
        if (devices.Length == 0)
            return new(InspectionStatus.Missing, "未找到 N / I / A / Q 显卡");

        var items = devices.Select(device =>
            $"{device.Vendor} · {device.Adapter.Name} · 驱动 {Known(device.Adapter.DriverVersion)} · 设备 ID {device.Adapter.InstanceId}").ToArray();
        var missingDriver = devices.Any(device => string.IsNullOrWhiteSpace(device.Adapter.DriverVersion));
        return new(missingDriver ? InspectionStatus.Pending : InspectionStatus.Ready,
            $"{devices.Length} 张" + (missingDriver ? "，部分驱动未知" : ""), items);
    }

    private static string Known(string value) => string.IsNullOrWhiteSpace(value) ? "未取得" : value;

    private static string? IdentifyVendor(string instanceId, string manufacturer)
    {
        // Match the chipset identification used by Core's GpuDriverPackageResolver.ResolveVendor.
        // A subsystem board vendor (ASUS / MSI etc.) is not the GPU chipset vendor.
        var identity = string.Join('|', instanceId, manufacturer);
        if (identity.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) || identity.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (identity.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) || identity.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (identity.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || identity.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(identity, @"(^|\|)AMD($|\|)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "AMD";
        if (identity.Contains("VEN_17CB", StringComparison.OrdinalIgnoreCase) || identity.Contains("QCOM", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase)) return "Qualcomm";
        return null;
    }
}
