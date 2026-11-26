using System.Net.Http;
using System.Reflection;
using ExHyperV.App.Localization;
using Windows.System;

namespace ExHyperV.App;

internal static class AppUpdates
{
    internal const string Repository = "https://github.com/Justsenger/ExHyperV";
    internal const string ReleasePage = Repository + "/releases/latest";
    internal static readonly string CurrentVersion = typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    // Compare the published stable release with this build, including promotion from alpha to stable.
    internal static async Task<string?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, ReleasePage);
        request.Headers.UserAgent.ParseAdd("ExHyperV/" + CurrentVersion);
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? release = response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Path);
        const string tagPrefix = Repository + "/releases/tag/";
        string? tag = release?.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? release[tagPrefix.Length..].TrimStart('v', 'V') : null;
        var installed = CurrentVersion.Split('-', 2);
        if (!Version.TryParse(tag, out var latest) || !Version.TryParse(installed[0], out var current))
            throw new InvalidDataException(L10n.Get("Settings_UnknownRelease"));
        return latest > current || (latest == current && installed.Length > 1) ? tag : null;
    }

    internal static async Task OpenLinkAsync(string url)
    {
        if (!await Launcher.LaunchUriAsync(new Uri(url))) throw new IOException(L10n.Get("Settings_OpenLinkFailed"));
    }
}
