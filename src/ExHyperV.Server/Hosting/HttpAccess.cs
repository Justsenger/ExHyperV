using System.Net;
using System.Net.Http.Headers;
using ExHyperV.Server.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace ExHyperV.Server.Hosting;

internal static class HttpAccess
{
    internal static bool SecureConsole(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return context.Request.IsHttps || remote != null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);
    }

    internal static readonly object SessionKey = new();
    internal static async Task Invoke(HttpContext context, RequestDelegate next, LaunchOptions options, Authentication auth)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        string host = request.Host.Host.Trim('[', ']');
        bool localhost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        bool ipHost = IPAddress.TryParse(host, out var ip);
        bool allowed = options.LoopbackOnly ? localhost || ipHost && IPAddress.IsLoopback(ip!)
            : options.Url.DnsSafeHost is "::" or "0.0.0.0" ? localhost || ipHost : ipHost && ip!.Equals(IPAddress.Parse(options.Url.DnsSafeHost));
        if (!allowed) { await Error(context, 403, "请求主机与监听地址不匹配。"); return; }
        var remote = context.Connection.RemoteIpAddress;
        if (!auth.Required && (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
        { await Error(context, 403, "当前配置仅接受本机连接。"); return; }

        var headers = context.Response.Headers;
        headers.Vary = "Origin";
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !LaunchOptions.TryOrigin(origins[0], out var origin)
                || !LaunchOptions.TryOrigin($"{request.Scheme}://{request.Host}", out var endpoint)
                || origin != endpoint && !options.Origins.Contains(origin.GetLeftPart(UriPartial.Authority)))
            { await Error(context, 403, "浏览器来源未获授权。"); return; }
            headers.AccessControlAllowOrigin = origins[0];
            headers.AccessControlAllowMethods = "GET, POST, DELETE, OPTIONS";
            headers.AccessControlAllowHeaders = "Authorization, Content-Type";
            if (HttpMethods.IsOptions(request.Method))
            {
                if (request.Headers["Access-Control-Request-Private-Network"] == "true")
                    headers["Access-Control-Allow-Private-Network"] = "true";
                context.Response.StatusCode = 204;
                return;
            }
        }
        bool ws = request.Path.StartsWithSegments("/ws");
        if (ws && request.Query.Keys.Any(key => key.Equals("token", StringComparison.OrdinalIgnoreCase)
            || key.Equals("access_token", StringComparison.OrdinalIgnoreCase) || key.Equals("rootToken", StringComparison.OrdinalIgnoreCase)))
        {
            headers.WWWAuthenticate = "Bearer";
            await Error(context, 401, "地址中仅接受单次票据。");
            return;
        }
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() == null)
        {
            Guid? session = null;
            bool bearer = AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
                && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && auth.Authenticate(header.Parameter, out session);
            SocketAuthorization? grant = null;
            if (ws && request.Query.TryGetValue("ticket", out var ticket))
                grant = ticket.Count == 1 ? auth.ConsumeTicket(ticket[0]!) : null;
            else if (bearer || !auth.Required)
            {
                try { grant = auth.Principal(session, !bearer); } catch (InvalidCredentialException) { }
            }
            bool authorized = grant != null;
            if (!authorized)
            {
                headers.WWWAuthenticate = "Bearer";
                await Error(context, 401, "请提供有效的 Bearer 访问令牌。");
                return;
            }
            if (grant!.Scoped && (!request.Path.StartsWithSegments("/ws/v2/console")
                || !Guid.TryParse(request.RouteValues["vmId"]?.ToString(), out var vm) || vm != grant.VmId
                || (request.Query["backend"].FirstOrDefault() ?? "RdpBasic") != grant.BackendId))
            { await Error(context, 403, "票据与请求的控制台不匹配。"); return; }
            context.Items[SessionKey] = grant;
        }
        if (ws && !context.WebSockets.IsWebSocketRequest)
        {
            await Error(context, 400, "请使用 WebSocket 升级请求。");
            return;
        }
        try { await next(context); }
        catch (LoginLimitedException)
        {
            headers.RetryAfter = "60";
            await Error(context, 429, "登录尝试过于频繁，请稍后重试。");
        }
        catch (InvalidCredentialException) { await Error(context, 401, "凭证无效或已过期。"); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error) when (!context.Response.HasStarted)
        {
            if (error is System.Reflection.TargetInvocationException { InnerException: { } inner }) error = inner;
            int status = error switch { BadHttpRequestException bad => bad.StatusCode, UnauthorizedAccessException => 403,
                ArgumentException or System.Text.Json.JsonException or FormatException or OverflowException or System.Reflection.TargetException => 400,
                KeyNotFoundException => 404, InvalidOperationException => 409, _ => 500 };
            await Error(context, status, status == 500 ? "服务器处理请求失败。" : DisplayText.Clean(error.Message));
        }
    }

    private static Task Error(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }
}
