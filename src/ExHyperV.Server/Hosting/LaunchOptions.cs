using System.Net;

namespace ExHyperV.Server.Hosting;

internal enum Command { Run, Help, Version, Fingerprint, Verify, Catalog, SetPassword, ClearPassword, SaveConfig, Install, Uninstall }

internal sealed class LaunchOptions
{
    internal static readonly string[] Groups = ["core", "host", "vm", "devices", "console", "utilities"];
    public Command Command { get; private set; }
    public Uri Url { get; private set; } = new("http://127.0.0.1:5191");
    public bool LoopbackOnly => Url.IsLoopback;
    public bool ReadOnly { get; private set; }
    public bool Service { get; private set; }
    public string ServiceName { get; private set; } = "ExHyperV.Server";
    public string? Password { get; private set; }
    public HashSet<string> Origins { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Toolsets { get; private set; } = Groups.ToHashSet(StringComparer.Ordinal);
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExHyperV.Server");
    public string StateDirectory { get; private set; } = Environment.GetEnvironmentVariable("EXHYPERV_SERVER_STATE_DIRECTORY") ?? DefaultDirectory;

    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();
        var seen = new HashSet<string>();
        for (int index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!seen.Add(name)) throw new ArgumentException("启动参数重复。");
            string Value() => ++index < args.Length && args[index].Length > 0 && (name == "--password" || !args[index].StartsWith("--", StringComparison.Ordinal))
                ? args[index] : throw new ArgumentException($"请为 {name} 提供值。");
            void Mode(Command command)
            {
                if (options.Command != Command.Run && options.Command != command)
                    throw new ArgumentException("请一次选择一个启动命令。");
                options.Command = command;
            }
            switch (name)
            {
                case "-h": case "--help": Mode(Command.Help); break;
                case "--version": Mode(Command.Version); break;
                case "--contract-fingerprint": Mode(Command.Fingerprint); break;
                case "--verify-contracts": Mode(Command.Verify); break;
                case "--api-catalog": Mode(Command.Catalog); break;
                case "--set-password":
                    if (options.Command != Command.ClearPassword) Mode(Command.SetPassword);
                    break;
                case "--password": Mode(Command.SetPassword); options.Password = Value(); break;
                case "--clear-password":
                    if (options.Command == Command.SetPassword && options.Password == null) options.Command = Command.ClearPassword;
                    else Mode(Command.ClearPassword);
                    break;
                case "--save-config": Mode(Command.SaveConfig); break;
                case "--install-service": Mode(Command.Install); break;
                case "--uninstall-service": Mode(Command.Uninstall); break;
                case "--service-name": options.ServiceName = ServiceCommandBuilder.ValidateName(Value()); break;
                case "--service": options.Service = true; break;
                case "--read-write": options.ReadOnly = false; break;
                case "--read-only": options.ReadOnly = true; break;
                case "--state-dir": options.StateDirectory = Value(); break;
                case "--url":
                    options.Url = ParseUrl(Value());
                    break;
                case "--cors-origins":
                    options.Origins.UnionWith(ParseOrigins(Value().Split(',', StringSplitOptions.TrimEntries)));
                    break;
                case "--toolsets":
                    options.Toolsets = ParseGroups(Value().Split(',', StringSplitOptions.TrimEntries));
                    break;
                default: throw new ArgumentException("未知启动参数，请使用 --help 查看用法。");
            }
        }
        if (string.IsNullOrWhiteSpace(options.StateDirectory)) throw new ArgumentException("请提供有效的状态目录。");
        options.StateDirectory = Path.GetFullPath(options.StateDirectory);
        if (seen.Contains("--read-only") && seen.Contains("--read-write")) throw new ArgumentException("请选择一种访问模式。");
        if (options.Command is Command.Run or Command.SaveConfig or Command.Install && new ServerConfigStore(options.StateDirectory).Load() is { } config)
        {
            if (!seen.Contains("--url")) options.Url = config.Url;
            if (!seen.Contains("--cors-origins")) options.Origins.UnionWith(config.Origins);
            if (!seen.Contains("--toolsets")) options.Toolsets = config.Toolsets;
            if (!seen.Contains("--read-only") && !seen.Contains("--read-write")) options.ReadOnly = config.ReadOnly;
        }
        return options;
    }

    internal static Uri ParseUrl(string url)
    {
        if (!TryOrigin(url.EndsWith('/') ? url[..^1] : url, out var endpoint)
            || !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(endpoint.DnsSafeHost, out _)))
            throw new ArgumentException("--url 使用 HTTP 或 HTTPS 的 IP 地址或 localhost，例如 http://127.0.0.1:5191。");
        return endpoint;
    }

    internal static string[] ParseOrigins(string[] origins) => origins.Select(origin =>
        TryOrigin(origin, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : throw new ArgumentException("需要标准 HTTP 或 HTTPS Origin。")).ToArray();

    internal static IReadOnlySet<string> ParseGroups(string[] groups) =>
        groups.Length > 0 && groups.All(group => Groups.Contains(group)) ? groups.ToHashSet(StringComparer.Ordinal)
            : throw new ArgumentException("工具组可选 " + string.Join(", ", Groups));

    internal static bool TryOrigin(string? text, out Uri uri)
    {
        uri = null!;
        if (text == null || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.Port < 1
            || !parsed.IsWellFormedOriginalString() || parsed.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
            return false;
        // Inspect the raw authority before Uri can normalize slashes, dot segments or empty credentials.
        int start = text.IndexOf("://", StringComparison.Ordinal) + 3;
        if (start < 3 || text.AsSpan(start).ContainsAny("/\\@?#%, ".AsSpan()) || text.EndsWith(':') || text.Any(char.IsWhiteSpace)) return false;
        uri = parsed;
        return true;
    }
}
