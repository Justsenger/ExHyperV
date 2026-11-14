using System.Text.Json;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostRestart
{
    internal static readonly TimeSpan ExitTimeout = TimeSpan.FromMinutes(2);

    internal static async Task RequestAsync(string taskName,
        Func<string, object?, int, bool, Task<JsonElement>>? command = null,
        Func<TimeSpan, Task>? delay = null)
    {
        Exception? requestError = null;
        string? recoveryDirectory = WindowsTestCommand.RecoveryDirectory.Value;
        try
        {
            // Windows may end the command process before it finishes writing JSON.
            // The durable WaitingForBoot phase owns this outcome; a broken pipe during
            // shutdown must not create a recovery hold that prevents the next boot.
            WindowsTestCommand.RecoveryDirectory.Value = null;
            var result = await (command ?? WindowsTestCommand.RunAsync)(RequestScript, new { task = taskName }, 45, true);
            if (!result.GetProperty("requested").GetBoolean()) requestError = new IOException("宿主自动重启请求未被接受。");
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException)
        { requestError = error; }
        finally { WindowsTestCommand.RecoveryDirectory.Value = recoveryDirectory; }

        // A successful restart terminates this process. A blocked/cancelled shutdown
        // must become a visible recovery hold instead of an endless wait or retry.
        await (delay ?? Task.Delay)(ExitTimeout);
        throw new IOException("宿主在自动重启请求结束后两分钟内仍未重启；保留恢复记录，后续步骤暂停。" +
            (requestError is null ? "" : " 原因：" + requestError.Message), requestError);
    }

    internal const string RequestScript = """
        $task=Get-ScheduledTask -TaskName $a.task -ErrorAction Stop
        $bootTriggers=@($task.Triggers|Where-Object {$_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' -and $_.Enabled -ne $false})
        if($task.State -eq 'Disabled' -or $task.Settings.Enabled -eq $false -or $bootTriggers.Count -eq 0){throw '开机续跑任务已停用或缺少启动触发器，保留现场'}
        $restart=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\shutdown.exe') -ArgumentList @('/r','/t','0','/d','p:4:1') -WindowStyle Hidden -Wait -PassThru
        if($restart.ExitCode -ne 0){throw ('宿主自动重启请求失败，退出码：'+$restart.ExitCode)}
        @{requested=$true}|ConvertTo-Json -Compress
        """;
}
