using System.Text.Json;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmTestMemory
{
    internal const long TemplateLimitBytes = 4L * 1024 * 1024 * 1024;
    internal const long HostReserveBytes = 2L * 1024 * 1024 * 1024;

    internal static long TemplateStartupBytes(long sourceStartupBytes)
    {
        if (sourceStartupBytes <= 0) throw new InvalidDataException("模板启动内存无效。");
        return Math.Min(sourceStartupBytes, TemplateLimitBytes);
    }

    internal static async Task PrepareStartAsync(ApiCaseContext context, TemporaryVm vm, bool start,
        Func<string, object?, int, bool, Task<JsonElement>>? command = null)
    {
        context.Token.ThrowIfCancellationRequested();
        var run = command ?? WindowsTestCommand.RunAsync;
        var result = await run(StartScript, new { id = vm.Id, name = vm.Name, root = vm.Root, start, reserve = HostReserveBytes }, 90, start);
        long startup = result.GetProperty("startup").GetInt64(), available = result.GetProperty("available").GetInt64();
        string budget = $"副本启动内存 {startup / 1073741824d:F1} GiB，宿主预留 {HostReserveBytes / 1073741824d:F1} GiB，当前可用 {available / 1073741824d:F1} GiB";
        context.Evidence.Add(budget);
        if (!result.GetProperty("sufficient").GetBoolean())
            throw new ApiCheckSkippedException("宿主内存余量不足，跳过启动：" + budget);
        if (result.GetProperty("allocationFailed").GetBoolean())
            throw new ApiCheckSkippedException("Hyper-V 分配内存失败，已核实虚拟机保持关闭或保存状态：" + result.GetProperty("error").GetString());
    }

    // Check the current settings and capacity just before starting an owned fixture.
    // The template is never resized and failed starts are never retried here.
    internal const string StartScript = """
        $v=Get-VM -Id ([guid]$a.id)
        $root=[IO.Path]::GetFullPath($a.root).TrimEnd('\')+'\'
        $location=[IO.Path]::GetFullPath($v.ConfigurationLocation).TrimEnd('\')+'\'
        if($v.Id -ne [guid]$a.id -or $v.Name -ne $a.name -or !$location.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw '测试虚拟机身份或目录已变化'}
        $state=$v.State.ToString()
        if($state -notin @('Off','Saved','Running','Paused')){throw ('测试虚拟机状态待确认：'+$state)}
        if($a.start -and $state -eq 'Paused'){throw '测试虚拟机处于暂停状态'}
        [long]$startup=$v.MemoryStartup
        $os=Get-CimInstance -ClassName Win32_OperatingSystem
        if($startup -le 0 -or $null -eq $os.FreePhysicalMemory){throw '启动内存或宿主内存读取失败'}
        [long]$available=[long]$os.FreePhysicalMemory*1KB
        $needsMemory=$state -in @('Off','Saved')
        $sufficient=(!$needsMemory -or $available -ge ($startup+[long]$a.reserve))
        $allocationFailed=$false;$errorText=$null
        if($sufficient -and $a.start -and $needsMemory){
            try{$v|Start-VM -ErrorAction Stop}
            catch{
                $failure=$_;$outOfMemory=$false
                for($errorItem=$failure.Exception;$null -ne $errorItem;$errorItem=$errorItem.InnerException){
                    if($errorItem.HResult -in @(-2147024882,-2147024888)){$outOfMemory=$true}
                }
                $after=Get-VM -Id ([guid]$a.id)
                if(!$outOfMemory -or $after.State.ToString() -notin @('Off','Saved')){throw}
                $allocationFailed=$true;$errorText=$failure.Exception.Message
            }
            if(!$allocationFailed -and (Get-VM -Id ([guid]$a.id)).State.ToString() -ne 'Running'){throw '测试虚拟机启动终态尚待确认'}
        }
        @{startup=$startup;available=$available;sufficient=$sufficient;allocationFailed=$allocationFailed;error=$errorText}|ConvertTo-Json -Compress
        """;
}
