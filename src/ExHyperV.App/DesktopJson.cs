using System.Text.Json.Serialization;
using ExHyperV.App.UI.Shared.Appearance;

namespace ExHyperV.App;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(JsonStringEnumConverter<StartupMode>), typeof(JsonStringEnumConverter<WindowCloseBehavior>),
        typeof(JsonStringEnumConverter<ConsoleSessionMode>)])]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AppearanceSettings))]
internal sealed partial class DesktopJson : JsonSerializerContext;
