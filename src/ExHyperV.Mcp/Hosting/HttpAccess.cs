using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace ExHyperV.Mcp.Hosting;

internal static class HttpAccess
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, McpLaunchOptions options)
    {
        var request = context.Request;
        string host = request.Host.Host.Trim('[', ']');
        bool localhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        bool isIp = IPAddress.TryParse(host, out var ip);
        // Restrict Host independently of the socket binding to reject DNS rebinding.
        bool allowedHost = options.LoopbackOnly
            ? localhost || isIp && IPAddress.IsLoopback(ip!)
            : options.Url!.DnsSafeHost is "0.0.0.0" or "::" ? localhost || isIp
            : string.Equals(host, options.Url.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
        if (!allowedHost)
        {
            await Reject(context, StatusCodes.Status403Forbidden, "请求地址与监听地址匹配后再连接。");
            return;
        }

        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
                || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
                || !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var endpoint)
                || !string.Equals(origin.GetLeftPart(UriPartial.Authority), endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                await Reject(context, StatusCodes.Status403Forbidden, "浏览器来源校验失败。");
                return;
            }
        }

        if (options.TokenHash is { } expected)
        {
            bool valid = AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
                && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                && header.Parameter is { Length: >= 32 and <= 512 } token
                && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            if (!valid)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"ExHyperV.Mcp\"";
                await Reject(context, StatusCodes.Status401Unauthorized, "请提供有效的 Bearer 访问令牌。");
                return;
            }
        }
        else if (context.Connection.RemoteIpAddress is not { } remote
            || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "当前配置仅接受本机连接。");
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await next(context);
    }

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}
