using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ExHyperV.Core.Tests.TestExecution;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class InspectionHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var port = 5187;
            string? reports = null, configuration = null, pagePath = null, updateError = null;
            var supervised = false;
            var headless = false;
            var openBrowser = true;
            var deployment = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--port": port = int.Parse(args[++i]); break;
                    case "--reports": reports = Path.GetFullPath(args[++i]); break;
                    case "--configuration": configuration = Path.GetFullPath(args[++i]); break;
                    case "--report-only": headless = true; break;
                    case "--no-browser": openBrowser = false; break;
                    case "--page": pagePath = Path.GetFullPath(args[++i]); break;
                    case "--supervised": supervised = true; break;
                    case "--update-error": updateError = args[++i]; break;
                    case "--deployment": deployment = true; break;
                    default: throw new ArgumentException($"未知参数：{args[i]}");
                }
            }
            if (port is < 1024 or > 65535) throw new ArgumentException("端口必须在 1024–65535 之间。");
            if (reports is null) throw new ArgumentException("需要 --reports 指定本地报告目录。");
            configuration ??= InspectionConfiguration.DefaultPath;
            var registration = new ResourceRegistration(configuration);
            _ = registration.Read(); // Reject invalid registration before scanning.
            var scanner = new ResourceScanner(reports, configuration);
            if (headless)
            {
                using var cancelled = new CancellationTokenSource();
                ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
                Console.CancelKeyPress += handler;
                try
                {
                    scanner.TryStart(cancelled.Token);
                    await scanner.Completion;
                    var report = scanner.Snapshot();
                    Console.WriteLine($"资源报告：{Path.Combine(reports, report.RunId, "resources.html")}");
                    // Readiness is not a Core test verdict. Errors must still fail unattended checks.
                    return report.Error is not null || report.Items.Any(i => i.Result.Status == InspectionStatus.Unavailable) ? 1 : 0;
                }
                finally { Console.CancelKeyPress -= handler; }
            }

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear(); // A machine-wide URL setting cannot expose inventory externally.
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
            await using var app = builder.Build();
            var testRunner = new VmQueryTestRunner(reports, workspaceProvider: () => registration.Read().Configuration.WorkDirectory!, inspectionPort: port);
            var operationGate = new object();
            var restartRequested = false;
            var deploymentFinished = false;
            var page = new InspectionPageSource(pagePath);
            var instanceId = Guid.NewGuid().ToString("N");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var url = $"http://127.0.0.1:{port}";
            app.Use(async (context, next) =>
            {
                if (context.Request.Host.Host != "127.0.0.1" || context.Request.Host.Port != port)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'nonce-{token}'; style-src 'nonce-{token}'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
                await next();
            });
            object Session(InspectionPageTemplate template) => new
            {
                instanceId, pageRevision = template.Revision, pagePath, supervised, deployment, configurationPath = configuration,
                restarting = restartRequested, updateError = updateError ?? template.Error
            };
            app.MapGet("/", () =>
            {
                var template = page.Read();
                var session = System.Text.Json.JsonSerializer.Serialize(Session(template), InspectionJson.Options);
                return Results.Content(InspectionPage.Render(null, token, template: template.Html, sessionJson: session), "text/html; charset=utf-8");
            });
            app.MapGet("/api/session", () => Results.Json(Session(page.Read()), InspectionJson.Options));
            app.MapPost("/api/restart", (HttpContext context) =>
            {
                if (context.Request.Headers["X-Inspection-Token"] != token || context.Request.Headers.Origin != url)
                    return Results.StatusCode(403);
                lock (operationGate)
                {
                    if (!supervised) return Results.Conflict(new { error = "请通过启动脚本接入自动更新。" });
                    if (restartRequested || scanner.Snapshot().Running || testRunner.Snapshot()?.Running == true)
                        return Results.Conflict(new { error = "当前任务执行中，请完成后更新。" });
                    restartRequested = true;
                    context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
                    return Results.Accepted();
                }
            });
            app.MapGet("/api/report", () => Results.Json(scanner.Snapshot(), InspectionJson.Options));
            app.MapGet("/api/resources", () => Results.Json(registration.Read(), InspectionJson.Options));
            app.MapGet("/api/deployment", () => Results.Json(new
            {
                issues = Automation.DeploymentSetup.GetIssues(scanner.Snapshot()),
                available = Automation.DeploymentSetup.AvailableCount(scanner.Snapshot()),
                total = scanner.Snapshot().Plan.Items.Length
            }, InspectionJson.Options));
            app.MapPost("/api/deployment/complete", (HttpContext context) =>
            {
                if (context.Request.Headers["X-Inspection-Token"] != token || context.Request.Headers.Origin != url)
                    return Results.StatusCode(403);
                lock (operationGate)
                {
                    if (!deployment) return Results.NotFound();
                    var report = scanner.Snapshot();
                    var issues = Automation.DeploymentSetup.GetIssues(report);
                    if (issues.Length > 0) return Results.Conflict(new { error = string.Join("；", issues) });
                    if (!File.Exists(configuration)) return Results.Conflict(new { error = "请先保存资源选择。" });
                    var current = registration.Read();
                    var scanned = InspectionConfiguration.Read(Path.Combine(reports, report.RunId, "resource-settings.json"));
                    string Serialize(InspectionConfiguration value) => System.Text.Json.JsonSerializer.Serialize(value, InspectionJson.Options);
                    if (Serialize(scanned) != Serialize(current.Configuration)) return Results.Conflict(new { error = "资源选择已更新，请保存并重新检查。" });
                    DurableJson.Write(Path.Combine(reports, "deployment.json"), new
                    {
                        resourceRunId = report.RunId, configurationRevision = current.Revision, completed = DateTimeOffset.Now,
                        available = Automation.DeploymentSetup.AvailableCount(report), total = report.Plan.Items.Length
                    });
                    deploymentFinished = true;
                    context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
                    return Results.Ok(new { message = "资源选择已保存，后台正在完成接单设置。此页可以关闭。" });
                }
            });
            app.MapPost("/api/resources", async (HttpContext context) =>
            {
                if (context.Request.Headers["X-Inspection-Token"] != token || context.Request.Headers.Origin != url)
                    return Results.StatusCode(403);
                if (context.Request.ContentLength > 65536) return Results.StatusCode(413);
                SaveRegistrationRequest? request;
                try { request = await context.Request.ReadFromJsonAsync<SaveRegistrationRequest>(InspectionJson.Options); }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "资源设置格式错误，请检查输入。" }); }
                if (request is null) return Results.BadRequest(new { error = "请填写资源设置。" });
                lock (operationGate)
                {
                    if (restartRequested || scanner.Snapshot().Running || testRunner.Snapshot()?.Running == true)
                        return Results.Conflict(new { error = "请等当前检查或测试结束后保存。" });
                    try
                    {
                        var saved = registration.Save(request, scanner.Snapshot());
                        scanner.TryStart(app.Lifetime.ApplicationStopping);
                        return Results.Json(saved, InspectionJson.Options);
                    }
                    catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    { return Results.BadRequest(new { error = $"保存失败：{error.Message}" }); }
                }
            });
            app.MapGet("/api/tests", () =>
            {
                var current = testRunner.Snapshot();
                return current is null ? Results.Content("null", "application/json") : Results.Json(current, InspectionJson.Options);
            });
            app.MapPost("/api/scan", (HttpContext context) =>
            {
                if (context.Request.Headers["X-Inspection-Token"] != token || context.Request.Headers.Origin != url)
                    return Results.StatusCode(403);
                lock (operationGate)
                {
                    if (restartRequested || testRunner.Snapshot()?.Running == true) return Results.Conflict();
                    return scanner.TryStart(app.Lifetime.ApplicationStopping) ? Results.Accepted() : Results.Conflict();
                }
            });
            app.MapPost("/api/tests", async (HttpContext context) =>
            {
                if (context.Request.Headers["X-Inspection-Token"] != token || context.Request.Headers.Origin != url)
                    return Results.StatusCode(403);
                if (deployment) return Results.Conflict(new { error = "部署页面只保存资源，测试由 Runner 任务启动。" });
                StartApiTestRequest? request;
                try { request = await context.Request.ReadFromJsonAsync<StartApiTestRequest>(InspectionJson.Options); }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "测试请求格式错误。" }); }
                if (request is null) return Results.BadRequest(new { error = "测试请求为空。" });
                lock (operationGate)
                {
                    if (restartRequested) return Results.Conflict(new { error = "正在更新程序。" });
                    if (!testRunner.TryStart(scanner.Snapshot(), request, app.Lifetime.ApplicationStopping, out var reason))
                        return Results.Conflict(new { error = reason });
                    return Results.Json(testRunner.Snapshot(), InspectionJson.Options, statusCode: 202);
                }
            });
            app.MapPost("/api/tests/{runId}/cancel", (HttpContext context, string runId) =>
            {
                if (context.Request.Headers["X-Inspection-Token"] != token || context.Request.Headers.Origin != url)
                    return Results.StatusCode(403);
                return testRunner.Cancel(runId) ? Results.Accepted() : Results.Conflict();
            });
            await app.StartAsync();
            Console.WriteLine($"第一步：资源清单检查  {url}/");
            Console.WriteLine("检查环境、指定资源并保存选择。按 Ctrl+C 关闭页面服务。");
            var resumedResources = RebootContinuation.LatestResources(reports);
            if (resumedResources is null) scanner.TryStart(app.Lifetime.ApplicationStopping);
            else scanner.LoadCompleted(resumedResources);
            if (openBrowser)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception error) { Console.Error.WriteLine($"浏览器未自动打开，请访问上方地址。{error.Message}"); }
            }
            await app.WaitForShutdownAsync();
            await scanner.Completion;
            await testRunner.Completion;
            return restartRequested ? 75 : deployment && !deploymentFinished ? 2 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"资源检查未能启动：{error.Message}");
            return 1;
        }
    }
}

internal static class InspectionPage
{
    private static readonly Lazy<string> TablerStyles = new(() =>
    {
        using var stream = typeof(InspectionPage).Assembly.GetManifestResourceStream("ExHyperV.Core.Tests.ResourceInspection.Assets.Tabler.tabler.min.css")
            ?? throw new InvalidOperationException("测试页面样式未打包。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static string Label(InspectionStatus status) => status switch
    {
        InspectionStatus.Waiting => "等待检查", InspectionStatus.Running => "正在检查",
        InspectionStatus.Ready => "已满足", InspectionStatus.Missing => "未满足",
        InspectionStatus.Pending => "待选择", _ => "无法检查"
    };

    internal static string ReadTemplate()
    {
        using var stream = typeof(InspectionPage).Assembly.GetManifestResourceStream("ExHyperV.Core.Tests.ResourceInspection.Page.html")
            ?? throw new InvalidOperationException("资源检查页面未打包。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static string Render(string? reportJson = null, string token = "", string? testJson = null,
        string? template = null, string? sessionJson = null)
    {
        return (template ?? ReadTemplate()).Replace("__TABLER_CSS__", TablerStyles.Value, StringComparison.Ordinal)
            .Replace("__NONCE__", token, StringComparison.Ordinal)
            .Replace("__REPORT__", reportJson ?? "null", StringComparison.Ordinal)
            .Replace("__TEST_REPORT__", testJson ?? "null", StringComparison.Ordinal)
            .Replace("__SESSION__", sessionJson ?? "null", StringComparison.Ordinal);
    }
}
