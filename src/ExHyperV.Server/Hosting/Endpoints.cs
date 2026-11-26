using System.Runtime.InteropServices;
using System.Security.Principal;
using ExHyperV.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ExHyperV.Server.Hosting;

internal sealed record PasswordRequest(string Password);

internal static class Endpoints
{
    internal static void Map(WebApplication app, LaunchOptions options, Authentication auth)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/health", () => new { status = "ok", version = Program.Version, authRequired = auth.Required }).AllowAnonymous();
        api.MapPost("/auth/login", (LoginRequest body, HttpContext context) =>
            auth.Login(body, context.Connection.RemoteIpAddress!.MapToIPv6().ToString())).AllowAnonymous();
        api.MapGet("/auth/sessions", () => new { sessions = auth.List() });
        api.MapDelete("/auth/sessions/{sessionId:guid}", (Guid sessionId) => auth.Revoke(sessionId) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/auth/logout", (HttpContext context) =>
        {
            if (context.Items[HttpAccess.SessionKey] is SocketAuthorization { SessionId: Guid id }) auth.Revoke(id);
            return Results.NoContent();
        });
        api.MapPost("/auth/password", (PasswordRequest body) =>
        {
            if (options.ReadOnly) throw new UnauthorizedAccessException("只读模式禁止修改访问口令。");
            auth.SetPassword(body.Password);
            return Results.NoContent();
        });
        api.MapPost("/auth/ws-ticket", async (HttpContext context, TicketScope? scope, ConsoleDirectory directory) =>
        {
            var grant = (SocketAuthorization)context.Items[HttpAccess.SessionKey]!;
            if (scope == null || scope.Empty) return auth.IssueGrant(grant);
            if (grant.Subject == "loopback" || !options.Toolsets.Contains("console")) throw new UnauthorizedAccessException("限域控制台需要登录及控制台工具组。");
            var requested = scope.Validate();
            var target = await directory.InspectVm(scope.VmId!.Value, context.RequestAborted);
            var allowed = directory.Allowed(scope.BackendId!, scope.VmId!.Value, grant, options, target, HttpAccess.SecureConsole(context), out var access);
            if ((requested & allowed) != requested) throw new UnauthorizedAccessException("请求的控制台权限未获准。");
            return auth.IssueGrant(grant with { VmId = scope.VmId, Channel = scope.Channel, BackendId = scope.BackendId,
                Rights = requested, ViewerContextId = scope.ViewerContextId, ExtensionVersion = access?.Snapshot.Version });
        });
        api.MapGet("/vms/{vmId:guid}/console/backends", async (Guid vmId, HttpContext context, ConsoleDirectory directory) =>
        {
            if (vmId == Guid.Empty) throw new ArgumentException("需要有效的虚拟机标识。");
            if (!options.Toolsets.Contains("console")) throw new UnauthorizedAccessException("控制台工具组未启用。");
            var viewer = (SocketAuthorization)context.Items[HttpAccess.SessionKey]!;
            if (viewer.Subject == "loopback") throw new UnauthorizedAccessException("请先登录以查询虚拟机控制台。");
            var target = await directory.InspectVm(vmId, context.RequestAborted);
            return new { vmId, backends = directory.Backends.Select(backendId =>
            {
                var rights = directory.Allowed(backendId, vmId, viewer, options, target, HttpAccess.SecureConsole(context), out var access);
                return new { backendId, rights = Enum.GetValues<ConsoleRights>().Where(r => r is not (ConsoleRights.None or ConsoleRights.All) && rights.HasFlag(r)).Select(r => r.ToString()),
                    extension = access?.Snapshot, modes = access?.Modes ?? [] };
            }) };
        });
        api.MapGet("/server/info", (ApiCatalog catalog, ConsoleDirectory directory) =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            var h264 = ConsoleH264Encoder.Probe;
            return new { version = Program.Version, isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                authenticationMode = auth.Mode, readOnly = options.ReadOnly, toolsets = options.Toolsets, url = options.Url.GetLeftPart(UriPartial.Authority),
                catalog.ContractFingerprint, catalog.ContractReviewed, osArchitecture = RuntimeInformation.OSArchitecture.ToString(), processId = Environment.ProcessId,
                consoleProtocolVersions = new[] { 1, 2 }, consoleBackends = directory.Backends, extensionBackends = directory.ExtensionIds,
                extensionSummary = directory.HostSummary(), consoleCapabilities = new { scopedTickets = true, viewOnly = true, dirtyRegions = true,
                    moveRegions = true, independentCursor = true, displayModes = true },
                supportedConsoleEncodings = h264.SupportedEncodings, h264VideoProcessorStatus = h264.VideoProcessorStatus, h264VideoProcessorAvailable = h264.VideoProcessorAvailable, h264HardwareAvailable = h264.HardwareAvailable };
        });
        api.MapGet("/openapi.json", () => OpenApiDocument.Document);
        api.MapGet("/meta/field-help", () => Resource("Core.FieldHelp.json", "application/json"));
        api.MapGet("/meta/guide", () => Resource("Core.API.md", "text/markdown"));
        api.MapGet("/core/catalog", (CoreGateway core, string? search, string? group, string? kind, bool? includeAuxiliary, bool? readOnlyOnly, int? limit, int? offset)
            => core.Catalog(search, group, kind, includeAuxiliary ?? false, readOnlyOnly ?? false, offset ?? 0, limit ?? 30));
        api.MapGet("/core/describe", (string apiId, CoreGateway core) => core.Describe(apiId));
        api.MapPost("/core/call", (CallRequest body, CoreGateway core, CancellationToken token) => core.Call(body, token));
        api.MapPost("/core/get", (AccessRequest body, CoreGateway core, CancellationToken token) => core.Access(body, false, token));
        api.MapPost("/core/set", (AccessRequest body, CoreGateway core, CancellationToken token) => core.Access(body, true, token));
        api.MapGet("/objects", (ApiObjects objects) => new { objects = objects.List() });
        api.MapDelete("/objects/{objectId:guid}", (Guid objectId, ApiObjects objects) => objects.ReleaseAsync(objectId));
        api.MapGet("/operations", (ApiExecution execution) => new { operations = execution.List() });
        api.MapGet("/operations/{operationId:guid}", (Guid operationId, int? eventLimit, ApiExecution execution) => execution.Status(operationId, eventLimit ?? 30));
        api.MapPost("/operations/{operationId:guid}/cancel", (Guid operationId, ApiExecution execution) =>
        {
            execution.Cancel(operationId);
            return new { operationId, cancellationRequested = true };
        });
        api.MapPost("/operations/{operationId:guid}/reply", (Guid operationId, ReplyRequest body, ApiExecution execution) =>
        {
            if (body.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined) throw new ArgumentException("请提供回调响应 value。");
            execution.Reply(operationId, body.RequestId, body.Value);
            return Results.NoContent();
        });
        api.MapDelete("/operations/{operationId:guid}", (Guid operationId, ApiExecution execution) => { execution.Forget(operationId); return Results.NoContent(); });
        api.MapPost("/events/subscribe", (SubscribeRequest body, CoreGateway core) => core.Subscribe(body));
        api.MapGet("/events/{subscriptionId:guid}", (Guid subscriptionId, int? limit, ApiExecution execution) => execution.Events(subscriptionId, limit ?? 30));
        api.MapDelete("/events/{subscriptionId:guid}", (Guid subscriptionId, ApiExecution execution) => { execution.Unsubscribe(subscriptionId); return Results.NoContent(); });
        api.MapGet("/binaries/{objectId:guid}", (Guid objectId, HttpContext context, CoreGateway core) => core.SendBinary(objectId, context));
    }

    private static IResult Resource(string name, string mediaType) =>
        Results.Stream(typeof(Endpoints).Assembly.GetManifestResourceStream(name)!, mediaType);
}
