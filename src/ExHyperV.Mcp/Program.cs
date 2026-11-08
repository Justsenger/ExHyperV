// Hosting pattern adapted from the official MCP C# QuickstartWeatherServer.
// Source and license are recorded in THIRD-PARTY-NOTICES.txt.
using ExHyperV.Mcp;
using ExHyperV.Mcp.Tools;
using ExHyperV.Mcp.Api;
using ExHyperV.Mcp.Hosting;
using ExHyperV.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Tasks;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("""
        ExHyperV.Mcp
        stdio：ExHyperV.Mcp.exe
        HTTP ：ExHyperV.Mcp.exe --transport http [--url http://127.0.0.1:5188]
        MCP 地址：监听地址 + /mcp。内网监听先设置环境变量 EXHYPERV_MCP_TOKEN（至少 32 位）。
        管理操作使用服务器进程的 Windows 权限；HTTP 模式由用户启动并保持运行，Ctrl+C 正常退出。
        --api-catalog 导出当前 Core 的完整 API 目录。
        --read-only 仅开放只读操作；--toolsets host,vm,devices,console,utilities 选择工具组。
        """);
    return;
}

if (args.Contains("--contract-fingerprint", StringComparer.Ordinal) || args.Contains("--verify-contracts", StringComparer.Ordinal))
{
    var catalog = new ApiCatalog();
    Console.WriteLine(catalog.ContractFingerprint);
    if (args.Contains("--verify-contracts", StringComparer.Ordinal) && !catalog.ContractReviewed)
    {
        Console.Error.WriteLine("Core 的公开契约已变化。请核对新增、删除、参数、分类与只读声明，再更新 Api/Contracts.sha256。");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("--api-catalog", StringComparer.Ordinal))
{
    var catalog = new ApiCatalog();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        types = catalog.Types.Count, members = catalog.Members.Count, coverage = catalog.Coverage(),
        entries = catalog.Members.Values.Select(member => catalog.Describe(member))
    }));
    return;
}

McpLaunchOptions launch;
try { launch = McpLaunchOptions.Parse(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}

if (launch.Transport == "http")
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.WebHost.UseUrls(launch.Url!.GetLeftPart(UriPartial.Authority));
    ConfigureServices(builder, launch);
    var app = builder.Build();
    app.Use((context, next) => HttpAccess.InvokeAsync(context, next, launch));
    app.MapMcp("/mcp");
    Console.Error.WriteLine($"MCP HTTP：{launch.Url.GetLeftPart(UriPartial.Authority)}/mcp（验证：{launch.Authentication}）");
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    ConfigureServices(builder, launch);
    await builder.Build().RunAsync();
}

static void ConfigureServices(IHostApplicationBuilder builder, McpLaunchOptions launch)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Services.AddSingleton(launch);
    builder.Services.AddSingleton<InvocationJournal>();
    var taskStore = new PersistentTaskStore();
    builder.Services.AddSingleton<PersistentTaskStore>(_ => taskStore);
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(2));
    builder.Services.AddSingleton<VmQueryService>();
    builder.Services.AddSingleton<VmGpuService>();
    builder.Services.AddSingleton<QueryTools>();
    builder.Services.AddSingleton<HostTools>();
    builder.Services.AddSingleton<VmTools>();
    builder.Services.AddSingleton<GpuTools>();
    builder.Services.AddSingleton<ApiTools>();
    builder.Services.AddSingleton<GpuSessions>();
    builder.Services.AddSingleton<ApiCatalog>();
    builder.Services.AddSingleton<ApiPolicy>();
    builder.Services.AddSingleton<ApiObjects>();
    builder.Services.AddSingleton<ApiCodec>();
    builder.Services.AddSingleton<ApiExecution>();
    builder.Services.AddHostedService(provider =>
    {
        // Resolve the factory singleton so the host owns and disposes the task-store lock.
        provider.GetRequiredService<PersistentTaskStore>();
        return provider.GetRequiredService<ApiExecution>();
    });
    var mcp = builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "ExHyperV", Version = "2.0.0-Alpha" };
        options.ServerInstructions = "通过 Core 管理本机 Hyper-V。先查询目标 GUID 与当前状态，再执行用户要求的修改。"
            + "所有工具统一返回 success、data、error，Core 原始结果位于 data。core_api_describe 的 inputSchema/valueSchema 描述参数与返回值。"
            + "vm_query_fields 提供字段目录，vm_query 支持按字段和 GUID 选择。字段 HasValue/State 与整个操作的 Success 分开判断。"
            + "写入仅提交明确选择的属性。OutcomeUnknown 表示底层结果尚待核实，应先查询实际状态；自动重试可能重复产生资源。"
            + "GPU 流程先 create，再 start；后续依据 PendingDecision 和 AllowedActions 调用，确认内容交给用户决定。"
            + "完整公开 API 通过 core_api_list 搜索中文说明，core_api_describe 读取参数，core_api_call 调用方法和构造函数。"
            + "core_api_query 用于已核对的只读 API。core_api_get/set 处理属性和字段；普通查询直接返回数据，retainResult=true 可保留数据对象供后续实例调用。"
            + "长操作默认后台执行，用 core_operation_status 查看结果和回调，并用 core_callback_reply 回答；布尔确认可用 core_callback_prompt 交给客户端表单。"
            + "修改工具支持 requestKey；同一请求重试保持相同键和参数。server_requests 保存核对收据，进程重启后先核对实际状态。"
            + "标准 Tasks 由客户端按协议协商；用 Tasks 跟踪 core_api_call 全程时设置 background=false。协议任务取消后通过 core_operations 和 core_operation_status 核对 Core 实际执行状态。"
            + "控制台与监控先订阅事件；core_image_read 可查看帧或截图，core_object_release 负责对象释放。"
            + "编号只在当前 MCP 进程内有效，结束前处理待确认流程和资源。HTTP 客户端共享此管理进程的对象和操作；客户端断线后后台操作仍可通过原编号查询。"
            + "日志使用标准错误，stdio 模式的标准输出专供 MCP 协议。";
    }).WithTools(ToolRegistry.Create(launch)).WithResources<CoreResources>().WithTasks(taskStore);
    if (launch.Transport == "http") mcp.WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);
    else mcp.WithStdioServerTransport();
}
