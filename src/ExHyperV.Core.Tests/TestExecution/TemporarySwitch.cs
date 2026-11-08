using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed class TemporarySwitch : IAsyncDisposable
{
    private sealed record Journal(string Name, Guid? Id);
    private readonly string directory;
    private readonly ApiCaseContext context;
    internal string Name { get; }
    internal Guid Id { get; private set; }
    internal TemporarySwitch(ApiCaseContext context)
    {
        this.context = context;
        directory = Path.Combine(context.DirectoryPath, "switches", Guid.NewGuid().ToString("N"));
        Name = "ExHyperV-ApiSwitch-" + Guid.NewGuid().ToString("N");
    }

    internal static async Task<TemporarySwitch> CreateAsync(ApiCaseContext context, bool internalNetwork = false)
    {
        var value = new TemporarySwitch(context);
        Directory.CreateDirectory(value.directory); value.Save();
        var result = await WindowsTestCommand.RunAsync("$kind=if($a.internalNetwork){'Internal'}else{'Private'};$s=New-VMSwitch -Name $a.name -SwitchType $kind -MinimumBandwidthMode Absolute; @{id=$s.Id.ToString()}|ConvertTo-Json", new { name = value.Name, internalNetwork });
        value.Id = Guid.Parse(result.GetProperty("id").GetString()!); value.Save();
        context.Evidence.Add("已创建本项专属隔离交换机：" + value.Id); return value;
    }

    private void Save()
    {
        string path = Path.Combine(directory, "switch.json");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(new Journal(Name, Id == Guid.Empty ? null : Id), InspectionJson.Options)); File.Move(path + ".new", path, true);
    }

    internal static async Task CleanupAllAsync(string directory)
    {
        if (File.Exists(Path.Combine(directory, "recovery-hold.txt")))
            throw new IOException("原生操作终态待核实，保留交换机及关联资源：" + directory);
        var root = Path.Combine(directory, "switches");
        if (!Directory.Exists(root)) return;
        foreach (var child in Directory.GetDirectories(root))
            if (Guid.TryParseExact(Path.GetFileName(child), "N", out _)) await CleanupAsync(child);
    }

    private static async Task CleanupAsync(string directory)
    {
        string path = Path.Combine(directory, "switch.json"); if (!File.Exists(path)) return;
        var record = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), InspectionJson.Options) ?? throw new IOException("交换机归属记录缺失。");
        if (!record.Name.StartsWith("ExHyperV-ApiSwitch-", StringComparison.Ordinal) || !Guid.TryParseExact(record.Name["ExHyperV-ApiSwitch-".Length..], "N", out _)) throw new IOException("交换机归属记录无效。");
        await WindowsTestCommand.RunAsync("""
            $matches=@(Get-VMSwitch|Where-Object {if($a.id){$_.Id.ToString() -eq $a.id}else{$_.Name -eq $a.name}})
            foreach($switch in $matches){
                if($switch.Name -ne $a.name){throw '测试交换机名称已变化，保留现场'}
                if($switch.SwitchType -notin @('Private','Internal')){throw '测试交换机类型已变化，保留现场'}
                if(@(Get-VMNetworkAdapter -All|Where-Object {$_.SwitchId -eq $switch.Id -and !$_.IsManagementOs}).Count){throw '交换机仍有连接，保留现场'}
                Remove-VMSwitch -VMSwitch $switch -Force -Confirm:$false
            }
            if(@(Get-VMSwitch|Where-Object {if($a.id){$_.Id.ToString() -eq $a.id}else{$_.Name -eq $a.name}}).Count){throw '交换机清理后仍存在'}
            @{cleaned=$true}|ConvertTo-Json
            """, new { id = record.Id?.ToString("D"), name = record.Name }, timeoutSeconds: 120);
        File.Delete(path);
    }

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(Path.Combine(context.DirectoryPath, "recovery-hold.txt")))
        {
            context.Evidence.Add("原生操作终态待核实，交换机随虚拟机保留，恢复记录已保存。");
            return;
        }
        await CleanupAsync(directory);
    }
}
