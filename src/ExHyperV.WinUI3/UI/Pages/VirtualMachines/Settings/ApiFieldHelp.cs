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
        using var resource = typeof(ApiFieldHelp).Assembly.GetManifestResourceStream("ExHyperV.ApiFieldHelp.json.br")
            ?? throw new InvalidOperationException("缺少字段说明资源。");
        using var stream = new System.IO.Compression.BrotliStream(resource, System.IO.Compression.CompressionMode.Decompress);
        return JsonSerializer.Deserialize(stream, DesktopJson.Default.DictionaryStringString)
            ?? throw new InvalidOperationException("字段说明资源为空。");
    }
}
