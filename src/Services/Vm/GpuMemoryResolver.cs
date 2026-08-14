using System.Buffers.Binary;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>
/// Reads memory for the exact present display-adapter instance. This avoids mixing
/// another adapter or a stale display-class entry into the value shown by the UI.
/// </summary>
internal static class GpuMemoryResolver
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static ulong ReadBytes(string instanceId, string manufacturer, string vendor)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return 0;

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? deviceKey = baseKey.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            string driverKey = deviceKey?.GetValue("Driver")?.ToString() ?? string.Empty;
            string prefix = $@"{DisplayClassGuid}\";
            if (!driverKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;

            string classSubKeyName = driverKey[prefix.Length..];
            if (string.IsNullOrWhiteSpace(classSubKeyName) || classSubKeyName.Contains('\\')) return 0;

            using RegistryKey? adapterKey = baseKey.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}\{classSubKeyName}");
            if (adapterKey == null) return 0;

            ulong bytes = NormalizeRegistryValue(adapterKey.GetValue("HardwareInformation.qwMemorySize"));
            if (bytes != 0) return bytes;

            // Moore Threads historically exposes HardwareInformation.MemorySize in MiB.
            // Other vendors expose bytes. The QWORD value above always has priority.
            bool isMooreThreads = instanceId.Contains("VEN_1ED5", StringComparison.OrdinalIgnoreCase) ||
                                  manufacturer.Contains("Moore", StringComparison.OrdinalIgnoreCase) ||
                                  vendor.Contains("Moore", StringComparison.OrdinalIgnoreCase) ||
                                  manufacturer.Contains("摩尔线程", StringComparison.OrdinalIgnoreCase) ||
                                  vendor.Contains("摩尔线程", StringComparison.OrdinalIgnoreCase);
            return NormalizeLegacyRegistryValue(
                adapterKey.GetValue("HardwareInformation.MemorySize"),
                isMooreThreads);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static ulong NormalizeRegistryValue(object? value)
    {
        return value switch
        {
            ulong number => number,
            long number when number > 0 => (ulong)number,
            long => 0,
            uint number => number,
            int number => unchecked((uint)number),
            ushort number => number,
            short number => unchecked((ushort)number),
            byte number => number,
            sbyte number => unchecked((byte)number),
            byte[] bytes when bytes.Length >= sizeof(ulong) => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            byte[] bytes when bytes.Length >= sizeof(uint) => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            string text when ulong.TryParse(text, out ulong number) => number,
            _ => 0
        };
    }

    internal static ulong NormalizeLegacyRegistryValue(object? value, bool isMooreThreads)
    {
        ulong normalized = NormalizeRegistryValue(value);
        if (!isMooreThreads || normalized == 0) return normalized;
        const ulong bytesPerMiB = 1024UL * 1024UL;
        return normalized <= ulong.MaxValue / bytesPerMiB ? normalized * bytesPerMiB : 0;
    }
}
