using System.Text.Json.Serialization;
using ExHyperV.App.UI.Shared.Appearance;

namespace ExHyperV.App;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AppearanceSettings))]
internal sealed partial class DesktopJson : JsonSerializerContext;
