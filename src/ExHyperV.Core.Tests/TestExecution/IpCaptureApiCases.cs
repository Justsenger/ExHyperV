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
    private static bool Lifecycle(CatalogTest p) => p.Api is "ArpSnoopService.Start()" or "ArpSnoopService.Dispose()" or "SharedBackgroundServices.ShutdownAsync()";
    internal static bool Live(CatalogTest p) => Lifecycle(p) && !p.Id.EndsWith("-2", StringComparison.Ordinal);
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        var service = ArpSnoopService.Instance;
        if (Lifecycle(p))
        {
            if (Live(p)) await LiveAsync(p, c, service);
            else { await CloseAsync(p, c, service); AssertReleased(c, service); await CloseAsync(p, c, service); c.Call(service.Start); AssertReleased(c, service); }
            return;
        }
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
        var before = await ReadGlobalCaptureAsync();
        using var peer = new ArpSnoopService();
        try
        {
            Start(c, service); Start(c, peer);
            nint handle = Handle(service, "_session"), peerHandle = Handle(peer, "_session");
            c.Require(handle != 0 && peerHandle != 0 && handle != peerHandle, "两个捕获实例具有各自的会话句柄");
            c.Call(service.Start); c.Equal(handle, Handle(service, "_session"), "重复启动复用本实例会话");
            if (p.Api != "ArpSnoopService.Start()")
            {
                await CloseAsync(p, c, service); AssertReleased(c, service);
                await CloseAsync(p, c, service); c.Call(service.Start); AssertReleased(c, service);
                c.Require(peer.IsAvailable && peer.LastFailure is null && Handle(peer, "_session") == peerHandle, "关闭目标后另一独立捕获仍运行");
                return;
            }
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
            c.Call(service.Dispose); AssertReleased(c, service);
            c.Require(!service.TryGetIp(mac, out _), "退出后缓存停止作为有效来源");
            // Clear only the peer's test cache, then require a fresh packet after target disposal.
            var map = (ConcurrentDictionary<string, (string Ip, long Ticks, DateTimeOffset ObservedAtUtc)>)typeof(ArpSnoopService).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer)!;
            map.Clear();
            length = 6; SendARP(BitConverter.ToUInt32(IPAddress.Parse(subnet.GetProperty("target").GetString()!).GetAddressBytes()), BitConverter.ToUInt32(IPAddress.Parse(subnet.GetProperty("source").GetString()!).GetAddressBytes()), hardware, ref length);
            deadline = DateTime.UtcNow.AddSeconds(10); while (!peer.TryGetIp(mac, out observed) && DateTime.UtcNow < deadline) await Task.Delay(100, c.Token);
            c.Equal(subnet.GetProperty("source").GetString(), observed, "关闭目标后另一实例继续接收新 ARP 报文");
        }
        finally
        {
            var failure = service.LastFailure; var peerFailure = peer.LastFailure;
            service.Dispose(); peer.Dispose();
            AssertReleased(c, service, failure); AssertReleased(c, peer, peerFailure);
            var after = await ReadGlobalCaptureAsync();
            c.Equal(before, after, "系统已有捕获状态和筛选器保持原样");
        }
    }

    private static nint Handle(ArpSnoopService service, string field)
        => (nint)typeof(ArpSnoopService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    private static void AssertReleased(ApiCaseContext c, ArpSnoopService service, ApiResponse? previousFailure = null)
    {
        c.Require(!service.IsAvailable, "捕获已结束");
        c.Require(new[] { "_monitor", "_session", "_stream" }.All(field => Handle(service, field) == 0), "本实例监视器、会话和实时流句柄均已释放");
        c.Require(ReferenceEquals(service.LastFailure, previousFailure), "资源清理没有新增接口错误：" + service.LastFailure?.Error);
    }
    private static async Task CloseAsync(CatalogTest p, ApiCaseContext c, ArpSnoopService service)
    {
        if (p.Api == "SharedBackgroundServices.ShutdownAsync()")
            await c.CallAsync(async () => { await SharedBackgroundServices.ShutdownAsync(); return true; });
        else c.Call(service.Dispose);
    }
    private static void Start(ApiCaseContext c, ArpSnoopService service)
    {
        c.Call(service.Start);
        if (service.IsAvailable) return;
        c.Require(service.LastFailure is { Success: false }, "捕获启动失败具有结构化诊断");
        if (service.LastFailure!.ErrorKind is ApiErrorKind.Unsupported or ApiErrorKind.AccessDenied)
            throw new ApiCheckSkippedException("Packet Monitor 权限或系统能力缺失：" + service.LastFailure.Error);
        throw new ApiCheckFailureException("Core 独立捕获启动失败：" + service.LastFailure.Error);
    }
    private static async Task<string> ReadGlobalCaptureAsync()
    {
        var snapshot = await WindowsTestCommand.RunAsync("$s=pktmon status 2>&1;$ec=$LASTEXITCODE;$f=pktmon filter list 2>&1;if($ec -ne 0 -or $LASTEXITCODE -ne 0){throw '系统捕获基准读取失败'};@{status=($s -join \"`n\");filters=($f -join \"`n\")}|ConvertTo-Json", timeoutSeconds: 15, mayMutate: false);
        return snapshot.GetProperty("status").GetString()!.Trim() + "\n" + snapshot.GetProperty("filters").GetString()!.Trim();
    }
    [DllImport("iphlpapi.dll")] private static extern uint SendARP(uint destination, uint source, byte[] hardware, ref uint length);
}
