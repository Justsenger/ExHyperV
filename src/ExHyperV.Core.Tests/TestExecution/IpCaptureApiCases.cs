using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Runtime.InteropServices;
using ExHyperV.Services;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class IpCaptureApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmIpService.ResolveAdapterAddresses()" or "VmIpService.LookupAsync()" or "ArpSnoopService.TryGetIp()" or "ArpSnoopService.Start()" or "ArpSnoopService.Dispose()" or "SharedBackgroundServices.ShutdownAsync()";
    internal static bool Live(CatalogTest p) => p.Api is "ArpSnoopService.Start()" or "ArpSnoopService.Dispose()" or "SharedBackgroundServices.ShutdownAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        var service = ArpSnoopService.Instance;
        if (Live(p)) { await LiveAsync(p, c, service); return; }
        // Exercise public lookup contracts with a deterministic cache, in the disposable worker process.
        var state = typeof(ArpSnoopService).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = (ConcurrentDictionary<string, (string Ip, long Ticks, DateTimeOffset ObservedAtUtc)>)typeof(ArpSnoopService).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        const string mac = "02AABBCCDDEE"; const string ip = "192.0.2.37";
        state.SetValue(service, 2); map[mac] = (ip, Environment.TickCount64, DateTimeOffset.UtcNow);
        c.Evidence.Add("受控缓存样本，公开地址接口执行归一化、优先级与有效期判断。");
        try
        {
            if (p.Api == "ArpSnoopService.TryGetIp()")
            {
                foreach (string form in new[] { mac, "02:aa:bb:cc:dd:ee", "02-aa-bb-cc-dd-ee" }) { string actual = ""; c.Require(c.Call(() => service.TryGetIp(form, out actual)), "MAC 格式命中"); c.Equal(ip, actual, "缓存地址"); }
                map[mac] = (ip, Environment.TickCount64 - 300001, DateTimeOffset.UtcNow.AddMinutes(-6)); c.Require(!c.Call(() => service.TryGetIp(mac, out _)), "超过五分钟的映射失效");
                map[mac] = (ip, Environment.TickCount64 + 10000, DateTimeOffset.UtcNow); c.Require(!c.Call(() => service.TryGetIp(mac, out _)), "未来时钟样本失效");
                foreach (string? invalid in new[] { null, "", "invalid" }) c.Require(!c.Call(() => service.TryGetIp(invalid, out _)), "空或非法 MAC 返回缺测"); return;
            }
            if (p.Api == "VmIpService.LookupAsync()")
            {
                var id = Guid.NewGuid(); var result = await c.CallAsync(() => VmIpService.LookupAsync(id, "02:aa:bb:cc:dd:ee")); c.Require(result.HasData, result.Error);
                var observation = result.Data!.Single(); c.Equal(ip, observation.Address, "宿主嗅探优先"); c.Equal(VmIpAddressSource.ArpCapture, observation.Source, "观测来源"); c.Equal(VmIpAddressAssociation.MacAddress, observation.Association, "MAC 关联范围"); c.Require(observation.ObservedAtUtc.HasValue, "捕获时间保留");
                map.Clear(); using (WmiApi.UseQuerySource((_, _) => new())) { var empty = await c.CallAsync(() => VmIpService.LookupAsync(id, mac)); c.Require(empty.HasData && empty.Data!.Count == 0, "空来源返回空列表"); }
                using (WmiApi.UseQuerySource((_, _) => new(new ApiFailure(ApiErrorKind.AccessDenied, 5, ApiErrorSource.Win32, "测试只读来源拒绝")))) { var denied = await c.CallAsync(() => VmIpService.LookupAsync(id, mac)); c.Equal(ApiErrorKind.AccessDenied, denied.ErrorKind, "来源失败传播"); }
                var invalid = await c.CallAsync(() => VmIpService.LookupAsync(Guid.Empty, mac)); c.Equal(ApiErrorKind.InvalidArgument, invalid.ErrorKind, "空 VM 身份拒绝"); return;
            }
            var resolved = c.Call(() => VmIpService.ResolveAdapterAddresses(mac, ["192.0.2.88"], "192.0.2.99")); c.Sequence(new[] { ip }, resolved.Addresses, "宿主嗅探覆盖其他来源");
            map.Clear(); resolved = c.Call(() => VmIpService.ResolveAdapterAddresses(mac, [" 192.0.2.88,192.0.2.88 ", "2001:db8::1", "bad"], "192.0.2.99")); c.Sequence(new[] { "192.0.2.88", "2001:db8::1" }, resolved.Addresses, "上报地址规范化、去重、过滤");
            resolved = c.Call(() => VmIpService.ResolveAdapterAddresses(mac, [], "192.0.2.99")); c.Sequence(new[] { "192.0.2.99" }, resolved.Addresses, "查询来源后备");
            foreach (string? input in new[] { mac, null }) { resolved = c.Call(() => VmIpService.ResolveAdapterAddresses(input, [])); c.Require(resolved.Addresses.Count == 0, "空来源保持空地址"); c.Equal(input is not null, resolved.RequiresLookup, "有 MAC 才继续查询"); }
        }
        finally { state.SetValue(service, 0); map.Clear(); }
    }
    private static async Task LiveAsync(CatalogTest p, ApiCaseContext c, ArpSnoopService service)
    {
        var idle = await WindowsTestCommand.RunAsync("$s=pktmon status 2>&1;$exit=$LASTEXITCODE;$f=pktmon filter list 2>&1;@{status=($s -join \"`n\");filters=($f -join \"`n\");exit=$exit;filterExit=$LASTEXITCODE}|ConvertTo-Json");
        string status = idle.GetProperty("status").GetString()!, filters = idle.GetProperty("filters").GetString()!;
        if (!ExHyperV.Core.Tests.ResourceInspection.PacketCaptureAvailability.IsIdle(status, idle.GetProperty("exit").GetInt32(), filters, idle.GetProperty("filterExit").GetInt32()))
            throw new ApiCheckSkippedException("PktMon 的空闲状态与空筛选器尚未核实；现有捕获和筛选器保持原样。");
        c.Call(service.Start);
        if (!service.IsAvailable)
        {
            c.Require(service.LastFailure is { Success: false }, "捕获启动失败具有结构化诊断");
            if (service.LastFailure!.ErrorKind is ApiErrorKind.Unsupported or ApiErrorKind.AccessDenied)
                throw new ApiCheckSkippedException("Packet Monitor 权限或系统能力缺失：" + service.LastFailure.Error);
            throw new ApiCheckFailureException("抓包资源空闲时，Core 启动捕获失败：" + service.LastFailure.Error);
        }
        try
        {
            var field = typeof(ArpSnoopService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!; var handle = field.GetValue(service);
            c.Call(service.Start); c.Equal(handle, field.GetValue(service), "重复启动复用同一捕获会话");
            await using var network = await TemporarySwitch.CreateAsync(c, internalNetwork: true);
            var subnet = await WindowsTestCommand.RunAsync("""
                $adapter=Get-VMNetworkAdapter -ManagementOS -SwitchName $a.name
                $nic=Get-NetAdapter|Where-Object {($_.MacAddress -replace '-','') -eq $adapter.MacAddress}|Select-Object -First 1
                if(!$nic){throw '内部交换机宿主接口尚未枚举'}
                $third=Get-Random -Minimum 1 -Maximum 254;$prefix='198.18.'+$third+'.'
                if(@(Get-NetIPAddress|Where-Object {$_.IPAddress -like ($prefix+'*')}).Count){throw '测试子网已有占用'}
                New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress ($prefix+'1') -PrefixLength 30 -AddressFamily IPv4|Out-Null
                @{source=($prefix+'1');target=($prefix+'2');mac=$adapter.MacAddress}|ConvertTo-Json
                """, new { name = network.Name });
            byte[] hardware = new byte[6]; uint length = 6; SendARP(BitConverter.ToUInt32(IPAddress.Parse(subnet.GetProperty("target").GetString()!).GetAddressBytes()), BitConverter.ToUInt32(IPAddress.Parse(subnet.GetProperty("source").GetString()!).GetAddressBytes()), hardware, ref length);
            string mac = subnet.GetProperty("mac").GetString()!; string observed = "";
            var deadline = DateTime.UtcNow.AddSeconds(10); while (!service.TryGetIp(mac, out observed) && DateTime.UtcNow < deadline) await Task.Delay(100, c.Token);
            c.Equal(subnet.GetProperty("source").GetString(), observed, "独立生成的 ARP 请求被 Core 捕获并解析");
            if (p.Api.Contains("SharedBackground")) { await c.CallAsync(async () => { await SharedBackgroundServices.ShutdownAsync(); return true; }); await SharedBackgroundServices.ShutdownAsync(); }
            else c.Call(service.Dispose);
            c.Require(!service.IsAvailable, "退出后停止捕获"); c.Require(!service.TryGetIp(mac, out _), "退出后缓存无法作为有效来源"); c.Call(service.Dispose); c.Call(service.Start); c.Require(!service.IsAvailable, "最终释放后保持已结束状态");
        }
        finally { service.Dispose(); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint SendARP(uint destination, uint source, byte[] hardware, ref uint length);
}
