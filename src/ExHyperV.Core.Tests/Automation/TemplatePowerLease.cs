using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;

namespace ExHyperV.Core.Tests.Automation;

internal static class TemplatePowerLease
{
    private sealed record State(Guid Id, string OriginalState, string Phase, string Name);
    private static string PathFor(string root) => Path.Combine(root, "template-power.json");
    internal static bool HasPendingChange(string root) => File.Exists(PathFor(root))
        && DurableJson.Read<State>(PathFor(root)) is { OriginalState: not "Off", Phase: not "restored" };

    internal static async Task PrepareAsync(string root, InspectionConfiguration configuration, bool enabled)
    {
        if (!enabled || !Guid.TryParse(configuration.TemplateVmId, out var id)) return;
        using var hostLease = await TestExecutionLease.AcquireAsync();
        if (File.Exists(PathFor(root)))
        {
            if (DurableJson.Read<State>(PathFor(root)).Phase == "prepared") return;
            throw new IOException("模板电源步骤曾中断，先核对本轮 template-power.json，保留原状态记录。");
        }
        var observed = await WindowsTestCommand.RunAsync("$v=Get-VM -Id ([guid]$a.id); @{name=$v.Name;state=$v.State.ToString()}|ConvertTo-Json", new { id }, mayMutate: false);
        var state = new State(id, observed.GetProperty("state").GetString()!, "observed", observed.GetProperty("name").GetString()!);
        if (state.OriginalState is not ("Off" or "Running")) return; // Resource conditions describe this state.
        DurableJson.Write(PathFor(root), state);
        if (state.OriginalState == "Running")
        {
            DurableJson.Write(PathFor(root), state with { Phase = "shutdown-requested" });
            await WindowsTestCommand.RunAsync("""
                $v=Get-VM -Id ([guid]$a.id)
                if($v.State -ne 'Running' -or $v.Name -ne $a.name){throw '模板身份或状态已变化'}
                $component=@(Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ShutdownComponent|Where-Object SystemName -eq $v.Id.ToString())
                if($component.Count -ne 1){throw '模板正常关机接口尚未就绪'}
                $response=Invoke-CimMethod -InputObject $component[0] -MethodName InitiateShutdown -Arguments @{Force=$false;Reason='ExHyperV test template copy'}
                if($response.ReturnValue -ne 0){throw ('正常关机请求失败：'+$response.ReturnValue)}
                $deadline=(Get-Date).AddSeconds(120)
                do{if((Get-VM -Id $v.Id).State -eq 'Off'){break};Start-Sleep -Milliseconds 500}while((Get-Date) -lt $deadline)
                if((Get-VM -Id $v.Id).State -ne 'Off'){throw '模板尚未正常关机，保留记录等待核对'}
                @{off=$true}|ConvertTo-Json
                """, new { id, name = state.Name }, 150);
        }
        DurableJson.Write(PathFor(root), state with { Phase = "prepared" });
    }

    internal static async Task RestoreAsync(string root)
    {
        if (!File.Exists(PathFor(root))) return;
        using var hostLease = await TestExecutionLease.AcquireAsync();
        var state = DurableJson.Read<State>(PathFor(root));
        if (state.Phase == "restored") return;
        if (state.Phase != "prepared") throw new IOException("模板恢复前须核对上次电源步骤终态。");
        if (state.OriginalState == "Running")
        {
            DurableJson.Write(PathFor(root), state with { Phase = "restore-requested" });
            await WindowsTestCommand.RunAsync("""
                $v=Get-VM -Id ([guid]$a.id)
                if($v.Name -ne $a.name){throw '模板身份已变化'}
                if($v.State -eq 'Off'){$v|Start-VM}
                elseif($v.State -ne 'Running'){throw '模板当前状态需要人工核对'}
                if((Get-VM -Id $v.Id).State -ne 'Running'){throw '模板恢复运行尚未完成'}
                @{running=$true}|ConvertTo-Json
                """, new { id = state.Id, name = state.Name }, 90);
        }
        DurableJson.Write(PathFor(root), state with { Phase = "restored" });
    }
}
