using System.Text.Json;
using System.Text.Json.Serialization;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal enum InspectionStatus { Waiting, Running, Ready, Missing, Pending, Unavailable }

internal sealed record ResourceCheck(string Id, string Group, string Name, string Requirement, string Supports);
internal sealed record ResourceChoice(string Id, string Name, string Detail = "", string? LocationPath = null,
    string? UnavailableReason = null, string? Fingerprint = null);
internal sealed record ResourceResult(InspectionStatus Status, string Detail, string[]? Items = null, ResourceChoice[]? Choices = null);
internal sealed record InspectionItem(ResourceCheck Check, ResourceResult Result);
internal sealed record InspectionReport(string RunId, DateTimeOffset Started, DateTimeOffset? Finished,
    bool Running, string ExecutionContext, string? Error, InspectionItem[] Items)
{
    // Live and saved reports use the same resource rules. This never runs tests.
    public TestPlan Plan => TestPlanner.Analyze(this);
}

// Machine-specific registration stays outside source control. No credentials are accepted here.
internal sealed record InspectionConfiguration
{
    public string? WorkDirectory { get; init; } = DefaultWorkDirectory;
    public string? TemplateVmId { get; init; }
    public string[] GuestTemplatePaths { get; init; } = [];
    public string? GpuTemplateVmId { get; init; }
    public string? TestSwitchId { get; init; }
    public string[] SampleFiles { get; init; } = [];
    public SelectedPcieDevice[] PcieDevices { get; init; } = [];
    public RegisteredDevice? PhysicalDisk { get; init; }
    public RegisteredDevice? OpticalDrive { get; init; }

    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExHyperV.Core.Tests", "resources.json");
    internal static string DefaultWorkDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExHyperV.Core.Tests", "Cache");

    internal InspectionConfiguration WithDefaults() => this with
    {
        WorkDirectory = string.IsNullOrWhiteSpace(WorkDirectory) ? DefaultWorkDirectory : WorkDirectory,
        TemplateVmId = TemplateVmId ?? GpuTemplateVmId,
        GpuTemplateVmId = TemplateVmId ?? GpuTemplateVmId
    };

    public static InspectionConfiguration Read(string? path) => (path is null || !File.Exists(path) ? new() :
        JsonSerializer.Deserialize<InspectionConfiguration>(File.ReadAllText(path), InspectionJson.Options)
        ?? throw new InvalidDataException("资源登记文件为空。")).WithDefaults();
}

internal sealed record SelectedPcieDevice(string InstanceId, string LocationPath);
internal sealed record RegisteredDevice(string Id, string Fingerprint, string Name);

internal static class InspectionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
