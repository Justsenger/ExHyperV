using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed record ApiCaseResult(ApiTestStatus Status, string Detail, string[] Evidence, int Calls, double Milliseconds, bool RecoveryRequired = false);

internal static class ApiCaseWorker
{
    private const string ResultFile = "api-case-result.json";

    internal static async Task<int> RunAsync(string[] args)
    {
        var context = new ApiCaseContext(args[2], CancellationToken.None);
        WindowsTestCommand.RecoveryDirectory.Value = context.DirectoryPath;
        var timer = Stopwatch.StartNew();
        ApiCaseResult result;
        try
        {
            var definition = ApiCaseRegistry.Get(args[1]);
            context.GuardNativeOutcomes = definition.Exclusive && definition.UsesHostResources;
            if (ApiCaseRegistry.NeedsAzureBackup(definition.Api))
                new HostStateJournal(context).Registry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization", "AzureFeatureSet");
            await definition.Run(context);
            if (context.Assertions == 0) throw new ApiCheckFailureException("执行器完成后缺少结果断言。");
            context.Require(context.Calls > 0, "至少调用一次目标接口");
            result = new(ApiTestStatus.Passed, "已核对输入、预期值与实际结果。", context.Evidence.ToArray(), context.Calls, timer.Elapsed.TotalMilliseconds);
        }
        catch (ApiCheckSkippedException error)
        {
            result = new(ApiTestStatus.Skipped, error.Message, context.Evidence.ToArray(), context.Calls, timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception error)
        {
            result = new(ApiTestStatus.Failed, $"{error.GetType().Name}：{error.Message}", context.Evidence.ToArray(), context.Calls, timer.Elapsed.TotalMilliseconds);
        }
        finally { WindowsTestCommand.RecoveryDirectory.Value = null; }
        // Native libraries can write to stdout, even while the process is exiting.
        DurableJson.Write(Path.Combine(context.DirectoryPath, ResultFile), result);
        return 0;
    }

    internal static async Task<ApiCaseResult> ExecuteAsync(string id, string workspace, CancellationToken cancellation, string? configurationPath = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var definition = ApiCaseRegistry.Get(id);
        var root = Path.GetFullPath(Path.Combine(workspace, "ApiCases"));
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ApiCaseResult result;
        var timer = Stopwatch.StartNew();
        try
        {
            if (configurationPath is not null && File.Exists(configurationPath))
                File.Copy(configurationPath, Path.Combine(directory, "resource-settings.json"));
            var executable = Environment.ProcessPath ?? throw new IOException("测试程序路径缺失。");
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "--api-test-worker", id, directory }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("测试工作进程启动失败。");
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var error = process.StandardError.ReadToEndAsync();
            // Native cases finish releasing their resources before responding to cancellation.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(definition.Exclusive ? CancellationToken.None : cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(definition.TimeoutSeconds, definition.Exclusive ? 120 : 30)));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await output;
                var stderr = await error;
                try
                {
                    if (process.ExitCode != 0) throw new IOException($"工作进程退出码 {process.ExitCode}：{stderr.Trim()}");
                    var json = await File.ReadAllTextAsync(Path.Combine(directory, ResultFile));
                    result = JsonSerializer.Deserialize<ApiCaseResult>(json, InspectionJson.Options) ?? throw new InvalidDataException("工作进程结果缺失。");
                }
                catch
                {
                    if (definition.Exclusive && definition.UsesHostResources)
                        File.WriteAllText(Path.Combine(directory, "recovery-hold.txt"), "工作进程异常结束，需核对原生作业终态后再恢复资源。");
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(); await Task.WhenAll(output, error);
                if (definition.Exclusive && definition.UsesHostResources)
                    File.WriteAllText(Path.Combine(directory, "recovery-hold.txt"), "工作进程被结束时原生写入可能仍在进行，先核对实际作业终态再恢复本项资源。");
                result = new(cancellation.IsCancellationRequested ? ApiTestStatus.Cancelled : ApiTestStatus.Failed,
                    cancellation.IsCancellationRequested ? "已停止工作进程。" : "执行超时，已结束工作进程。", [], 0, timer.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception error) { result = new(ApiTestStatus.Failed, error.Message, [], 0, timer.Elapsed.TotalMilliseconds); }
        try
        {
            if (File.Exists(Path.Combine(directory, "recovery-hold.txt"))) throw new IOException(File.ReadAllText(Path.Combine(directory, "recovery-hold.txt")));
            await PcieApiCases.RestoreAsync(directory);
            await TemporaryVm.CleanupAsync(directory);
            await TemporaryDisk.CleanupMountsAsync(directory);
            await TemporaryCpuGroups.CleanupAsync(directory);
            await SwitchApiCases.RestoreAsync(directory);
            await TemporarySwitch.CleanupAllAsync(directory);
            await TemporaryGroup.CleanupAllAsync(directory);
            await HostStateJournal.RecoverAsync(directory);
            if (definition.Api == "IsoBuilderService.BuildUdfIso()") await IsoApiCases.ReleaseImageAsync(directory);
            // Only delete the newly allocated child, never a supplied workspace root.
            if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("测试目录范围校验失败。");
            Directory.Delete(directory, recursive: true);
            result = result with { Evidence = [.. result.Evidence, "本项临时文件已清理。"] };
        }
        catch (Exception error)
        {
            result = result with
            {
                Status = ApiTestStatus.Failed,
                RecoveryRequired = true,
                Detail = $"{result.Detail}\n测试资源清理失败：{error.Message}",
                Evidence = [.. result.Evidence, $"保留目录：{directory}"]
            };
        }
        return result;
    }
}
