using System.Management;

namespace ExHyperV;

internal static class CimJsonValue
{
    // Preserve the typed representation of primitive arrays (including base64 for byte[]).
    // Embedded objects own a native reference returned from the property read.
    internal static object? FromProperty(object? value)
    {
        if (value is ManagementBaseObject item)
        {
            using (item) return ReadFields(item);
        }
        if (value is ManagementBaseObject[] items)
        {
            try { return items.Select(child => (object?)(child == null ? null : ReadFields(child))).ToArray(); }
            finally { foreach (var child in items) child?.Dispose(); }
        }
        return value;
    }

    private static Dictionary<string, object?> ReadFields(ManagementBaseObject item)
        => item.Properties.Cast<PropertyData>().OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => FromProperty(p.Value), StringComparer.Ordinal);
}
