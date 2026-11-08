using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed record VmQueryMessage(string Stage, VmQueryCapture? Capture = null, string? Error = null);

internal static class VmQueryWorker
{
    private static readonly JsonSerializerOptions WireJson = new(InspectionJson.Options) { WriteIndented = false };

    internal static async Task<int> RunAsync()
    {
        var output = Console.Out;
        Console.SetOut(TextWriter.Null); // Reserve stdout for progress messages.
        void Send(VmQueryMessage message) { output.WriteLine(JsonSerializer.Serialize(message, WireJson)); output.Flush(); }
        try
        {
            Send(new("before"));
            var before = ReadBaseline();
            Send(new("api"));
            await using var service = new VmQueryService();
            var timer = Stopwatch.StartNew();
            var result = await service.GetVmListAsync();
            timer.Stop();
            var (actual, issues) = ReadResponse(result);
            Send(new("after"));
            var after = ReadBaseline();
            Send(new("captured", new(before, actual, after, timer.Elapsed.TotalMilliseconds, issues)));
            return 0;
        }
        catch (Exception error) { Send(new("error", Error: error.Message)); return 1; }
        finally { Console.SetOut(output); }
    }

    internal static (VmListRow[] Rows, VmQueryIssue[] Issues) ReadResponse(ApiResponse<VmInventorySnapshot> response)
    {
        if (!response.Success)
            throw new InvalidOperationException($"Core 查询失败 [{response.ErrorKind} / {response.ErrorSource}:{response.Code}]：{response.Error}");
        if (!response.HasData)
            throw new InvalidDataException("Core 查询成功，但缺少清单快照。");
        return (response.Data.VirtualMachines.Select(vm => new VmListRow(vm.Id, vm.Name, vm.StateCode)).ToArray(), response.Data.Issues.ToArray());
    }

    private static VmListRow[] ReadBaseline()
    {
        var scope = new ManagementScope(@"\\.\root\virtualization\v2");
        var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(10) };
        using (var serviceQuery = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Msvm_VirtualSystemManagementService"), options))
        using (var services = serviceQuery.Get())
        {
            var found = false;
            foreach (ManagementObject service in services) { using (service) found = true; }
            if (!found) throw new UnauthorizedAccessException("Hyper-V 管理接口读取权限待检查。");
        }
        // Core reads SummaryInformation. Use ComputerSystem as an independent baseline,
        // and identify VMs by GUID instead of a localized Caption.
        using var query = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem"), options);
        using var objects = query.Get();
        var rows = new List<VmListRow>();
        foreach (ManagementObject item in objects)
        {
            using (item)
                if (Guid.TryParse(item["Name"]?.ToString(), out var id))
                    rows.Add(new(id, item["ElementName"]?.ToString() ?? "", Convert.ToUInt16(item["EnabledState"])));
        }
        return rows.ToArray();
    }

    internal static async Task<VmQueryCapture> CaptureAsync(Action<string> progress, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("测试程序路径缺失。");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--vm-query-test-worker");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("查询进程启动失败。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stderr = process.StandardError.ReadToEndAsync();
        VmQueryCapture? capture = null;
        string? failure = null;
        async Task ReadMessages()
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var message = JsonSerializer.Deserialize<VmQueryMessage>(line, WireJson)
                    ?? throw new InvalidDataException("查询进度消息为空。");
                if (message.Capture is not null) capture = message.Capture;
                if (message.Error is not null) failure = message.Error;
                progress(message.Stage);
            }
        }
        var reading = ReadMessages();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), reading);
            var error = await stderr;
            if (failure is not null || process.ExitCode != 0)
                throw new InvalidOperationException(failure ?? $"查询进程退出码 {process.ExitCode}：{error.Trim()}");
            return capture ?? throw new InvalidDataException("查询进程缺少结果快照。");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await reading; } catch { }
            await stderr;
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("查询超过 60 秒，工作进程已结束。");
            throw;
        }
    }
}
