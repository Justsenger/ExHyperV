using ExHyperV.Server.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExHyperV.Server.Hosting;

internal sealed class LoginResources(Authentication auth, ApiCatalog catalog, ApiPolicy policy, ILogger<ApiExecution> logger,
    ApiObjects initialObjects, ApiExecution initialExecution, CoreGateway initialGateway) : IHostedService
{
    internal sealed class Resources(ApiCatalog catalog, ApiPolicy policy, ILogger<ApiExecution> logger)
    {
        internal ApiObjects Objects { get; private set; } = null!;
        internal ApiExecution Execution { get; private set; } = null!;
        internal CoreGateway Gateway { get; private set; } = null!;
        internal ActiveSocket Lifetime = null!;
        internal Timer? Expiry;
        internal void Initialize(ApiObjects? initial = null, ApiExecution? execution = null, CoreGateway? gateway = null)
        {
            Objects = initial ?? new();
            if (execution != null && gateway != null) { Execution = execution; Gateway = gateway; return; }
            var codec = new ApiCodec(catalog, Objects, policy);
            Execution = new(catalog, Objects, codec, logger);
            Gateway = new(catalog, policy, Objects, codec, Execution);
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<(string Subject, long Version), Resources> sessions = [];
    private bool stopping;

    internal Resources For(SocketAuthorization grant)
    {
        var id = (grant.Subject, grant.Version);
        lock (gate)
        {
            if (stopping) throw new InvalidOperationException("服务正在停止。");
            if (sessions.TryGetValue(id, out var resources)) return resources;
            resources = new(catalog, policy, logger);
            if (grant.SessionId == null && grant.Version == 1) resources.Initialize(initialObjects, initialExecution, initialGateway);
            else resources.Initialize();
            resources.Lifetime = new(() => _ = Task.Run(async () =>
            {
                try { await resources.Execution.StopAsync(default); }
                finally
                {
                    lock (gate) sessions.Remove(id);
                    resources.Expiry?.Dispose();
                    auth.Unregister(resources.Lifetime);
                }
            }));
            auth.Register(grant, resources.Lifetime);
            sessions.Add(id, resources);
            if (grant.LoginExpiresAt is { } until) resources.Expiry = new Timer(_ => _ = resources.Lifetime.Revoke(), null,
                TimeSpan.FromTicks(Math.Max(0, auth.Remaining(until).Ticks)), Timeout.InfiniteTimeSpan);
            return resources;
        }
    }

    internal async Task Invoke(HttpContext context, RequestDelegate next)
    {
        if (context.Items[HttpAccess.SessionKey] is not SocketAuthorization grant
            || !(context.Request.Path.StartsWithSegments("/ws/v1/events") ||
                new[] { "core", "objects", "operations", "events", "binaries" }.Any(p => context.Request.Path.StartsWithSegments("/api/v1/" + p))))
        { await next(context); return; }
        var request = new ActiveSocket(context.Abort);
        auth.Register(grant, request);
        var original = context.RequestServices;
        try
        {
            context.RequestServices = new Services(original, For(grant));
            await next(context);
        }
        finally { context.RequestServices = original; auth.Unregister(request); }
    }

    private sealed class Services(IServiceProvider original, Resources resources) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(CoreGateway) ? resources.Gateway :
            type == typeof(ApiExecution) ? resources.Execution : type == typeof(ApiObjects) ? resources.Objects : original.GetService(type);
    }

    internal void BeginStop()
    {
        lock (gate)
        {
            stopping = true;
            foreach (var r in sessions.Values) r.Execution.BeginStop();
        }
    }

    public Task StartAsync(CancellationToken token) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken token)
    {
        ActiveSocket[] lifetimes;
        lock (gate) { stopping = true; lifetimes = sessions.Values.Select(s => s.Lifetime).ToArray(); }
        await Task.WhenAll(lifetimes.Select(s => s.Revoke())).WaitAsync(token);
    }
}
