using System.Text.Json;
using ExHyperV.Services;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class SwitchApiCases
{
    private sealed record Journal(string Name, Guid? Id, Guid? Upstream, bool Dhcp, string[] Dns, IcsConnection[] Sharing);
    internal static bool Supports(CatalogTest p) => p.Api is "HyperVSwitchService.CreateSwitchAsync()" or "HyperVSwitchService.DeleteSwitchAsync()" or "HyperVSwitchService.UpdateSwitchConfigurationAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        var sharing = await ComApi.GetConnectionsAsync(); c.Require(sharing.HasData, sharing.Error);
        if (sharing.Data!.Any(v => v.Enabled)) throw new ApiCheckSkippedException("宿主已有 ICS 共享配置；交换机写入测试等待共享资源空闲。");
        bool usesUpstream = p.Api.Contains("Update") || p.Api.Contains("Delete") || p.Id is "T-8e882037c098524e-2" or "T-8e882037c098524e-3";
        SwitchUpstreamAdapter? upstream = usesUpstream ? await SafeUpstreamAsync() : null;
        var adapterState = upstream is null ? default : await WindowsTestCommand.RunAsync("$n=Get-CimInstance Win32_NetworkAdapterConfiguration|Where-Object {$_.SettingID -eq $a.id};@{dhcp=[bool]$n.DHCPEnabled;dns=@($n.DNSServerSearchOrder)}|ConvertTo-Json", new { id = upstream.ConnectionId });
        string name = "ExHyperV-ApiSwitch-" + Guid.NewGuid().ToString("N"); string path = Path.Combine(c.DirectoryPath, "switch-case.json");
        var journal = new Journal(name, null, upstream?.ConnectionId, upstream is not null && adapterState.GetProperty("dhcp").GetBoolean(), upstream is null ? [] : adapterState.GetProperty("dns").EnumerateArray().Select(v => v.GetString()).OfType<string>().ToArray(), sharing.Data!.ToArray());
        Save(path, journal);
        var original = SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch").Select(v => (Id: Convert.ToString(v["Name"]), Name: Convert.ToString(v["ElementName"]))).Order().ToArray();
        try
        {
            SwitchMode mode = p.Id == "T-8e882037c098524e-2" ? SwitchMode.Bridge : p.Id == "T-8e882037c098524e-3" || p.Api.Contains("Delete") ? SwitchMode.NAT : SwitchMode.Isolated;
            await c.CallAsync(async () => { await HyperVSwitchService.CreateSwitchAsync(name, mode, upstream); return true; });
            var native = await WindowsTestCommand.RunAsync("$s=Get-VMSwitch -Name $a.name;@{id=$s.Id.ToString();type=$s.SwitchType.ToString();management=$s.AllowManagementOS;interface=$s.NetAdapterInterfaceDescription}|ConvertTo-Json", new { name });
            journal = journal with { Id = Guid.Parse(native.GetProperty("id").GetString()!) }; Save(path, journal);
            c.Equal(mode == SwitchMode.Bridge ? "External" : "Internal", native.GetProperty("type").GetString(), "系统交换机类型");
            if (mode == SwitchMode.Bridge) c.Equal(upstream!.Description, native.GetProperty("interface").GetString(), "独立系统上游网卡说明");
            await VerifyModeAsync(c, journal.Id.Value, mode, upstream);
            if (p.Id == "T-8e882037c098524e-4")
            {
                Exception? error = null; try { await c.CallAsync(async () => { await HyperVSwitchService.CreateSwitchAsync(name, SwitchMode.Isolated, null); return true; }); } catch (Exception e) { error = e; }
                c.Require(error is not null, "重复名称产生明确失败"); c.Equal(1, SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch").Count(v => Equals(v["ElementName"], name)), "名称冲突保留唯一原交换机");
            }
            else if (p.Api.Contains("Update"))
            {
                if (p.Id.EndsWith("-2", StringComparison.Ordinal))
                {
                    int faults = 0; using var fault = ComApi.UseSharingFault((_, _) => { faults++; return ApiResponse.Fail("测试注入的 ICS 写入失败", 5, ApiErrorSource.Com, kind: ApiErrorKind.AccessDenied); });
                    Exception? error = null; try { await c.CallAsync(async () => { await HyperVSwitchService.UpdateSwitchConfigurationAsync(name, SwitchMode.NAT, upstream, true); return true; }); } catch (Exception e) { error = e; }
                    c.Require(faults > 0 && error is not null, "已进入 ICS 共享配置阶段并收到受控失败");
                    await VerifyModeAsync(c, journal.Id.Value, SwitchMode.Isolated, null);
                    c.Sequence(journal.Sharing.Where(v => v.Enabled).Select(v => (v.Id, v.Type)).Order(), (await ComApi.GetConnectionsAsync()).Data!.Where(v => v.Enabled).Select(v => (v.Id, v.Type)).Order(), "失败恢复原 ICS 共享关系");
                }
                else foreach (var next in new[] { SwitchMode.NAT, SwitchMode.Isolated, SwitchMode.Bridge, SwitchMode.Isolated })
                { await c.CallAsync(async () => { await HyperVSwitchService.UpdateSwitchConfigurationAsync(name, next, upstream, true); return true; }); await VerifyModeAsync(c, journal.Id.Value, next, upstream); }
            }
            if (p.Api.Contains("Delete"))
            {
                await c.CallAsync(async () => { await HyperVSwitchService.DeleteSwitchAsync(name); return true; });
                c.Require(SystemBaseline.Query("SELECT Name FROM Msvm_VirtualEthernetSwitch").All(v => Guid.Parse(Convert.ToString(v["Name"])!) != journal.Id), "目标交换机已删除");
            }
            c.Sequence(original, SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch").Where(v => !Equals(v["ElementName"], name)).Select(v => (Convert.ToString(v["Name"]), Convert.ToString(v["ElementName"]))).Order(), "已有交换机身份和名称保持");
        }
        finally
        {
            try { await RestoreAsync(c.DirectoryPath); }
            catch (Exception error) { File.WriteAllText(Path.Combine(c.DirectoryPath, "recovery-hold.txt"), "交换机恢复失败：" + error.Message); throw; }
        }
    }
    internal static async Task<SwitchUpstreamAdapter> SafeUpstreamAsync()
    {
        var available = await HyperVSwitchService.GetBridgeableAdaptersAsync();
        foreach (var adapter in available.Where(v => !v.LinkUp).OrderBy(v => v.ConnectionId))
        {
            var check = await WindowsTestCommand.RunAsync("""
                $nic=Get-NetAdapter|Where-Object {$_.InterfaceGuid -eq $a.id}
                if(@($nic).Count -ne 1){@{safe=$false}|ConvertTo-Json;return}
                $ips=@(Get-NetIPAddress -InterfaceIndex $nic.ifIndex -ErrorAction SilentlyContinue|Where-Object {$_.IPAddress -notlike 'fe80:*' -and $_.IPAddress -notlike '169.254.*'})
                $routes=@(Get-NetRoute -InterfaceIndex $nic.ifIndex -ErrorAction SilentlyContinue|Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0' -or $_.DestinationPrefix -eq '::/0'})
                $switches=@(Get-VMSwitch|Where-Object {$_.NetAdapterInterfaceDescription -eq $nic.InterfaceDescription})
                @{safe=($nic.Status -ne 'Up' -and !$ips.Count -and !$routes.Count -and !$switches.Count)}|ConvertTo-Json
                """, new { id = adapter.ConnectionId });
            if (check.GetProperty("safe").GetBoolean()) return adapter;
        }
        throw new ApiCheckSkippedException("缺少闲置物理上游网卡：测试要求链路断开、无业务 IP、无默认路由且尚未绑定交换机；现用网络保持原样。");
    }
    private static async Task VerifyModeAsync(ApiCaseContext c, Guid id, SwitchMode mode, SwitchUpstreamAdapter? upstream)
    {
        var read = await HyperVSwitchService.GetNetworkInfoAsync(); var current = read.Switches.Single(v => Guid.Parse(v.Id) == id);
        c.Equal(mode, current.SwitchType, "实际网络模式"); c.Require(string.IsNullOrEmpty(current.StateError), "网络模式识别完整");
        if (mode != SwitchMode.Isolated) c.Equal(upstream!.ConnectionId, current.Upstream!.ConnectionId, "上游稳定身份");
        if (mode == SwitchMode.NAT) { var sharing = await ComApi.GetConnectionsAsync(); c.Require(sharing.HasData && sharing.Data!.Any(v => v.Id == upstream!.ConnectionId && v.Enabled && v.Type == 0) && sharing.Data!.Any(v => v.Enabled && v.Type == 1), "公共与私有 ICS 共享实际启用"); }
    }
    private static void Save(string path, Journal journal) { File.WriteAllText(path + ".new", JsonSerializer.Serialize(journal)); File.Move(path + ".new", path, true); }
    internal static async Task RestoreAsync(string directory)
    {
        string path = Path.Combine(directory, "switch-case.json"); if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))!;
        if (!journal.Name.StartsWith("ExHyperV-ApiSwitch-", StringComparison.Ordinal) || !Guid.TryParseExact(journal.Name["ExHyperV-ApiSwitch-".Length..], "N", out _)) throw new IOException("交换机测试归属记录无效。");
        var actual = SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch").Where(v => journal.Id is Guid id ? Guid.Parse(Convert.ToString(v["Name"])!) == id : Equals(v["ElementName"], journal.Name)).ToArray();
        foreach (var s in actual)
        {
            if (!Equals(s["ElementName"], journal.Name)) throw new IOException("专属交换机已被外部改名，保留现场。");
            await HyperVSwitchService.DeleteSwitchAsync(journal.Name);
        }
        await ComApi.RestoreConnectionsAsync(journal.Sharing);
        if (journal.Upstream is Guid upstream) await WindowsTestCommand.RunAsync("""
            $nic=Get-NetAdapter|Where-Object {$_.InterfaceGuid -eq $a.id};if(@($nic).Count -ne 1){throw '待恢复上游身份已变化'}
            $state=if($a.dhcp){'Enabled'}else{'Disabled'};Set-NetIPInterface -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -Dhcp $state
            if(@($a.dns).Count){Set-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ServerAddresses $a.dns}else{Set-DnsClientServerAddress -InterfaceIndex $nic.ifIndex -ResetServerAddresses}
            if((Get-NetIPInterface -InterfaceIndex $nic.ifIndex -AddressFamily IPv4).Dhcp.ToString() -ne $state){throw '上游 DHCP 恢复核验失败'}
            if(@($a.dns).Count){$actual=@(Get-DnsClientServerAddress -InterfaceIndex $nic.ifIndex|ForEach-Object ServerAddresses);if(@(Compare-Object @($a.dns) $actual).Count){throw '上游 DNS 恢复核验失败'}}
            @{ok=$true}|ConvertTo-Json
            """, new { id = upstream, dhcp = journal.Dhcp, dns = journal.Dns });
        if (SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch").Any(v => Equals(v["ElementName"], journal.Name))) throw new IOException("测试交换机清理后仍存在。");
        var restored = await ComApi.GetConnectionsAsync(); if (!restored.HasData || !journal.Sharing.Where(v => v.Enabled).Select(v => (v.Id, v.Type)).Order().SequenceEqual(restored.Data!.Where(v => v.Enabled).Select(v => (v.Id, v.Type)).Order())) throw new IOException("ICS 共享恢复核验失败。");
        File.Delete(path);
    }
}
