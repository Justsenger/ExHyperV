using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using System.Text.Json;

namespace ExHyperV.Core.Tests.Automation;

internal static class TemplatePowerLease
{
    private sealed record State(Guid Id, string OriginalState, string Phase, string Name);
    private static string PathFor(string root) => Path.Combine(root, "template-power.json");
    internal static bool HasPendingChange(string root) => File.Exists(PathFor(root))
        && DurableJson.Read<State>(PathFor(root)) is { OriginalState: not "Off", Phase: not "restored" };

    internal static async Task PrepareAsync(string root, InspectionConfiguration configuration, bool enabled, bool ownsHostLease = false,
        Func<string, object?, int, bool, Task<JsonElement>>? command = null)
    {
        if (!enabled || !Guid.TryParse(configuration.TemplateVmId, out var id)) return;
        using var hostLease = ownsHostLease ? null : await TestExecutionLease.AcquireAsync();
        var run = command ?? WindowsTestCommand.RunAsync;
        if (File.Exists(PathFor(root)))
        {
            var previous = DurableJson.Read<State>(PathFor(root));
            if (previous.Id != id) throw new IOException("模板身份与电源记录相异，保留原记录。");
            if (previous.Phase == "prepared") return;
            if (previous.Phase != "restored") throw new IOException("模板电源步骤曾中断，先核对本轮 template-power.json，保留原状态记录。");
        }
        var observed = await run("$v=Get-VM -Id ([guid]$a.id); @{name=$v.Name;state=$v.State.ToString()}|ConvertTo-Json", new { id }, 45, false);
        var state = new State(id, observed.GetProperty("state").GetString()!, "observed", observed.GetProperty("name").GetString()!);
        if (state.OriginalState is not ("Off" or "Running")) throw new ApiCheckSkippedException("模板处于暂停、保存或切换状态，暂时跳过完整副本测试。");
        if (state.OriginalState == "Running" && Environment.GetEnvironmentVariable("EXHYPERV_TEST_PREPARE_TEMPLATE") == "0")
            throw new ApiCheckSkippedException("本轮已关闭模板自动关机，完整副本测试暂时跳过。");
        DurableJson.Write(PathFor(root), state);
        if (state.OriginalState == "Running")
        {
            DurableJson.Write(PathFor(root), state with { Phase = "shutdown-requested" });
            var shutdown = await run("""
                $v=Get-VM -Id ([guid]$a.id)
                if($v.State -ne 'Running' -or $v.Name -ne $a.name){throw '模板身份或状态已变化'}
                $component=@(Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ShutdownComponent|Where-Object SystemName -eq $v.Id.ToString())
                if($component.Count -ne 1){@{off=$false;rejected=$true;unchanged=((Get-VM -Id $v.Id).State -eq 'Running');reason='模板正常关机接口尚未就绪'}|ConvertTo-Json;return}
                $response=Invoke-CimMethod -InputObject $component[0] -MethodName InitiateShutdown -Arguments @{Force=$false;Reason='ExHyperV test template copy'}
                if($response.ReturnValue -ne 0){@{off=$false;rejected=$true;unchanged=((Get-VM -Id $v.Id).State -eq 'Running');reason=('模板拒绝正常关机：'+$response.ReturnValue)}|ConvertTo-Json;return}
                $deadline=(Get-Date).AddSeconds(120)
                do{if((Get-VM -Id $v.Id).State -eq 'Off'){break};Start-Sleep -Milliseconds 500}while((Get-Date) -lt $deadline)
                if((Get-VM -Id $v.Id).State -ne 'Off'){throw '模板尚未正常关机，保留记录等待核对'}
                @{off=$true}|ConvertTo-Json
                """, new { id, name = state.Name }, 150, true);
            if (shutdown.TryGetProperty("rejected", out var rejected) && rejected.GetBoolean()
                && shutdown.GetProperty("unchanged").GetBoolean())
            {
                DurableJson.Write(PathFor(root), state with { Phase = "restored" });
                throw new ApiCheckSkippedException(shutdown.GetProperty("reason").GetString()!);
            }
            if (!shutdown.GetProperty("off").GetBoolean()) throw new IOException("模板关机终态待核对。");
        }
        DurableJson.Write(PathFor(root), state with { Phase = "prepared" });
    }

    internal static async Task RestoreAsync(string root, bool ownsHostLease = false,
        Func<string, object?, int, bool, Task<JsonElement>>? command = null)
    {
        if (!File.Exists(PathFor(root))) return;
        using var hostLease = ownsHostLease ? null : await TestExecutionLease.AcquireAsync();
        var run = command ?? WindowsTestCommand.RunAsync;
        var state = DurableJson.Read<State>(PathFor(root));
        if (state.Phase == "restored") return;
        if (state.Phase != "prepared") throw new IOException("模板恢复前须核对上次电源步骤终态。");
        if (state.OriginalState == "Running")
        {
            DurableJson.Write(PathFor(root), state with { Phase = "restore-requested" });
            await run("""
                $v=Get-VM -Id ([guid]$a.id)
                if($v.Name -ne $a.name){throw '模板身份已变化'}
                if($v.State -eq 'Off'){$v|Start-VM}
                elseif($v.State -ne 'Running'){throw '模板当前状态需要人工核对'}
                if((Get-VM -Id $v.Id).State -ne 'Running'){throw '模板恢复运行尚未完成'}
                @{running=$true}|ConvertTo-Json
                """, new { id = state.Id, name = state.Name }, 90, true);
        }
        DurableJson.Write(PathFor(root), state with { Phase = "restored" });
    }

    internal static async Task<T> CopyAsync<T>(string root, InspectionConfiguration configuration, Func<Task<T>> copy,
        Func<string, object?, int, bool, Task<JsonElement>>? command = null)
    {
        var hold = Path.Combine(root, "recovery-hold.txt");
        if (File.Exists(hold)) throw new IOException("模板复制存在待核实现场。");
        bool prepared = false;
        try
        {
            // The parent test runner already holds the cross-process host lease.
            await PrepareAsync(root, configuration, true, ownsHostLease: true, command);
            prepared = true;
            return await copy();
        }
        catch
        {
            if (!prepared && HasPendingChange(root)) File.WriteAllText(hold, "模板关机步骤终态待核对，保留电源记录。");
            throw;
        }
        finally
        {
            if (prepared && !File.Exists(hold))
            {
                try { await RestoreAsync(root, ownsHostLease: true, command); }
                catch { File.WriteAllText(hold, "模板恢复运行终态待核对，保留电源记录。"); throw; }
            }
        }
    }
}
