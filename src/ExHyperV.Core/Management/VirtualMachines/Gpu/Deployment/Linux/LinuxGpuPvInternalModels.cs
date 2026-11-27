using System.Text.Json.Serialization;

namespace ExHyperV.Services;

internal sealed class LinuxBundleManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("probeSha256")]
    public string ProbeSha256 { get; set; } = string.Empty;

    [JsonPropertyName("libraries")]
    public Dictionary<string, string> Libraries { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("kernels")]
    public List<LinuxBundleKernelEntry> Kernels { get; set; } = new();
}

internal sealed class LinuxBundleKernelEntry
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("vermagic")]
    public string Vermagic { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("srcversion")]
    public string? SrcVersion { get; set; }

    [JsonPropertyName("targets")]
    public List<string>? Targets { get; set; }
}

internal sealed class LinuxIsoDeviceManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("adapters")]
    public List<LinuxIsoAdapterEntry> Adapters { get; set; } = new();
}

internal sealed class LinuxIsoAdapterEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("package")]
    public string Package { get; set; } = string.Empty;
}

internal sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto> Assets { get; set; } = new();
}

internal sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

internal sealed class LinuxBundleException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
