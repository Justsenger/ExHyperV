using System.Reflection;
using System.Text.Json;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

internal static class ApiFieldHelp
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions = Load();

    internal static string? For(PropertyInfo property)
        => Descriptions.GetValueOrDefault($"P:{property.DeclaringType!.FullName}.{property.Name}");

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(ApiFieldHelp).Assembly.GetManifestResourceStream("ExHyperV.ApiFieldHelp.json")
            ?? throw new InvalidOperationException("缺少字段说明资源。");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("字段说明资源为空。");
    }
}
