using System.Diagnostics;
using System.Text.Json;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal sealed class ResourceScanner(string reportDirectory, string? configurationPath,
    Func<ResourceCheck, CancellationToken, Task<ResourceResult>>? probe = null)
{
    private readonly object gate = new();
    private InspectionReport report = new("", DateTimeOffset.Now, null, false, ExecutionContext(), null,
        ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Waiting, "等待检查"))).ToArray());
    private Task completion = Task.CompletedTask;
    private string? runConfigurationPath;

    public InspectionReport Snapshot() { lock (gate) return report with { Items = report.Items.ToArray() }; }
    public Task Completion { get { lock (gate) return completion; } }
    internal void LoadCompleted(InspectionReport saved)
    {
        if (saved.Running || saved.Finished is null) throw new InvalidDataException("持久资源报告尚未完成。");
        lock (gate) report = saved;
    }

    public bool TryStart(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (report.Running) return false;
            report = new(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, null, true, ExecutionContext(), null,
                ResourceProbes.Checks.Select(c => new InspectionItem(c, new(InspectionStatus.Waiting, "等待检查"))).ToArray());
            completion = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
            return true;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(reportDirectory);
            var runDirectory = Path.Combine(reportDirectory, Snapshot().RunId);
            Directory.CreateDirectory(runDirectory);
            runConfigurationPath = Path.Combine(runDirectory, "resource-settings.json");
            AtomicWrite(runConfigurationPath, JsonSerializer.Serialize(InspectionConfiguration.Read(configurationPath), InspectionJson.Options));
            for (var i = 0; i < ResourceProbes.Checks.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetResult(i, new(InspectionStatus.Running, "正在读取…"));
                ResourceResult result;
                try { result = await (probe ?? RunProbeAsync)(ResourceProbes.Checks[i], cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { result = ResourceProbes.Failure(error); }
                if (result.Status is InspectionStatus.Running or InspectionStatus.Waiting)
                    result = new(InspectionStatus.Unavailable, "检查程序没有返回最终结果。");
                SetResult(i, result);
                Console.WriteLine($"[{i + 1}/{ResourceProbes.Checks.Length}] {ResourceProbes.Checks[i].Name}：{InspectionPage.Label(result.Status)}");
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                report = report with { Error = error is OperationCanceledException ? "检查已停止，未完成的项目不能视为满足。" : $"检查中断：{error.Message}" };
                report = report with { Items = report.Items.Select(item => item.Result.Status is InspectionStatus.Waiting or InspectionStatus.Running
                    ? item with { Result = new(InspectionStatus.Unavailable, "本轮检查未完成。") } : item).ToArray() };
            }
        }
        finally
        {
            // Keep the completion and final save together: a new scan must not replace
            // the snapshot while the preceding run is still writing its report.
            lock (gate)
            {
                report = report with { Running = false, Finished = DateTimeOffset.Now };
                try { Save(); }
                catch (Exception error)
                {
                    report = report with { Error = $"无法保存报告：{error.Message}" };
                    Console.Error.WriteLine(report.Error);
                }
            }
        }
    }

    private void SetResult(int index, ResourceResult result)
    {
        lock (gate) report.Items[index] = report.Items[index] with { Result = result };
    }

    private void Save()
    {
        var snapshot = Snapshot();
        var json = JsonSerializer.Serialize(snapshot, InspectionJson.Options);
        var directory = Path.Combine(reportDirectory, snapshot.RunId);
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(directory, "resources.json"), json);
        if (!snapshot.Running) AtomicWrite(Path.Combine(directory, "resources.html"), InspectionPage.Render(json));
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<ResourceResult> RunProbeAsync(ResourceCheck check, CancellationToken cancellationToken)
    {
        using var process = Process.Start(ResourceProbes.WorkerStart(check.Id, runConfigurationPath))
            ?? throw new InvalidOperationException("无法启动检查进程。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0) return new(InspectionStatus.Unavailable, $"检查进程异常退出（{process.ExitCode}）：{stderr.Trim()}");
            return JsonSerializer.Deserialize<ResourceResult>(stdout, InspectionJson.Options)
                ?? new(InspectionStatus.Unavailable, "检查程序未返回结果。");
        }
        catch (OperationCanceledException)
        {
            // Only this resource probe and its child processes belong to the scanner.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            cancellationToken.ThrowIfCancellationRequested();
            return new(InspectionStatus.Unavailable, "读取超过 25 秒，已停止本项检查进程；不能据此认定资源不存在。");
        }
    }

    private static string ExecutionContext() => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))
        ? "本机"
        : "Runner";
}
