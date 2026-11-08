using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record HostDiskSnapshot(int Number, string UniqueId, string SerialNumber, long SizeBytes,
    bool IsOffline, bool IsReadOnly, bool IsSystem, bool IsBoot)
{
    internal bool SameDevice(HostDiskSnapshot other) => Number == other.Number && SizeBytes > 0 &&
        SizeBytes == other.SizeBytes &&
        (!string.IsNullOrEmpty(UniqueId) || !string.IsNullOrEmpty(SerialNumber)) &&
        (string.IsNullOrEmpty(UniqueId) || string.Equals(UniqueId, other.UniqueId, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(SerialNumber) || string.Equals(SerialNumber, other.SerialNumber, StringComparison.OrdinalIgnoreCase));
}
