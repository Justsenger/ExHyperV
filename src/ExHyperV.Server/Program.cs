using System.Net;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Server.Api;
using ExHyperV.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExHyperV.Server;

internal static class Program
{
    internal static readonly string Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private const string Help = """
        ExHyperV.Server 使用当前 Windows 进程的权限管理 Hyper-V。
        --url <地址> 默认 http://127.0.0.1:5191，支持 HTTP 和 HTTPS。
        --cors-origins <来源列表> 使用逗号分隔受信任的浏览器 Origin。
        --read-only 仅执行契约已确认的只读 API。
        --toolsets <工具组> 使用逗号选择 core,host,vm,devices,console,utilities。
        --state-dir <目录> 指定持久状态目录，也可设置 EXHYPERV_SERVER_STATE_DIRECTORY。
        --set-password 从标准输入读取一行访问口令并保存，--password <口令> 可直接提供值。
        --clear-password 清除持久口令。通过命令行更改后，请重启运行中的服务。
        EXHYPERV_SERVER_TOKEN 支持 32 至 512 位字母、数字或 -._~+/= 字符。
        非回环监听需要预先配置令牌或口令。远程传输凭证请使用 HTTPS。
        --service 使用 Windows Service 生命周期。控制台支持 Ctrl+C，重定向输入可发送 shutdown 单独一行退出。
        --save-config 将有效配置保存到状态目录的 server.json 后退出。--read-write 可覆盖只读配置。
        --install-service 注册自动启动的 Windows 服务。--uninstall-service 注销服务。
        --service-name <名称> 指定 SCM 服务名，默认 ExHyperV.Server。
        --contract-fingerprint 输出当前 Core 契约指纹。
        --verify-contracts 校验内嵌指纹，契约变更时返回非零退出码。
        --api-catalog 导出完整 Core API 目录。
        --version 输出版本。--help 或 -h 显示帮助。
        """;

    internal static async Task<int> Main(string[] args)
    {
        try
        {
            var options = LaunchOptions.Parse(args);
            switch (options.Command)
            {
                case Command.Help: Console.WriteLine(Help); return 0;
                case Command.Version: Console.WriteLine(Version); return 0;
                case Command.Fingerprint: case Command.Verify: case Command.Catalog:
                    var catalog = new ApiCatalog();
                    if (options.Command == Command.Catalog)
                        JsonSerializer.Serialize(Console.OpenStandardOutput(), new { types = catalog.Types.Count, members = catalog.Members.Count,
                            coverage = catalog.Coverage(), entries = catalog.Members.Values.Select(m => catalog.Describe(m)) });
                    else Console.WriteLine(catalog.ContractFingerprint);
                    if (options.Command == Command.Verify && !catalog.ContractReviewed)
                    { Console.Error.WriteLine("Core 公开契约已变化，请核对签名和权限分类后更新指纹。"); return 1; }
                    return 0;
                case Command.SaveConfig:
                    new ServerConfigStore(options.StateDirectory).Save(options);
                    Console.WriteLine("运行配置已保存。"); return 0;
                case Command.Install: case Command.Uninstall:
                    return await ServiceCommandBuilder.Execute(options, args);
                case Command.SetPassword:
                    new PasswordStore(options.StateDirectory).Save(PasswordHash.Create(options.Password ?? ReadPassword()));
                    Console.WriteLine("访问口令已保存。"); return 0;
                case Command.ClearPassword:
                    new PasswordStore(options.StateDirectory).Clear();
                    Console.WriteLine("访问口令已清除。"); return 0;
            }

            await using var app = Build(options, Environment.GetEnvironmentVariable("EXHYPERV_SERVER_TOKEN"));
            if (!options.Service && Console.IsInputRedirected)
                _ = Task.Run(() =>
                {
                    while (Console.ReadLine() is { } line)
                        if (line == "shutdown") { app.Lifetime.StopApplication(); break; }
                });
            await app.RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(DisplayText.Clean(error is InvalidDataException ? error.Message : error.GetBaseException().Message));
            return 2;
        }
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? throw new ArgumentException("标准输入需要一行口令。");
        Console.Error.Write("输入访问口令：");
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return password.ToString(); }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
    }

    internal static WebApplication Build(LaunchOptions options, string? token, Action<IServiceCollection>? configure = null, TimeProvider? clock = null)
    {
        var auth = new Authentication(options, token, clock ?? TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        if (options.Service) builder.Host.UseWindowsService(service => service.ServiceName = options.ServiceName);
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(server =>
        {
            // The validated listener is authoritative, including when Kestrel endpoint settings exist in the environment.
            var certificates = builder.Configuration.GetSection("Kestrel:Certificates").AsEnumerable(makePathsRelative: true)
                .Where(pair => pair.Value != null).ToDictionary(pair => "Certificates:" + pair.Key, pair => pair.Value);
            server.Configure(new ConfigurationBuilder().AddInMemoryCollection(certificates).Build(), reloadOnChange: false);
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
            void Configure(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listener)
            {
                if (options.Url.Scheme == "https") listener.UseHttps();
            }
            if (options.Url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) server.ListenLocalhost(options.Url.Port, Configure);
            else server.Listen(IPAddress.Parse(options.Url.DnsSafeHost), options.Url.Port, Configure);
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton<ApiCatalog>();
        builder.Services.AddSingleton<ApiPolicy>();
        builder.Services.AddSingleton<ApiObjects>();
        builder.Services.AddSingleton<ApiCodec>();
        builder.Services.AddSingleton<ApiExecution>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ApiExecution>());
        builder.Services.AddSingleton<CoreGateway>();
        builder.Services.AddSingleton<LoginResources>();
        builder.Services.AddHostedService(p => p.GetRequiredService<LoginResources>());
        builder.Services.AddSingleton<SocketSessions>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SocketSessions>());
        builder.Services.AddSingleton<IConsoleSessionFactory, CoreConsoleSessionFactory>();
        builder.Services.AddSingleton<ConsoleTransport>();
        builder.Services.AddSingleton<ConsoleSlots>();
        builder.Services.AddSingleton<ConsoleDirectory>();
        builder.Services.AddSingleton<ConsoleSources>();
        builder.Services.AddSingleton<Func<ITelemetrySampler>>(() => new CoreTelemetrySampler());
        configure?.Invoke(builder.Services);
        if (!builder.Services.Any(s => s.ServiceType == typeof(IConsoleExtensionProvider)))
        {
            builder.Services.TryAddSingleton<ExHyperV.ConsoleIpc.ConsoleIpcDiscovery>();
            builder.Services.AddSingleton<IConsoleExtensionProvider, ConsoleIpcServerProvider>();
        }
        var app = builder.Build();
        // Synchronous HTTP calls may be waiting for a Core decision while Kestrel drains requests.
        app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<ApiExecution>().BeginStop);
        app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<SocketSessions>().BeginStop);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseRouting();
        app.Use((context, next) => HttpAccess.Invoke(context, next, options, auth));
        var resources = app.Services.GetRequiredService<LoginResources>();
        app.Lifetime.ApplicationStopping.Register(resources.BeginStop);
        app.Use((context, next) => resources.Invoke(context, next));
        Endpoints.Map(app, options, auth);
        SocketRoutes.Map(app);
        return app;
    }
}
