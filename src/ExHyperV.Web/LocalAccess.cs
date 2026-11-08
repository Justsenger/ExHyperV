using System.Net;

namespace ExHyperV.Web;

internal static class LocalAccess
{
    public static bool IsAllowed(HttpContext context, int port)
    {
        var remote = context.Connection.RemoteIpAddress;
        var host = context.Request.Host;
        if (remote is null || !IPAddress.IsLoopback(remote) || host.Port != port
            || host.Host is not ("127.0.0.1" or "localhost")) return false;
        return context.Request.Headers["Sec-Fetch-Site"] != "cross-site"
            || (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/"
                && context.Request.Headers["Sec-Fetch-Mode"] == "navigate"
                && context.Request.Headers["Sec-Fetch-Dest"] == "document");
    }

    public static bool HasSameOrigin(HttpContext context, int port) =>
        Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && origin.Scheme == "http" && origin.Port == port
        && origin.Host == context.Request.Host.Host
        && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.UserInfo.Length == 0;
}
