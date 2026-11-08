using System.Management;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmNetworkFeatureApiCases
{
    private sealed record Field(string Property, string Native, object Value, string Feature, object? Expected = null);
    private const string Security = "Msvm_EthernetSwitchPortSecuritySettingData", Offload = "Msvm_EthernetSwitchPortOffloadSettingData", Bandwidth = "Msvm_EthernetSwitchPortBandwidthSettingData", Vlan = "Msvm_EthernetSwitchPortVlanSettingData";
    // Absolute reservations are quantized by link capacity; a 2 Mbps sample is rejected on the 10 Gbps test switch.
    private const ulong BandwidthLimit = 1_000_000_000, BandwidthReservation = 100_000_000;
    private static readonly Field[] SecurityFields = [
        new(nameof(VmNetworkAdapter.MacSpoofingAllowed), "AllowMacSpoofing", true, Security),
        new(nameof(VmNetworkAdapter.DhcpGuardEnabled), "EnableDhcpGuard", true, Security),
        new(nameof(VmNetworkAdapter.RouterGuardEnabled), "EnableRouterGuard", true, Security),
        new(nameof(VmNetworkAdapter.TeamingAllowed), "AllowTeaming", true, Security),
        new(nameof(VmNetworkAdapter.MonitorMode), "MonitorMode", PortMonitorMode.Source, Security, (byte)2),
        new(nameof(VmNetworkAdapter.StormLimit), "StormLimit", 123u, Security)];
    private static readonly Field[] OffloadFields = [
        new(nameof(VmNetworkAdapter.VmqEnabled), "VMQOffloadWeight", false, Offload, 0u),
        new(nameof(VmNetworkAdapter.SriovEnabled), "IOVOffloadWeight", true, Offload, 1u),
        new(nameof(VmNetworkAdapter.IpsecOffloadEnabled), "IPSecOffloadLimit", false, Offload, 0u)];
    internal static bool Supports(CatalogTest plan) => plan.Api is
        "VmNetworkService.ApplyVlanSettingsAsync()" or "VmNetworkService.ApplyBandwidthSettingsAsync()"
        or "VmNetworkService.ApplySecuritySettingsAsync()" or "VmNetworkService.ApplyOffloadSettingsAsync()"
        or "VmNetworkService.ApplySecurityAndOffloadSettingsAsync()" or "VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync()"
        or "VmNetworkService.UpdateConnectionAsync()" or "VmNetworkService.GetNetworkAdaptersAsync()"
        or "VmNetworkService.GetAvailableSwitchesAsync()" or "VmNetworkService.FillDynamicIpsAsync()";

    internal static async Task RunAsync(CatalogTest plan, ApiCaseContext c)
    {
        if (plan.Api == "VmNetworkService.GetAvailableSwitchesAsync()")
        {
            var before = SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch").ToDictionary(x => Guid.Parse((string)x["Name"]!), x => (string)x["ElementName"]!);
            var result = await c.CallAsync(VmNetworkService.GetAvailableSwitchesAsync);
            c.Require(result.HasData, "交换机清单读取：" + result.Error);
            c.Sequence(before.OrderBy(x => x.Key), result.Data!.OrderBy(x => x.Key), "交换机身份与名称"); return;
        }
        await using var network = await TemporarySwitch.CreateAsync(c);
        await using var vm = await TemporaryVm.CreateAsync(c);
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMNetworkAdapter|Connect-VMNetworkAdapter -SwitchName $a.network; @{ready=$true}|ConvertTo-Json", new { id = vm.Id, network = network.Name });
        var response = await VmNetworkService.GetNetworkAdaptersAsync(vm.Id);
        c.Require(response.HasData && response.Data!.Count == 1, "准备一张专属测试网卡"); var adapter = response.Data![0];
        if (plan.Api == "VmNetworkService.FillDynamicIpsAsync()") { await IpsAsync(plan, c, vm, adapter); return; }
        if (plan.Api == "VmNetworkService.GetNetworkAdaptersAsync()") { await ReadAsync(c, vm, network, adapter.Id); return; }
        int n = int.Parse(plan.Id.Split('-').Last());
        if (n == 3) await vm.DisposeAsync();
        if (plan.Api == "VmNetworkService.UpdateConnectionAsync()")
        {
            foreach (bool enabled in n == 2 ? new[] { true, true } : new[] { true, false })
            {
                adapter.IsConnected = enabled;
                var result = await c.CallAsync(() => VmNetworkService.UpdateConnectionAsync(vm.Id, adapter, network.Id));
                if (n == 3) { c.Equal(ApiErrorKind.NotFound, result.ErrorKind, "已删除目标"); return; }
                VmSettingsApiCases.Success(c, result);
                var allocation = Allocation(adapter.Id);
                VmSettingsApiCases.EqualRaw(c, enabled ? 2 : 3, allocation["EnabledState"], "连接状态");
                c.Require(((string[])allocation["HostResource"]!).Any(p => p.Contains(network.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)), "连接或断开均保留明确交换机身份");
            }
            return;
        }
        if (plan.Api == "VmNetworkService.ApplyVlanSettingsAsync()") { await VlansAsync(vm.Id, adapter, n, c); return; }
        bool combined = plan.Api.Contains("SecurityAndOffload", StringComparison.Ordinal);
        var fields = plan.Api.Contains("Bandwidth", StringComparison.Ordinal) ? new Field[] {
            new(nameof(VmNetworkAdapter.BandwidthLimitBitsPerSecond), "Limit", BandwidthLimit, Bandwidth),
            new(nameof(VmNetworkAdapter.BandwidthReservationBitsPerSecond), "Reservation", BandwidthReservation, Bandwidth) }
            : combined ? [.. SecurityFields, .. OffloadFields] : plan.Api.Contains("Security", StringComparison.Ordinal) ? SecurityFields : OffloadFields;
        if (n == 3)
        {
            var failure = await ApplyAsync(plan.Api, vm.Id, adapter, [fields[0].Property], c);
            c.Equal(ApiErrorKind.NotFound, failure.ErrorKind, "已删除目标"); return;
        }
        foreach (var field in fields)
        {
            typeof(VmNetworkAdapter).GetProperty(field.Property)!.SetValue(adapter, field.Value);
            for (int repeat = 0; repeat < (n == 2 ? 2 : 1); repeat++)
            {
                var before = Feature(adapter.Id, field.Feature);
                VmSettingsApiCases.Success(c, await ApplyAsync(plan.Api, vm.Id, adapter, [field.Property], c));
                var after = Feature(adapter.Id, field.Feature);
                VmSettingsApiCases.EqualRaw(c, field.Expected ?? field.Value, after[field.Native], field.Property + " 独立读回");
                foreach (var peer in fields.Where(f => f.Feature == field.Feature && f.Native != field.Native))
                    if (before.ContainsKey(peer.Native)) VmSettingsApiCases.EqualRaw(c, before[peer.Native], after[peer.Native], "保留未选字段 " + peer.Native);
            }
        }
        if (n == 1)
        {
            VmSettingsApiCases.Success(c, await ApplyAsync(plan.Api, vm.Id, adapter, fields.Select(f => f.Property).ToArray(), c));
            if (plan.Api.Contains("Bandwidth", StringComparison.Ordinal))
            {
                adapter.BandwidthReservationBitsPerSecond = adapter.BandwidthLimitBitsPerSecond = 0;
                VmSettingsApiCases.Success(c, await ApplyAsync(plan.Api, vm.Id, adapter, fields.Select(f => f.Property).ToArray(), c));
                var raw = Feature(adapter.Id, Bandwidth); VmSettingsApiCases.EqualRaw(c, 0, raw["Limit"], "解除上限"); VmSettingsApiCases.EqualRaw(c, 0, raw["Reservation"], "解除保留");
            }
            if (combined)
            {
                int secondCalls = 0;
                var rejected = await c.CallAsync(() => VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(
                    () => Task.FromResult(ApiResponse.Fail("受控安全写入故障")), () => { secondCalls++; return Task.FromResult(ApiResponse.Ok()); }));
                c.Require(!rejected.Success && !rejected.SecurityApplied && !rejected.OffloadApplied && secondCalls == 0, "受控故障：第一步失败阻止第二步");
                var partial = await c.CallAsync(() => VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(
                    () => Task.FromResult(ApiResponse.Ok()), () => Task.FromResult(ApiResponse.Fail("受控卸载写入故障"))));
                c.Require(!partial.Success && partial.SecurityApplied && !partial.OffloadApplied, "受控故障：保留先前已完成步骤");
            }
        }
    }

    private static async Task<ApiResponse> ApplyAsync(string api, Guid vm, VmNetworkAdapter adapter, string[] fields, ApiCaseContext c)
    {
        switch (api)
        {
            case "VmNetworkService.ApplyBandwidthSettingsAsync()": return await c.CallAsync(() => VmNetworkService.ApplyBandwidthSettingsAsync(vm, adapter, fields));
            case "VmNetworkService.ApplySecuritySettingsAsync()": return await c.CallAsync(() => VmNetworkService.ApplySecuritySettingsAsync(vm, adapter, fields));
            case "VmNetworkService.ApplyOffloadSettingsAsync()": return await c.CallAsync(() => VmNetworkService.ApplyOffloadSettingsAsync(vm, adapter, fields));
            case "VmNetworkService.ApplySecurityAndOffloadSettingsAsync()": return await c.CallAsync(() => VmNetworkService.ApplySecurityAndOffloadSettingsAsync(vm, adapter, fields));
            case "VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync()":
                var result = await c.CallAsync(() => VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(vm, adapter, fields));
                c.Evidence.Add($"组合结果：成功={result.Success}，安全={result.SecurityApplied}，卸载={result.OffloadApplied}，原因={result.Message}");
                return result.Success ? ApiResponse.Ok() : ApiResponse.Fail(result.Message, kind: result.Failure?.Kind ?? ApiErrorKind.OperationFailed);
            default: throw new InvalidDataException(api);
        }
    }

    private static async Task VlansAsync(Guid vm, VmNetworkAdapter adapter, int n, ApiCaseContext c)
    {
        if (n == 3) { c.Equal(ApiErrorKind.NotFound, (await c.CallAsync(() => VmNetworkService.ApplyVlanSettingsAsync(vm, adapter, [nameof(adapter.AccessVlanId)]))).ErrorKind, "已删除目标"); return; }
        foreach (int id in n == 2 ? new[] { 42, 42 } : new[] { 0, 1, 4094 })
        {
            adapter.VlanMode = VlanOperationMode.Access; adapter.AccessVlanId = id;
            VmSettingsApiCases.Success(c, await c.CallAsync(() => VmNetworkService.ApplyVlanSettingsAsync(vm, adapter, [nameof(adapter.VlanMode), nameof(adapter.AccessVlanId)])));
            var raw = Feature(adapter.Id, Vlan); VmSettingsApiCases.EqualRaw(c, 1, raw["OperationMode"], "接入模式"); VmSettingsApiCases.EqualRaw(c, id, raw["AccessVlanId"], "接入 VLAN");
        }
        if (n == 2) return;
        adapter.VlanMode = VlanOperationMode.Trunk; adapter.NativeVlanId = 1; adapter.TrunkAllowedVlanIds = [1, 42, 4094];
        VmSettingsApiCases.Success(c, await c.CallAsync(() => VmNetworkService.ApplyVlanSettingsAsync(vm, adapter, [nameof(adapter.VlanMode), nameof(adapter.NativeVlanId), nameof(adapter.TrunkAllowedVlanIds)])));
        var trunk = Feature(adapter.Id, Vlan); VmSettingsApiCases.EqualRaw(c, 2, trunk["OperationMode"], "中继模式");
        c.Sequence(new ushort[] { 1, 42, 4094 }, (ushort[])trunk["TrunkVlanIdArray"]!, "允许 VLAN 集合");
        VmSettingsApiCases.EqualRaw(c, 0, trunk["AccessVlanId"], "切换模式清理接入 ID");
        adapter.NativeVlanId = 4095;
        var invalid = await c.CallAsync(() => VmNetworkService.ApplyVlanSettingsAsync(vm, adapter, [nameof(adapter.NativeVlanId)]));
        c.Require(!invalid.Success, "拒绝超界 VLAN"); VmSettingsApiCases.EqualRaw(c, 1, Feature(adapter.Id, Vlan)["NativeVlanId"], "拒绝后保持原配置");
    }

    internal static Dictionary<string, object?> Allocation(string adapterId) => ReadRelated(adapterId, null);
    internal static Dictionary<string, object?> Feature(string adapterId, string feature) => ReadRelated(adapterId, feature);
    private static Dictionary<string, object?> ReadRelated(string adapterId, string? feature)
    {
        string escaped = adapterId.Replace("\\", "\\\\").Replace("'", "\\'");
        using var searcher = new ManagementObjectSearcher(@"root\virtualization\v2", $"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID='{escaped}'");
        using var ports = searcher.Get(); using var port = ports.Cast<ManagementObject>().Single();
        using var allocations = port.GetRelated("Msvm_EthernetPortAllocationSettingData"); using var allocation = allocations.Cast<ManagementObject>().Single();
        if (feature is null) return Copy(allocation);
        using var features = allocation.GetRelated(feature); using var instance = features.Cast<ManagementObject>().SingleOrDefault();
        return instance is null ? new(StringComparer.OrdinalIgnoreCase) : Copy(instance);
    }
    private static Dictionary<string, object?> Copy(ManagementObject row) => row.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);

    private static async Task ReadAsync(ApiCaseContext c, TemporaryVm vm, TemporarySwitch network, string adapterId)
    {
        await WindowsTestCommand.RunAsync("""
            $nic=Get-VM -Id ([guid]$a.id)|Get-VMNetworkAdapter
            $nic|Set-VMNetworkAdapter -StaticMacAddress 00155D012345 -MacAddressSpoofing On -DhcpGuard On -RouterGuard On -AllowTeaming On -PortMirroring Source -StormLimit 123 -MaximumBandwidth $a.limit -MinimumBandwidthAbsolute $a.reservation -VmqWeight 100 -IovWeight 0 -IPSecOffloadMaximumSecurityAssociation 0
            $nic|Set-VMNetworkAdapterVlan -Access -VlanId 42
            @{ready=$true}|ConvertTo-Json
            """, new { id = vm.Id, limit = BandwidthLimit, reservation = BandwidthReservation });
        var baseline = Feature(adapterId, Bandwidth);
        var result = await c.CallAsync(() => VmNetworkService.GetNetworkAdaptersAsync(vm.Id));
        c.Require(result.HasData && result.Data!.Count == 1, "网卡数量及读取成功"); var nic = result.Data![0];
        c.Equal(network.Id, nic.SwitchId, "交换机 GUID"); c.Equal(network.Name, nic.SwitchName, "交换机名称");
        c.Require(nic.DeviceId != Guid.Empty && nic.IsConnected && nic.IsStaticMac, "网卡身份和连接标记");
        c.Equal("00155D012345", new string(nic.MacAddress.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant(), "静态 MAC");
        c.Equal(VlanOperationMode.Access, nic.VlanMode, "VLAN 模式"); c.Equal(42, nic.AccessVlanId, "VLAN ID");
        c.Equal(BandwidthLimit, nic.BandwidthLimitBitsPerSecond, "带宽上限原始 bit/s");
        c.Equal(BandwidthReservation, Convert.ToUInt64(baseline["Reservation"]), "准备非零保留带宽样本");
        c.Equal(Convert.ToUInt64(baseline["Reservation"]), nic.BandwidthReservationBitsPerSecond, "系统实际带宽保留 bit/s");
        c.Require(nic.MacSpoofingAllowed && nic.DhcpGuardEnabled && nic.RouterGuardEnabled && nic.TeamingAllowed, "安全字段");
        c.Equal(PortMonitorMode.Source, nic.MonitorMode, "镜像方向"); c.Equal(123u, nic.StormLimit, "风暴限制");
        c.Require(nic.VmqEnabled && !nic.SriovEnabled && !nic.IpsecOffloadEnabled, "卸载字段");
    }

    private static async Task IpsAsync(CatalogTest plan, ApiCaseContext c, TemporaryVm vm, VmNetworkAdapter adapter)
    {
        await using var peer = await TemporaryVm.CreateAsync(c);
        var foreign = await VmNetworkService.GetNetworkAdaptersAsync(peer.Id); c.Require(foreign.HasData, "对照虚拟机网卡");
        if (plan.Id == "contract.network-ownership")
        {
            var original = foreign.Data![0].IpAddresses.ToArray();
            c.Equal(ApiErrorKind.Conflict, (await c.CallAsync(() => VmNetworkService.FillDynamicIpsAsync(vm.Id, foreign.Data!))).ErrorKind, "跨虚拟机网卡身份被拒绝");
            c.Sequence(original, foreign.Data![0].IpAddresses, "拒绝前保留对方模型"); return;
        }
        foreach (var target in new[] { vm, peer })
        {
            await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Add-VMNetworkAdapter -Name secondary; @{ready=$true}|ConvertTo-Json", new { id = target.Id });
            var current = await VmNetworkService.GetNetworkAdaptersAsync(target.Id); c.Require(current.HasData && current.Data!.Count == 2, "每台两张网卡");
            int index = target == vm ? 10 : 20;
            foreach (var nic in current.Data!) nic.IpAddresses = [$"192.0.2.{index++}"];
            var expected = current.Data.Select(nic => nic.IpAddresses.Single()).ToArray();
            VmSettingsApiCases.Success(c, await c.CallAsync(() => VmNetworkService.FillDynamicIpsAsync(target.Id, current.Data)));
            c.Sequence(expected, current.Data.Select(nic => nic.IpAddresses.Single()), "保留已知且按设备归属的 IPv4 来源");
        }
    }
}
