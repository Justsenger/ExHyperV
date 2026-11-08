using System.Net;
using System.Net.Sockets;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmPerformanceApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmQueryService.UpdateDiskPerformanceAsync()" or "VmQueryService.UpdateNetworkPerformanceAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        VmTestAccess.RequireCredentials(c);
        await using var network = p.Api.Contains("Network") ? await TemporarySwitch.CreateAsync(c, internalNetwork: true) : null;
        await using var vm = await TemporaryVm.CopyTemplateAsync(c);
        string adapterName = "ApiLoad-" + Guid.NewGuid().ToString("N");
        if (network is not null) await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Add-VMNetworkAdapter -Name $a.name -SwitchName $a.network;@{ok=$true}|ConvertTo-Json", new { id = vm.Id, name = adapterName, network = network.Name });
        await VmTestAccess.ReadyWindowsAsync(c, vm);
        await using var query = new VmQueryService(); var inventory = await query.GetVmListAsync(); c.Require(inventory.HasData, inventory.Error); var model = inventory.Data!.VirtualMachines.Single(v => v.Id == vm.Id);
        if (network is null)
        {
            c.Require(model.Disks.Count > 0, "模板副本具有实际磁盘");
            await c.CallAsync(async () => { await query.UpdateDiskPerformanceAsync([model]); return true; });
            var load = VmTestAccess.WindowsAsync(c, vm, """
                $path=Join-Path $env:TEMP ('ExHyperV-ApiIo-'+[guid]::NewGuid().ToString('N')+'.bin')
                $data=New-Object byte[] (4MB);[Random]::new(23).NextBytes($data);$bytes=0
                try{$until=[DateTime]::UtcNow.AddSeconds(20)
                    while([DateTime]::UtcNow -lt $until){$f=[IO.FileStream]::new($path,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
                        try{for($i=0;$i -lt 8;$i++){$f.Write($data,0,$data.Length);$bytes+=$data.Length};$f.Flush($true);$f.Position=0;while($f.Read($data,0,$data.Length) -gt 0){}}finally{$f.Dispose()}}
                    @{bytes=$bytes}
                }finally{if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
                """, seconds: 90);
            bool active = false; try
            {
                for (int i = 0; i < 22; i++) { await Task.Delay(1000, c.Token); await c.CallAsync(async () => { await query.UpdateDiskPerformanceAsync([model]); return true; }); active |= model.Disks.Any(v => v.PerformanceSample.IsValid && v.ReadBytesPerSecond + v.WriteBytesPerSecond > 0); foreach (var disk in model.Disks.Where(v => v.PerformanceSample.IsValid)) c.Require(disk.ReadBytesPerSecond >= 0 && disk.WriteBytesPerSecond >= 0, "磁盘有效速率为非负数"); }
            }
            finally { var completed = await load; c.Require(completed.GetProperty("bytes").GetInt64() > 0, "副本内磁盘负载实际完成写入"); }
            c.Require(active, "Core 在副本磁盘实际 I/O 期间读到有效非零速率");
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Stop-VM -TurnOff -Force;@{ok=$true}|ConvertTo-Json", new { id = vm.Id }); model.StateCode = 3;
                await c.CallAsync(async () => { await query.UpdateDiskPerformanceAsync([model]); return true; }); foreach (var disk in model.Disks) c.Equal(PerformanceSampleState.NotApplicable, disk.PerformanceSample.State, "停机后速率明确为非适用");
                await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Get-VMHardDiskDrive|Remove-VMHardDiskDrive;@{ok=$true}|ConvertTo-Json", new { id = vm.Id }); model.StateCode = 2;
                await c.CallAsync(async () => { await query.UpdateDiskPerformanceAsync([model]); return true; }); c.Require(model.Disks.All(v => !v.PerformanceSample.IsValid), "失效磁盘身份停止发布有效速率");
            }
            return;
        }
        var setup = await WindowsTestCommand.RunAsync("""
            $hostNic=Get-VMNetworkAdapter -ManagementOS -SwitchName $a.network
            $nic=Get-NetAdapter|Where-Object {($_.MacAddress -replace '-','') -eq $hostNic.MacAddress}|Select-Object -First 1
            if(!$nic){throw '内部交换机宿主接口尚未枚举'}
            $prefix='198.19.'+(Get-Random -Minimum 1 -Maximum 254)+'.'
            if(@(Get-NetIPAddress|Where-Object {$_.IPAddress -like ($prefix+'*')}).Count){throw '测试子网已有占用'}
            New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress ($prefix+'1') -PrefixLength 30 -AddressFamily IPv4|Out-Null
            $vmNic=Get-VM -Id $a.id|Get-VMNetworkAdapter -Name $a.name
            @{host=($prefix+'1');vm=($prefix+'2');mac=$vmNic.MacAddress}|ConvertTo-Json
            """, new { id = vm.Id, name = adapterName, network = network.Name });
        var adapter = model.NetworkAdapters.Single(v => v.Name == adapterName);
        await VmTestAccess.WindowsAsync(c, vm, "param($p) $nic=Get-NetAdapter|Where-Object {($_.MacAddress -replace '-','') -eq $p.mac};if(@($nic).Count -ne 1){throw '测试网卡身份匹配失败'};New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress $p.ip -PrefixLength 30 -AddressFamily IPv4|Out-Null;@{ok=$true}", new { mac = setup.GetProperty("mac").GetString(), ip = setup.GetProperty("vm").GetString() });
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Parse(setup.GetProperty("host").GetString()!), 0)); int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var stop = new CancellationTokenSource(); long bytesReceived = 0;
        var drain = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    try { var packet = await receiver.ReceiveAsync(stop.Token); Interlocked.Add(ref bytesReceived, packet.Buffer.Length); }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { }
                }
            }
            catch (OperationCanceledException) { }
        });
        var toVm = new IPEndPoint(IPAddress.Parse(setup.GetProperty("vm").GetString()!), port);
        var hostTraffic = Task.Run(async () =>
        {
            byte[] payload = new byte[1200];
            try { while (true) { for (int i = 0; i < 50; i++) await receiver.SendAsync(payload, toVm, stop.Token); await Task.Delay(10, stop.Token); } }
            catch (OperationCanceledException) { }
        });
        var send = VmTestAccess.WindowsAsync(c, vm, """
            param($p) $udp=[Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Parse($p.vm),[int]$p.port));$payload=New-Object byte[] 1200;$bytes=0
            try{$until=[DateTime]::UtcNow.AddSeconds(20);while([DateTime]::UtcNow -lt $until){for($i=0;$i -lt 50;$i++){$bytes+=$udp.Send($payload,$payload.Length,$p.host,[int]$p.port)};while($udp.Available -gt 0){$remote=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0);$null=$udp.Receive([ref]$remote)};Start-Sleep -Milliseconds 10};@{sent=$bytes}}finally{$udp.Dispose()}
            """, new { host = setup.GetProperty("host").GetString(), vm = setup.GetProperty("vm").GetString(), port }, 90);
        bool traffic = false, incoming = false;
        try { for (int i = 0; i < 22; i++) { await Task.Delay(1000, c.Token); await c.CallAsync(async () => { await query.UpdateNetworkPerformanceAsync([model]); return true; }); traffic |= adapter.PerformanceSample.IsValid && adapter.SendBytesPerSecond > 0; incoming |= adapter.PerformanceSample.IsValid && adapter.ReceiveBytesPerSecond > 0; } }
        finally
        {
            try { var result = await send; c.Require(result.GetProperty("sent").GetInt64() > 0, "虚拟机内 UDP 发送负载完成"); }
            finally { stop.Cancel(); await Task.WhenAll(drain, hostTraffic); }
        }
        c.Require(traffic && incoming, "Core 读到指定测试网卡的有效发送与接收速率"); c.Evidence.Add("独立宿主 UDP 接收字节数：" + bytesReceived + "（受宿主入站策略影响）");
        if (p.Id.EndsWith("-2", StringComparison.Ordinal))
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Get-VMNetworkAdapter -Name $a.name|Remove-VMNetworkAdapter;@{ok=$true}|ConvertTo-Json", new { id = vm.Id, name = adapterName });
            await Task.Delay(2000, c.Token); await c.CallAsync(async () => { await query.UpdateNetworkPerformanceAsync([model]); return true; }); c.Require(!adapter.PerformanceSample.IsValid, "删除后旧网卡身份缺测");
            await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Add-VMNetworkAdapter -Name $a.name -SwitchName $a.network;@{ok=$true}|ConvertTo-Json", new { id = vm.Id, name = adapterName, network = network.Name });
            var refreshed = await VmNetworkService.GetNetworkAdaptersAsync(vm.Id); c.Require(refreshed.HasData, refreshed.Error); var replacement = refreshed.Data!.Single(v => v.Name == adapterName);
            c.Require(adapter.Id != replacement.Id, "同名重建设备具有新身份"); model.NetworkAdapters.Clear(); model.NetworkAdapters.Add(replacement);
            await c.CallAsync(async () => { await query.UpdateNetworkPerformanceAsync([model]); return true; }); c.Require(replacement.SendBytesPerSecond == 0, "新网卡初次样本保持自己的速率");
        }
    }
}
