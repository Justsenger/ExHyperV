using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class WindowsTestCommand
{
    internal static readonly AsyncLocal<string?> RecoveryDirectory = new();
    internal static async Task<JsonElement> RunAsync(string script, object? arguments = null, int timeoutSeconds = 45, bool mayMutate = true)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["EXHYPERV_TEST_ARGUMENTS"] = JsonSerializer.Serialize(arguments);
        string command = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "$a=ConvertFrom-Json $env:EXHYPERV_TEST_ARGUMENTS; try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("系统核验进程启动失败。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            if (mayMutate && RecoveryDirectory.Value is { } directory)
                File.WriteAllText(Path.Combine(directory, "recovery-hold.txt"), "系统命令超时；服务中的原生作业可能仍在继续，需核对终态后恢复。");
            throw new TimeoutException("系统核验进程超时，已结束该子进程。");
        }
        var text = await output; var errors = await error;
        if (process.ExitCode != 0) throw new IOException($"系统核验失败（{process.ExitCode}）：{errors.Trim()}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }
}
