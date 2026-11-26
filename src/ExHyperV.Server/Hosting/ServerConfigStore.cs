using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExHyperV.Server.Hosting;

internal sealed class ServerConfigStore(string directory) : StateFiles(directory)
{
    internal sealed record Config(Uri Url, string[] Origins, bool ReadOnly, IReadOnlySet<string> Toolsets);
    private string FilePath => StatePath("server.json");

    private sealed record Settings(string Url = "http://127.0.0.1:5191", string[]? CorsOrigins = null, bool ReadOnly = false, string[]? Toolsets = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, PropertyNameCaseInsensitive = false
    };

    internal Config? Load()
    {
        try
        {
            VerifyFile(FilePath);
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 65536) throw new InvalidDataException("server.json 超过 64 KiB。");
            var settings = JsonSerializer.Deserialize<Settings>(stream, Json) ?? throw new JsonException();
            return new(LaunchOptions.ParseUrl(settings.Url), LaunchOptions.ParseOrigins(settings.CorsOrigins ?? []),
                settings.ReadOnly, LaunchOptions.ParseGroups(settings.Toolsets ?? LaunchOptions.Groups));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidDataException("server.json 格式或配置值无效，请修复配置文件。", error); }
    }

    internal void Save(LaunchOptions options)
    {
        _ = Load();
        var data = new Settings(options.Url.GetLeftPart(UriPartial.Authority), options.Origins.Order().ToArray(),
            options.ReadOnly, options.Toolsets.Order().ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        if (bytes.Length > 65536) throw new ArgumentException("server.json 超过 64 KiB。");
        Write(FilePath, stream => stream.Write(bytes));
    }
}
