using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.Mcp.Hosting;

public sealed class McpLaunchOptions
{
    public string Transport { get; private init; } = "stdio";
    public Uri? Url { get; private init; }
    public bool LoopbackOnly { get; private init; }
    public bool ReadOnly { get; private init; }
    public IReadOnlySet<string> Toolsets { get; private init; } = new HashSet<string>(["core", "host", "vm", "devices", "console", "utilities"], StringComparer.Ordinal);
    internal byte[]? TokenHash { get; private init; }
    public string Authentication => Transport == "stdio" ? "process" : TokenHash is null ? "loopback" : "bearer";

    public static McpLaunchOptions Parse(string[] args)
    {
        string transport = "stdio";
        string? url = null;
        bool readOnly = false;
        var toolsets = new HashSet<string>(["core", "host", "vm", "devices", "console", "utilities"], StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (name == "--read-only") { readOnly = true; continue; }
            if (name is not ("--transport" or "--url" or "--toolsets"))
                throw new ArgumentException($"未知参数：{name}。使用 --help 查看启动方法。");
            if (++index >= args.Length) throw new ArgumentException($"请为 {name} 填写值。");
            if (name == "--transport") transport = args[index].ToLowerInvariant();
            else if (name == "--url") url = args[index];
            else
            {
                var selected = args[index].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (selected.Length == 0 || selected.Any(value => !new[] { "core", "host", "vm", "devices", "console", "utilities" }.Contains(value))) throw new ArgumentException("--toolsets 使用 host,vm,devices,console,utilities，可用逗号组合。");
                toolsets = new(selected.Append("core"), StringComparer.Ordinal);
            }
        }

        if (transport is not ("stdio" or "http")) throw new ArgumentException("--transport 可填写 stdio 或 http。");
        if (transport == "stdio")
        {
            if (url is not null) throw new ArgumentException("--url 用于 HTTP 模式，请同时设置 --transport http。");
            return new() { ReadOnly = readOnly, Toolsets = toolsets };
        }

        if (!Uri.TryCreate(url ?? "http://127.0.0.1:5188", UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https") || address.AbsolutePath != "/"
            || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0
            || address.Port is < 1 or > 65535)
            throw new ArgumentException("--url 使用 http(s)://地址:端口，例如 http://127.0.0.1:5188；MCP 路径固定为 /mcp。");

        string host = address.DnsSafeHost;
        bool localhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!IPAddress.TryParse(host, out var ip) && !localhost)
            throw new ArgumentException("监听地址使用 IP 或 localhost；0.0.0.0 监听全部 IPv4 网卡，[::] 监听 IPv6。");
        bool loopback = localhost || ip is not null && IPAddress.IsLoopback(ip);
        string? token = Environment.GetEnvironmentVariable("EXHYPERV_MCP_TOKEN");
        if (string.IsNullOrEmpty(token)) token = null;
        if (token is not null && (token.Length < 32 || token.Length > 512
            || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c)))))
            throw new ArgumentException("EXHYPERV_MCP_TOKEN 使用 32 至 512 位字母、数字或 -._~+/= 字符。");
        if (!loopback && token is null)
            throw new ArgumentException("内网监听需要访问令牌。请先设置 EXHYPERV_MCP_TOKEN，再启动 HTTP 服务。");

        return new()
        {
            Transport = transport, Url = address, LoopbackOnly = loopback,
            ReadOnly = readOnly, Toolsets = toolsets,
            TokenHash = token is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token))
        };
    }
}
