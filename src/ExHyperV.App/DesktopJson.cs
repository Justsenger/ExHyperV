using System.Text.Json.Serialization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.Models;

namespace ExHyperV.App;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AppearanceSettings))]
internal sealed partial class DesktopJson : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(GpuDeploymentRecovery))]
internal sealed partial class RecoveryReportJson : JsonSerializerContext;
