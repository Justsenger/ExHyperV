using System.Net;
using ExHyperV.Services;
using ExHyperV.Web;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
int port = builder.Configuration.GetValue("Port", 5197);
string consoleSource = builder.Configuration["ConsoleSource"] ?? ConsoleService.RdpBasicBackendId;
var consoleBackends = ConsoleService.GetBackends();
if (!consoleBackends.Any(backend => backend.BackendId == consoleSource))
    throw new ArgumentException("ConsoleSource must name a backend registered by Core.");
int videoEncoder = (builder.Configuration["VideoEncoder"] ?? "software").ToLowerInvariant() switch
{
    "auto" => 0, "hardware" => 1, "software" => 2,
    _ => throw new ArgumentException("VideoEncoder must be auto, hardware or software.")
};
GStreamerRuntime.Initialize(builder.Configuration["MediaRuntime"] ?? Path.Combine(AppContext.BaseDirectory, "media"));
if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
// This first console host is local only. Do not expose unauthenticated VM input over LAN.
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Services.AddSingleton<VmQueryService>();
builder.Services.AddSingleton<VmCatalog>();
builder.Services.AddSingleton<ConsoleSessions>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!LocalAccess.IsAllowed(context, port))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src 'self' blob:; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(10),
    KeepAliveTimeout = TimeSpan.FromSeconds(10)
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/console/backends", () => Results.Ok(new
{
    defaultBackendId = consoleSource,
    backends = consoleBackends.Select(backend => new
    {
        id = backend.BackendId, name = backend.Name,
        available = backend.IsImplemented && backend.Capabilities.HasFlag(ConsoleCapabilities.Video),
        unavailableReason = backend.UnavailableReason
    })
}));
app.MapGet("/api/vms", async (VmCatalog catalog, ILogger<Program> logger) =>
{
    try
    {
        var result = await catalog.GetAsync();
        if (result.HasData) return Results.Ok(result.Data);
        logger.LogWarning(result.Exception, "Core VM query failed: {Kind} {Source}:{Code} {Error}",
            result.ErrorKind, result.ErrorSource, result.Code, result.Error);
        return Results.Problem(string.IsNullOrWhiteSpace(result.Error) ? "无法读取虚拟机列表。" : result.Error,
            statusCode: 503, extensions: new Dictionary<string, object?>
            {
                ["errorKind"] = result.ErrorKind.ToString(),
                ["errorSource"] = result.ErrorSource.ToString(),
                ["code"] = result.Code
            });
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Unable to read Hyper-V virtual machines");
        return Results.Problem("无法读取虚拟机列表，请重试或查看服务日志。", statusCode: 503);
    }
});
app.Map("/api/console/{vmId:guid}", async (HttpContext context, Guid vmId, VmCatalog catalog,
    ConsoleSessions sessions, IHostApplicationLifetime lifetime, ILogger<ConsoleSession> logger) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!LocalAccess.HasSameOrigin(context, port)) { context.Response.StatusCode = 403; return; }
    if (context.Request.Query.ContainsKey("enhanced")) { context.Response.StatusCode = 400; return; }
    var backendId = context.Request.Query["backend"].ToString();
    if (backendId.Length == 0) backendId = consoleSource;
    var backend = consoleBackends.FirstOrDefault(item => item.BackendId == backendId);
    if (backend is null) { context.Response.StatusCode = 400; return; }
    if (!backend.IsImplemented || !backend.Capabilities.HasFlag(ConsoleCapabilities.Video))
    { context.Response.StatusCode = 503; return; }
    var quality = context.Request.Query["quality"].ToString();
    VideoSettings settings = quality switch
    {
        "low" => new(1280, 720, 30, 2_000_000),
        "balanced" => new(1920, 1080, 60, 4_000_000),
        "" or "high" => new(),
        _ => null!
    };
    if (settings is null) { context.Response.StatusCode = 400; return; }
    settings = settings with { Encoder = videoEncoder };
    VmListItem? vm;
    try
    {
        var result = await catalog.GetAsync();
        if (!result.HasData) { context.Response.StatusCode = 503; return; }
        vm = result.Data.VirtualMachines.FirstOrDefault(item => item.Id == vmId);
    }
    catch { context.Response.StatusCode = 503; return; }
    if (vm is null) { context.Response.StatusCode = 404; return; }
    if (!vm.IsRunning || !sessions.TryEnter(vmId)) { context.Response.StatusCode = 409; return; }
    try
    {
        IConsoleConnection connection;
        try
        {
            connection = ConsoleService.CreateConnection(new ConsoleConnectionOptions
            {
                VmId = vmId, ConnectTimeout = TimeSpan.FromSeconds(10)
            }, backendId);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            logger.LogWarning(exception, "Console backend {Backend} is unavailable", backendId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        await using var ownedConnection = connection;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new ConsoleSession(socket, vmId, settings, logger);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        await session.RunAsync(connection, stop.Token);
    }
    finally { sessions.Exit(vmId); }
});
app.Logger.LogInformation("ExHyperV.Web: http://127.0.0.1:{Port}/", port);
await app.RunAsync();
