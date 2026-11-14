using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class RebootContinuation
{
    internal sealed record VmBefore(Guid Id, string State, string AutomaticStartAction);
    internal sealed record Batch(string Sid, string TaskName, string ReportsRoot, string RunDirectory, string Directory,
        string[] CheckIds, int Index, string CoreHash, ApiTestReport Report, InspectionReport Resources,
        VmBefore[]? Machines = null, bool MachinesStopped = false, int Restarts = 0, bool Completed = false, bool Started = false,
        string? WaitingForBoot = null, int InspectionPort = 5187, bool AutomaticReboot = false);

    // Only newly submitted batches opt in. Older durable records retain their original manual policy.
    internal static Batch CreateBatch(string reports, InspectionReport resources, ApiTestReport report,
        string[] checkIds, string sid, string coreHash, int inspectionPort)
    {
        string run = Path.GetFullPath(Path.Combine(reports, resources.RunId, "api-tests", report.RunId));
        return new(sid, "ExHyperV.Core.Tests.Resume." + report.RunId, Path.GetFullPath(reports), run,
            Path.Combine(run, "reboot-continuation"), checkIds, 0, coreHash,
            report with { Phase = "准备宿主重启测试与续跑任务" }, resources,
            InspectionPort: inspectionPort, AutomaticReboot: true);
    }

    internal static Batch PrepareRestart(Batch batch, string boot, bool restoring)
    {
        if (batch.Restarts >= 40) throw new IOException("续跑重启次数超出本轮上限，保留现场。");
        int number = batch.Restarts + 1;
        string purpose = restoring ? "核验原设置已恢复" : "核验测试设置";
        string phase = batch.AutomaticReboot
            ? $"第 {number} 次宿主重启等待系统执行（{purpose}）。开机后自动继续本轮测试。"
            : $"第 {number} 次宿主重启等待你确认（{purpose}）。确认后请手动重启，开机后继续本轮测试。";
        return batch with { Restarts = number, WaitingForBoot = boot, Report = batch.Report with { Phase = phase } };
    }

    internal static async Task SubmitRestartAsync(Batch batch, Func<Task> saveProgress, Func<Task>? requestRestart = null)
    {
        await saveProgress();
        if (batch.AutomaticReboot) await (requestRestart ?? (() => HostRestart.RequestAsync(batch.TaskName)))();
    }
    internal static InspectionReport? LatestResources(string reports)
    {
        var batch = LatestBatch(reports);
        return batch is { Completed: false, Report.Running: true } ? batch.Resources : null;
    }
    internal static ApiTestReport? LatestReport(string reports) => LatestBatch(reports)?.Report;
    private static Batch? LatestBatch(string reports)
    {
        string marker = Path.Combine(reports, "reboot-active.json"); if (!File.Exists(marker)) return null;
        try
        {
            string path = DurableJson.Read<string>(marker);
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(reports).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            return Read(path);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
    internal static bool Cancel(string reports, string runId)
    {
        var batch = LatestBatch(reports); if (batch?.Report.RunId != runId || !batch.Report.Running) return false;
        File.WriteAllText(Path.Combine(batch.Directory, "cancel.request"), DateTimeOffset.UtcNow.ToString("O")); return true;
    }
    internal static async Task<ApiTestReport> QueueAsync(string reports, InspectionReport resources, ApiTestReport report,
        string? configuration, CatalogTest[] cases, Action<ApiTestReport> update, int inspectionPort)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            throw new InvalidOperationException("当前 Actions 作业缺少跨宿主重启的外部接管端；请从本机测试页面执行重启项。");
        string run = Path.Combine(reports, resources.RunId, "api-tests", report.RunId), directory = Path.Combine(run, "reboot-continuation"), bin = Path.Combine(directory, "bin");
        if (Path.GetFullPath(bin).StartsWith(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("续跑报告目录应位于程序发布目录之外。");
        Directory.CreateDirectory(bin);
        foreach (string source in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(bin, Path.GetRelativePath(AppContext.BaseDirectory, source)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source, dest, true);
        }
        if (configuration is not null && File.Exists(configuration)) File.Copy(configuration, Path.Combine(directory, "resource-settings.json"), true);
        var batch = CreateBatch(reports, resources, report, cases.Select(v => v.Id).ToArray(),
            WindowsIdentity.GetCurrent().User!.Value, Hash(Path.Combine(bin, "ExHyperV.Core.dll")), inspectionPort);
        string state = Path.Combine(directory, "state.json"); Save(state, batch); DurableJson.Write(Path.Combine(reports, "reboot-active.json"), Path.GetFullPath(state));
        try
        {
            await WindowsTestCommand.RunAsync("""
                $action=New-ScheduledTaskAction -Execute $a.exe -Argument ('--resume-reboot-tests "'+$a.state+'"') -WorkingDirectory $a.bin
                $trigger=New-ScheduledTaskTrigger -AtStartup
                $principal=New-ScheduledTaskPrincipal -UserId $a.sid -LogonType S4U -RunLevel Highest
                $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)
                Register-ScheduledTask -TaskName $a.name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force|Out-Null
                Start-ScheduledTask -TaskName $a.name
                @{registered=$true}|ConvertTo-Json
                """, new { exe = Path.Combine(bin, "ExHyperV.Core.Tests.exe"), state = Path.GetFullPath(state), bin = Path.GetFullPath(bin), sid = batch.Sid, name = batch.TaskName });
        }
        catch (Exception e)
        {
            batch = Read(state);
            if (batch.Started) throw new IOException("续跑任务已经接管，结果将由持久任务继续保存：" + state, e);
            // Mark the durable batch complete before removing the task, so a delayed trigger exits immediately.
            batch = batch with { Completed = true, Report = report with { Running = false, Finished = DateTimeOffset.Now, Phase = "重启续跑条件待补齐", Items = report.Items.Select(i => batch.CheckIds.Contains(i.Id) ? i with { Status = ApiTestStatus.Skipped, Detail = "当前账户的最高权限开机续跑任务注册失败；宿主配置保持原样。" + e.Message } : i).ToArray() } }; Save(state, batch); return batch.Report;
        }
        // The scheduled task owns the durable result. After reboot this waiting process may disappear;
        // a new inspection host reads the same marker and result, without resubmitting the tests.
        var startDeadline = DateTime.UtcNow.AddMinutes(2);
        var nextLivenessCheck = startDeadline;
        while (true)
        {
            batch = Read(state); update(batch.Report);
            if (batch.Completed) return batch.Report;
            if (!batch.Started && DateTime.UtcNow > startDeadline)
            {
                batch = Finish(batch, "最高权限续跑任务在两分钟内未接单，宿主设置保持原样。", ApiTestStatus.Skipped);
                Save(state, batch); await RemoveTaskAsync(batch); return batch.Report;
            }
            if (batch.Started && batch.WaitingForBoot is null && DateTime.UtcNow >= nextLivenessCheck)
            {
                nextLivenessCheck = DateTime.UtcNow.AddSeconds(30);
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(state) > TimeSpan.FromMinutes(2))
                {
                    var taskState = await WindowsTestCommand.RunAsync("$t=Get-ScheduledTask -TaskName $a.name -ErrorAction SilentlyContinue;@{running=($t -and $t.State -eq 'Running')}|ConvertTo-Json", new { name = batch.TaskName }, mayMutate: false);
                    batch = Read(state);
                    if (!batch.Completed && batch.WaitingForBoot is null && !taskState.GetProperty("running").GetBoolean()
                        && DateTime.UtcNow - File.GetLastWriteTimeUtc(state) > TimeSpan.FromMinutes(2))
                    {
                        const string message = "重启续跑进程已退出，保存的阶段仍未完成；请核对原配置与恢复材料。";
                        File.WriteAllText(Path.Combine(batch.Directory, "recovery-hold.txt"), message);
                        batch = Finish(batch, message, ApiTestStatus.Skipped);
                        batch = batch with { Report = batch.Report with { Error = message, Items = batch.Report.Items.Select(v => v.Status == ApiTestStatus.Running ? v with { Status = ApiTestStatus.Failed, Detail = message } : v).ToArray() } };
                        Save(state, batch); return batch.Report;
                    }
                }
            }
            await Task.Delay(1000);
        }
    }
    internal static async Task<int> RunAsync(string statePath)
    {
        var batch = Read(statePath);
        if (batch.Completed) return 0;
        if (batch.WaitingForBoot is not null && await RebootApiCases.BootStampAsync() == batch.WaitingForBoot) return 0;
        FileStream? lease = null;
        try
        {
            if (WindowsIdentity.GetCurrent().User!.Value != batch.Sid) throw new IOException("重启续跑账户与最初执行账户不同。");
            if (Hash(Path.Combine(AppContext.BaseDirectory, "ExHyperV.Core.dll")) != batch.CoreHash) throw new IOException("续跑使用的 Core DLL 与本轮固定版本不同。");
            lease = await TestExecutionLease.AcquireAsync();
            batch = Read(statePath); if (batch.Completed) return 0;
            if (batch.WaitingForBoot is not null && await RebootApiCases.BootStampAsync() == batch.WaitingForBoot) return 0;
            batch = batch with { Started = true, WaitingForBoot = null }; Save(statePath, batch);
            await EnsureWebHostAsync(batch);
            if (batch.Machines is null)
            {
                var pending = ExHyperV.Services.HostProductTypeService.GetPendingSwitch();
                var configured = ExHyperV.Services.HyperVSchedulerService.GetConfiguredSchedulerType();
                var running = ExHyperV.Services.HyperVSchedulerService.GetSchedulerType();
                if (!pending.Success || pending.HasData || configured != running || configured == ExHyperV.Services.HyperVSchedulerType.Unknown)
                {
                    batch = Finish(batch, "宿主已有待生效的系统类型／调度器配置，或当前模式无法核验；请先处理原有变更。", ApiTestStatus.Skipped);
                    Save(statePath, batch); await RemoveTaskAsync(batch); return 0;
                }
                var vms = await WindowsTestCommand.RunAsync("@{items=@(Get-VM|Select-Object @{n='id';e={$_.Id.ToString()}},@{n='state';e={$_.State.ToString()}},@{n='automaticStartAction';e={$_.AutomaticStartAction.ToString()}})}|ConvertTo-Json -Depth 4");
                batch = batch with { Machines = vms.GetProperty("items").EnumerateArray().Select(v => new VmBefore(Guid.Parse(v.GetProperty("id").GetString()!), v.GetProperty("state").GetString()!, v.GetProperty("automaticStartAction").GetString()!)).ToArray() }; Save(statePath, batch);
            }
            if (!batch.MachinesStopped)
            {
                try { await StopMachinesAsync(batch.Machines); batch = batch with { MachinesStopped = true }; Save(statePath, batch); }
                catch (Exception e)
                {
                    await RestoreMachinesAsync(batch.Machines!); batch = Finish(batch, "正常关闭虚拟机失败，本轮重启项跳过：" + e.Message, ApiTestStatus.Skipped); Save(statePath, batch); await RemoveTaskAsync(batch); return 0;
                }
            }
            while (batch.Index < batch.CheckIds.Length)
            {
                var id = batch.CheckIds[batch.Index]; var plan = TestPlanner.Catalog.Single(v => v.Id == id);
                string directory = Path.Combine(batch.Directory, "cases", id); Directory.CreateDirectory(directory);
                bool cancelled = File.Exists(Path.Combine(batch.Directory, "cancel.request"));
                if (cancelled && RebootApiCases.Read(directory) is null) break;
                DurableJson.Write(Path.Combine(directory, "reboot-owner.json"), new { runId = batch.Report.RunId, sid = batch.Sid });
                var context = new ApiCaseContext(directory, CancellationToken.None) { GuardNativeOutcomes = true };
                WindowsTestCommand.RecoveryDirectory.Value = directory;
                batch = WithItem(batch, id, ApiTestStatus.Running, "执行宿主配置、重启核验与恢复", []); Save(statePath, batch);
                try
                {
                    await RebootApiCases.RunAsync(plan, context);
                    var caseState = RebootApiCases.Read(directory)!;
                    batch = WithItem(batch, id, ApiTestStatus.Passed, "API 调用、重启核验及原配置恢复完成。", caseState.Evidence.ToArray());
                    batch = batch with { Report = batch.Report with { ApiCalls = batch.Report.ApiCalls + caseState.Calls } };
                }
                catch (RebootNeededException)
                {
                    if (batch.Restarts >= 40) throw new IOException("续跑重启次数超出本轮上限，保留现场。");
                    // Suppress automatic VM starts throughout the batch, and recheck before every restart.
                    var machines = batch.Machines ?? throw new InvalidDataException("缺少重启前的虚拟机清单。");
                    await StopMachinesAsync(machines, allowHypervisorStopped: true);
                    var pending = RebootApiCases.Read(directory) ?? throw new InvalidDataException("缺少宿主重启前保存的阶段。");
                    batch = PrepareRestart(batch, pending.Boot, pending.Phase == "AwaitRestoreBoot");
                    await SubmitRestartAsync(batch, async () =>
                    {
                        try { Save(statePath, batch); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            if (!await RebootApiCases.UndoDisableBeforeRestartAsync(directory, id)) throw;
                            await RestoreMachinesAsync(machines);
                            throw new IOException("重启前保存进度失败。Hyper-V 启动配置及虚拟机原状态已恢复。" + error.Message, error);
                        }
                    });
                    // Legacy batches wait here; new batches exit with the host and resume at startup.
                    return 0;
                }
                catch (Exception e)
                {
                    var state = RebootApiCases.Read(directory);
                    bool recovered = state?.Phase == "Complete" || e is ApiCheckSkippedException && state is null;
                    batch = WithItem(batch, id, e is ApiCheckSkippedException ? ApiTestStatus.Skipped : ApiTestStatus.Failed, e.Message, [.. context.Evidence, .. state?.Evidence ?? []]);
                    batch = batch with { Report = batch.Report with { ApiCalls = batch.Report.ApiCalls + (state?.Calls ?? context.Calls) } };
                    if (!recovered) throw new IOException("宿主恢复尚待完成，已停止后续步骤。恢复目录：" + directory, e);
                }
                finally { WindowsTestCommand.RecoveryDirectory.Value = null; }
                batch = batch with { Index = batch.Index + 1 }; Save(statePath, batch);
                if (cancelled) break;
            }
            await RestoreMachinesAsync(batch.Machines!);
            batch = Finish(batch, File.Exists(Path.Combine(batch.Directory, "cancel.request")) ? "已停止；宿主与虚拟机原状态已恢复" : "所选 API 测试及宿主恢复已完成", ApiTestStatus.Cancelled); Save(statePath, batch);
            await RemoveTaskAsync(batch); return batch.Report.Items.Any(v => v.Status == ApiTestStatus.Failed) ? 1 : 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(batch.Directory, "recovery-hold.txt"), e.ToString());
            batch = WithRecoveryHold(batch, e.Message, DateTimeOffset.Now);
            Save(statePath, batch); return 1;
        }
        finally { lease?.Dispose(); WindowsTestCommand.RecoveryDirectory.Value = null; }
    }
    private static async Task StopMachinesAsync(VmBefore[] machines, bool allowHypervisorStopped = false)
    {
        await WindowsTestCommand.RunAsync("""
            if($a.allowHypervisorStopped -and !(Get-CimInstance Win32_ComputerSystem).HypervisorPresent){
                if(@(Get-Process vmwp -ErrorAction SilentlyContinue).Count){throw '监控程序状态与虚拟机进程相异，需核对现场'}
                @{stopped=$true}|ConvertTo-Json;return
            }
            foreach($old in $a.items){$vm=Get-VM -Id ([guid]$old.id);$vm|Set-VM -AutomaticStartAction Nothing
                if($vm.State -eq 'Paused'){$vm|Resume-VM;$vm=Get-VM -Id $vm.Id}
                if($vm.State -eq 'Running'){
                    $component=Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ShutdownComponent|Where-Object {$_.SystemName -eq $vm.Id.ToString()}
                    if(@($component).Count -ne 1){throw ('虚拟机缺少正常关机通道：'+$vm.Name)}
                    $result=Invoke-CimMethod -InputObject $component -MethodName InitiateShutdown -Arguments @{Force=$false;Reason='ExHyperV API test restart'}
                    if($result.ReturnValue -notin @(0,4096)){throw ('正常关机被拒绝：'+$vm.Name+' / '+$result.ReturnValue)}
                    $end=[DateTime]::UtcNow.AddSeconds(90);while((Get-VM -Id $vm.Id).State -ne 'Off' -and [DateTime]::UtcNow -lt $end){Start-Sleep -Milliseconds 500}
                    if((Get-VM -Id $vm.Id).State -ne 'Off'){throw ('正常关机超时：'+$vm.Name)}
                }elseif($vm.State -notin @('Off','Saved')){throw ('虚拟机仍在转换状态：'+$vm.Name)}
            };@{stopped=$true}|ConvertTo-Json
            """, new { items = machines.Select(v => new { id = v.Id, state = v.State }).ToArray(), allowHypervisorStopped }, Math.Max(180, machines.Length * 100));
    }
    private static async Task RestoreMachinesAsync(VmBefore[] machines)
    {
        await WindowsTestCommand.RunAsync("""
            foreach($old in $a.items){$vm=Get-VM -Id ([guid]$old.id);$vm|Set-VM -AutomaticStartAction $old.start
                if($old.state -in @('Running','Paused')){if($vm.State -eq 'Off'){$vm|Start-VM};$vm=Get-VM -Id $vm.Id
                    if($old.state -eq 'Paused' -and $vm.State -eq 'Running'){$vm|Suspend-VM}
                    if($old.state -eq 'Running' -and $vm.State -eq 'Paused'){$vm|Resume-VM}}
                $actual=Get-VM -Id $vm.Id
                if($actual.State.ToString() -ne $old.state -or $actual.AutomaticStartAction.ToString() -ne $old.start){throw ('虚拟机原状态恢复未确认：'+$vm.Name)}
            };@{restored=$true}|ConvertTo-Json
            """, new { items = machines.Select(v => new { id = v.Id, state = v.State, start = v.AutomaticStartAction }).ToArray() }, 300);
    }
    private static Batch WithItem(Batch b, string id, ApiTestStatus status, string detail, string[] evidence) => b with { Report = b.Report with { Items = b.Report.Items.Select(v => v.Id == id ? v with { Status = status, Detail = detail, Evidence = evidence } : v).ToArray() } };
    private static Batch Finish(Batch b, string detail, ApiTestStatus waiting) => b with { Completed = true, Report = b.Report with { Running = false, Finished = DateTimeOffset.Now, Phase = detail, Items = b.Report.Items.Select(v => v.Status == ApiTestStatus.Waiting ? v with { Status = waiting, Detail = detail } : v).ToArray() } };
    private static Batch WithRecoveryHold(Batch batch, string reason, DateTimeOffset finished)
    {
        var items = batch.Report.Items.Select(item => item.Status switch
        {
            ApiTestStatus.Running => item with { Status = ApiTestStatus.Failed, Detail = reason },
            ApiTestStatus.Waiting => item with { Status = ApiTestStatus.Skipped, Detail = "前序恢复待处理" },
            _ => item
        }).ToArray();
        return batch with { Completed = true, Report = batch.Report with
            { Running = false, Finished = finished, Error = reason, Phase = "恢复记录待处理", Items = items } };
    }
    private static Batch Read(string path)
    {
        var batch = DurableJson.Read<Batch>(path);
        string hold = Path.Combine(Path.GetDirectoryName(path)!, "recovery-hold.txt");
        // The failure marker can reach disk even when replacing state.json is still denied.
        return !batch.Completed && File.Exists(hold)
            ? WithRecoveryHold(batch, File.ReadAllText(hold), File.GetLastWriteTimeUtc(hold))
            : batch;
    }
    private static void Save(string path, Batch b)
    {
        DurableJson.Write(path, b); DurableJson.Write(Path.Combine(b.RunDirectory, "results.json"), b.Report); DurableJson.Write(Path.Combine(b.RunDirectory, "resources.json"), b.Resources);
        File.WriteAllText(Path.Combine(b.RunDirectory, "results.html"), InspectionPage.Render(JsonSerializer.Serialize(b.Resources, InspectionJson.Options), testJson: JsonSerializer.Serialize(b.Report, InspectionJson.Options)));
    }
    private static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    private static Task RemoveTaskAsync(Batch b) => WindowsTestCommand.RunAsync("Unregister-ScheduledTask -TaskName $a.name -Confirm:$false;@{removed=$true}|ConvertTo-Json", new { name = b.TaskName });
    private static async Task EnsureWebHostAsync(Batch b)
    {
        // The original browser can reconnect after restart. No resource scan or test resubmission here.
        using var connection = new System.Net.Sockets.TcpClient();
        try { await connection.ConnectAsync("127.0.0.1", b.InspectionPort); return; } catch (System.Net.Sockets.SocketException) { }
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ExHyperV.Core.Tests.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--inspect-resources", "--port", b.InspectionPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reports", b.ReportsRoot, "--configuration", Path.Combine(b.Directory, "resource-settings.json"), "--no-browser" }) start.ArgumentList.Add(value);
        Process.Start(start)?.Dispose();
    }
}

internal static class TestExecutionLease
{
    internal static Task<FileStream> AcquireAsync()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExHyperV.Core.Tests"); Directory.CreateDirectory(directory);
        try { return Task.FromResult(new FileStream(Path.Combine(directory, "host-execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException e) { throw new IOException("本机已有测试任务持有宿主资源，请等待该任务完成。", e); }
    }
}
