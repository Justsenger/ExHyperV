using ExHyperV.Models;

namespace ExHyperV.Services;

public static partial class VmProcessorService
{
    private sealed record ProcessorProperty(string NativeName, Func<object?, object?> Convert, bool AcceptNull = false);
    private static readonly IReadOnlyDictionary<string, ProcessorProperty> ProcessorProperties = new Dictionary<string, ProcessorProperty>
    {
        [nameof(VmProcessorSettings.Count)] = new("VirtualQuantity", v => checked((ulong)(int)v!)),
        [nameof(VmProcessorSettings.Reservation)] = new("Reservation", v => v),
        [nameof(VmProcessorSettings.Limit)] = new("Limit", v => v),
        [nameof(VmProcessorSettings.RelativeWeight)] = new("Weight", v => checked((uint)(int)v!)),
        [nameof(VmProcessorSettings.ExposeVirtualizationExtensions)] = new("ExposeVirtualizationExtensions", v => v),
        [nameof(VmProcessorSettings.EnableHostResourceProtection)] = new("EnableHostResourceProtection", v => v),
        [nameof(VmProcessorSettings.CompatibilityForMigrationEnabled)] = new("LimitProcessorFeatures", v => v),
        [nameof(VmProcessorSettings.CompatibilityForMigrationMode)] = new("LimitProcessorFeaturesMode", v => Convert.ToByte(v)),
        [nameof(VmProcessorSettings.CompatibilityForOlderOperatingSystemsEnabled)] = new("LimitCPUID", v => v),
        [nameof(VmProcessorSettings.SmtMode)] = new("HwThreadsPerCore", v => (ulong)ConvertSmtModeToHwThreads((SmtMode)v!)),
        [nameof(VmProcessorSettings.DisableSpeculationControls)] = new("DisableSpeculationControls", v => v),
        [nameof(VmProcessorSettings.EnablePerfmonArchPmu)] = new("EnablePerfmonArchPmu", v => v),
        [nameof(VmProcessorSettings.AllowAcountMcount)] = new("AllowAcountMcount", v => v),
        [nameof(VmProcessorSettings.CpuBrandString)] = new("CpuBrandString", v => string.IsNullOrWhiteSpace((string?)v) ? string.Empty : v, true),
        [nameof(VmProcessorSettings.ApicMode)] = new("ApicMode", v => Convert.ToByte(v)),
        [nameof(VmProcessorSettings.L3CacheWays)] = new("L3CacheWays", v => v),
        [nameof(VmProcessorSettings.L3DistributionPolicy)] = new("L3ProcessorDistributionPolicy", v => Convert.ToByte(v)),
        [nameof(VmProcessorSettings.PageShatterMode)] = new("EnablePageShattering", v => Convert.ToByte(v)),
        [nameof(VmProcessorSettings.PerfCpuFreqCapMhz)] = new("PerfCpuFreqCapMhz", v => v),
        [nameof(VmProcessorSettings.PerfCpuFreqMinMhz)] = new("PerfCpuFreqMinMhz", v => v),
        [nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz)] = new("PerfCpuFreqDesiredMhz", v => v ?? uint.MaxValue, true),
        [nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference)] = new("PerfCpuEnergyPerformancePreference", v => v ?? uint.MaxValue, true),
        [nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow)] = new("PerfCpuAutonomousActivityWindow", v => v ?? uint.MaxValue, true),
        [nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency)] = new("PerfCpuIgnoreHostMaxFrequency", v => v),
        [nameof(VmProcessorSettings.EnablePerfmonPmu)] = new("EnablePerfmonPmu", v => v),
        [nameof(VmProcessorSettings.EnablePerfmonLbr)] = new("EnablePerfmonLbr", v => v),
        [nameof(VmProcessorSettings.EnablePerfmonPebs)] = new("EnablePerfmonPebs", v => v),
        [nameof(VmProcessorSettings.EnablePerfmonIpt)] = new("EnablePerfmonIpt", v => v),
        [nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled)] = new("ExtendedVirtualizationExtensions", v => (bool)v! ? 1u : 0u),
        [nameof(VmProcessorSettings.MaxHwIsolatedGuests)] = new("MaxHwIsolatedGuests", v => v),
        [nameof(VmProcessorSettings.MaxClusterCountPerSocket)] = new("MaxClusterCountPerSocket", v => v ?? uint.MaxValue, true),
        [nameof(VmProcessorSettings.MaxProcessorCountPerL3)] = new("MaxProcessorCountPerL3", v => v ?? uint.MaxValue, true),
        [nameof(VmProcessorSettings.MaxProcessorsPerNumaNode)] = new("MaxProcessorsPerNumaNode", v => v),
        [nameof(VmProcessorSettings.MaxNumaNodesPerSocket)] = new("MaxNumaNodesPerSocket", v => v),
        [nameof(VmProcessorSettings.PhysicalAddressWidth)] = new("PhysicalAddressWidth", v => v),
        [nameof(VmProcessorSettings.LpiMode)] = new("LpiMode", v => Convert.ToByte(v)),
    };

    internal static VmProcessorSettings MergeProcessorSelection(VmProcessorSettings current,
        SettingsSelection<VmProcessorSettings> selection)
    {
        var target = selection.Merge(current);
        foreach (string name in selection.Names)
        {
            if (selection[name] != null || ProcessorProperties[name].AcceptNull) continue;
            var property = typeof(VmProcessorSettings).GetProperty(name)!;
            property.SetValue(target, property.GetValue(current));
        }
        return target;
    }

    internal static Dictionary<string, object?> BuildProcessorWrites(VmProcessorSettings current,
        VmProcessorSettings requested, SettingsSelection<VmProcessorSettings> selection)
    {
        var writes = new Dictionary<string, object?>();
        foreach (string name in selection.Names)
        {
            var property = typeof(VmProcessorSettings).GetProperty(name)!;
            object? value = property.GetValue(requested);
            // 保留读取时被归一化的底层编码；未改变的值不重新编码。
            if (Equals(value, property.GetValue(current))) continue;
            var mapping = ProcessorProperties[name];
            if (value == null && !mapping.AcceptNull) continue;
            writes.Add(mapping.NativeName, mapping.Convert(value));
        }
        return writes;
    }
}
