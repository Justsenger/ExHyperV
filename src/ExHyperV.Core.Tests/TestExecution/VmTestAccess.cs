using System.Text.Json;

namespace ExHyperV.Core.Tests.TestExecution;

// Credentials are exported locally with Export-Clixml by the execution account (Windows DPAPI).
// Only the chosen template's credential file is accepted; contents never enter reports or journals.
internal static class VmTestAccess
{
    internal static string CredentialPath(ApiCaseContext c)
    {
        if (!Guid.TryParse(c.Configuration.TemplateVmId, out var id)) throw new ApiCheckSkippedException("第一步尚未选择测试虚拟机模板。");
        return new ResourceInspection.VmCredentialStore().PathFor(id);
    }
    internal static void RequireCredentials(ApiCaseContext c)
    {
        string path = CredentialPath(c);
        if (!File.Exists(path)) throw new ApiCheckSkippedException("请在第一步填写虚拟机账号和密码，再保存登录信息。");
    }
    internal static async Task<JsonElement> WindowsAsync(ApiCaseContext c, TemporaryVm vm, string script, object? payload = null, int seconds = 120, bool mayMutate = true)
    {
        RequireCredentials(c);
        return await WindowsTestCommand.RunAsync("""
            $credential=Import-Clixml -LiteralPath $a.credential
            if($credential -isnot [PSCredential]){throw '本机凭据文件格式错误'}
            $block=[scriptblock]::Create($a.script)
            $result=Invoke-Command -VMId ([guid]$a.id) -Credential $credential -ScriptBlock $block -ArgumentList $a.payload -ErrorAction Stop
            $result|ConvertTo-Json -Depth 12 -Compress
            """, new { credential = CredentialPath(c), id = vm.Id, script, payload }, seconds, mayMutate);
    }
    internal static async Task ReadyWindowsAsync(ApiCaseContext c, TemporaryVm vm)
    {
        RequireCredentials(c);
        await vm.StartAsync();
        var deadline = DateTime.UtcNow.AddMinutes(2); Exception? last = null;
        do
        {
            try { var ready = await WindowsAsync(c, vm, "@{windows=($env:OS -eq 'Windows_NT');name=$env:COMPUTERNAME}", seconds: 20, mayMutate: false); if (ready.GetProperty("windows").GetBoolean()) { c.Evidence.Add("已通过 VM GUID 建立副本内 PowerShell Direct 验证通道。"); return; } }
            catch (Exception error) { last = error; }
            await Task.Delay(1000, c.Token);
        } while (DateTime.UtcNow < deadline);
        throw new ApiCheckSkippedException("模板副本的 PowerShell Direct 登录尚未就绪：" + last?.Message);
    }
}
