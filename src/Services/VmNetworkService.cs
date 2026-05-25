using ExHyperV.Api;
using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services;

public class VmNetworkService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    // ── 查询 ──────────────────────────────────────────────────────

    public async Task<List<VmNetworkAdapter>> GetNetworkAdaptersAsync(string vmName)
    {
        var resultList = new List<VmNetworkAdapter>();
        if (string.IsNullOrEmpty(vmName)) return resultList;

        var vmResponse = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj["Name"]?.ToString());

        if (!vmResponse.HasData) return resultList;

        string vmGuid = vmResponse.Data!;

        var portsTask = WmiApi.QueryAsync(
            $"SELECT ElementName, InstanceID, Address, StaticMacAddress FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => (ManagementObject)obj);

        var allocsTask = WmiApi.QueryAsync(
            $"SELECT EnabledState, InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
            obj => (ManagementObject)obj);

        await Task.WhenAll(portsTask, allocsTask);

        var allPorts = portsTask.Result.Data ?? new List<ManagementObject>();
        var allAllocs = allocsTask.Result.Data ?? new List<ManagementObject>();

        foreach (var port in allPorts)
        {
            string elementName = port["ElementName"]?.ToString() ?? Properties.Resources.Common_NoName;
            string fullPortId = port["InstanceID"]?.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(fullPortId)) continue;

            string deviceGuid = fullPortId.Split('\\').Last();

            var adapter = new VmNetworkAdapter
            {
                Id = fullPortId,
                Name = elementName,
                MacAddress = Utils.FormatMac(port["Address"]?.ToString()),
                IsStaticMac = port.TryGet<bool>("StaticMacAddress") ?? false
            };

            var allocation = allAllocs.FirstOrDefault(a =>
                a["InstanceID"]?.ToString().Contains(deviceGuid, StringComparison.OrdinalIgnoreCase) == true);

            if (allocation != null)
            {
                adapter.IsConnected = allocation["EnabledState"]?.ToString() == "2";

                if (allocation["HostResource"] is string[] hostResources && hostResources.Length > 0)
                {
                    string swGuid = hostResources[0].Split('"').Reverse().Skip(1).FirstOrDefault();
                    adapter.SwitchName = await GetSwitchNameByGuidAsync(swGuid);
                }

                try
                {
                    string rawId = allocation["InstanceID"]?.ToString();
                    if (!string.IsNullOrEmpty(rawId))
                    {
                        string wqlSafeId = rawId.Replace(@"\", @"\\").Replace("'", "\\'");
                        string relPath = $"Msvm_EthernetPortAllocationSettingData.InstanceID=\"{wqlSafeId}\"";
                        string query = $"ASSOCIATORS OF {{{relPath}}} " +
                                       $"WHERE AssocClass = Msvm_EthernetPortSettingDataComponent " +
                                       $"ResultClass = Msvm_EthernetSwitchPortFeatureSettingData";

                        using var svcForScope = WmiApi.GetVirtualSystemManagementService();
                        using var searcher = new ManagementObjectSearcher(svcForScope.Scope, new ObjectQuery(query));
                        using var features = searcher.Get();

                        foreach (var feature in features.Cast<ManagementObject>())
                            ParseFeatureSettings(adapter, feature);
                    }
                }
                catch { }
            }
            else
            {
                adapter.IsConnected = false;
                adapter.SwitchName = Properties.Resources.Status_Unconnected;
            }

            resultList.Add(adapter);
        }

        return resultList;
    }

    public async Task<List<string>> GetAvailableSwitchesAsync()
    {
        var response = await WmiApi.QueryAsync(
            "SELECT ElementName FROM Msvm_VirtualEthernetSwitch",
            obj => obj["ElementName"]?.ToString());

        return (response.Data ?? new List<string?>())
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s)
            .ToList()!;
    }

    public async Task FillDynamicIpsAsync(string vmName, IEnumerable<VmNetworkAdapter> adapters)
    {
        var targets = adapters
            .Where(a => (a.IpAddresses == null || a.IpAddresses.Count == 0)
                     && !string.IsNullOrEmpty(a.MacAddress))
            .ToList();

        if (targets.Count == 0) return;

        foreach (var adapter in targets)
        {
            if (adapter.IpAddresses != null && adapter.IpAddresses.Count > 0) continue;
            try
            {
                string ip = await Utils.GetVmIpAddressAsync(vmName, adapter.MacAddress);
                if (!string.IsNullOrEmpty(ip))
                {
                    adapter.IpAddresses = ip
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).ToList();
                }
            }
            catch { }
        }
    }

    public async Task<string> GetVmIpAddressAsync(string vmName, string macAddressWithColons)
    {
        return await Utils.GetVmIpAddressAsync(vmName, macAddressWithColons);
    }

    // ── 网卡生命周期 ──────────────────────────────────────────────

    public async Task<(bool Success, string Message)> AddNetworkAdapterAsync(string vmName)
    {
        try
        {
            using var vm = WmiApi.GetVmComputerSystem(vmName);
            if (vm == null) return (false, Properties.Resources.Error_Net_VmNotFound);

            using var svcForScope = WmiApi.GetVirtualSystemManagementService();

            using var portTemplateSearcher = new ManagementObjectSearcher(svcForScope.Scope,
                new ObjectQuery("SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID LIKE '%Default%'"));
            using var portTemplateCol = portTemplateSearcher.Get();
            using var portTemplate = portTemplateCol.Cast<ManagementObject>().FirstOrDefault();

            if (portTemplate == null) return (false, Properties.Resources.Error_Net_TemplateNotFound);

            portTemplate["ElementName"] = Properties.Resources.Net_DefaultAdapterName;
            string portXml = portTemplate.GetText(TextFormat.CimDtd20);

            var portResult = await WmiApi.InvokeAsync(
                ServiceWql,
                "AddResourceSettings",
                p =>
                {
                    p["AffectedConfiguration"] = vm.Path.Path;
                    p["ResourceSettings"] = new string[] { portXml };
                });

            if (!portResult.Success) return (false, portResult.Error);

            using var allocTemplateSearcher = new ManagementObjectSearcher(svcForScope.Scope,
                new ObjectQuery("SELECT * FROM Msvm_EthernetPortAllocationSettingData WHERE InstanceID LIKE '%Default%'"));
            using var allocTemplateCol = allocTemplateSearcher.Get();
            using var allocTemplate = allocTemplateCol.Cast<ManagementObject>().FirstOrDefault();

            if (allocTemplate == null) return (false, Properties.Resources.Error_Net_TemplateNotFound);

            using var newPortSearcher = new ManagementObjectSearcher(svcForScope.Scope,
                new ObjectQuery($"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE ElementName = '{WmiApi.Escape(Properties.Resources.Net_DefaultAdapterName)}' AND InstanceID LIKE 'Microsoft:{vm["Name"]}%'"));
            using var newPortCol = newPortSearcher.Get();
            using var newPort = newPortCol.Cast<ManagementObject>().LastOrDefault();

            if (newPort == null) return (true, Properties.Resources.VmNet_AddSuccess);

            allocTemplate["Parent"] = newPort.Path.Path;
            string allocXml = allocTemplate.GetText(TextFormat.CimDtd20);

            await WmiApi.InvokeAsync(
                ServiceWql,
                "AddResourceSettings",
                p =>
                {
                    p["AffectedConfiguration"] = vm.Path.Path;
                    p["ResourceSettings"] = new string[] { allocXml };
                });

            return (true, Properties.Resources.VmNet_AddSuccess);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string Message)> RemoveNetworkAdapterAsync(string vmName, string id)
    {
        string escapedId = id.Replace("\\", "\\\\");

        var pathResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'",
            obj => obj.Path.Path);

        if (!pathResponse.HasData)
            return (false, Properties.Resources.Error_Net_AllocNotFound);

        var result = await WmiApi.InvokeAsync(
            ServiceWql,
            "RemoveResourceSettings",
            p => p["ResourceSettings"] = new string[] { pathResponse.Data! });

        return result.Success
            ? (true, string.Empty)
            : (false, result.Error);
    }

    public async Task<(bool Success, string Message)> UpdateConnectionAsync(
        string vmName, VmNetworkAdapter adapter)
    {
        string escapedId = adapter.Id.Replace("\\", "\\\\");

        var xmlResponse = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'",
            port =>
            {
                using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData")
                    .Cast<ManagementObject>().FirstOrDefault();
                if (allocation == null) return null;

                allocation["EnabledState"] = (ushort)(adapter.IsConnected ? 2 : 3);

                if (adapter.IsConnected && !string.IsNullOrEmpty(adapter.SwitchName))
                {
                    string path = GetSwitchPathByName(adapter.SwitchName);
                    if (!string.IsNullOrEmpty(path))
                        allocation["HostResource"] = new string[] { path };
                }

                return allocation.GetText(TextFormat.CimDtd20);
            });

        if (!xmlResponse.HasData || string.IsNullOrEmpty(xmlResponse.Data))
            return (false, Properties.Resources.Error_Net_AllocNotFound);

        var result = await WmiApi.InvokeAsync(
            ServiceWql,
            "ModifyResourceSettings",
            p => p["ResourceSettings"] = new string[] { xmlResponse.Data! });

        return result.Success
            ? (true, string.Empty)
            : (false, result.Error);
    }

    // ── 高级特性配置 ──────────────────────────────────────────────

    public async Task<(bool Success, string Message)> ApplyVlanSettingsAsync(
        string vmName, VmNetworkAdapter adapter)
    {
        if (adapter.VlanMode == VlanOperationMode.Private)
        {
            if (adapter.PvlanPrimaryId == 0) adapter.PvlanPrimaryId = 100;
            if (adapter.PvlanSecondaryId == 0) adapter.PvlanSecondaryId = 101;
            if (adapter.PvlanMode == PvlanMode.Promiscuous)
                adapter.PvlanSecondaryId = adapter.PvlanPrimaryId;
        }

        return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortVlanSettingData", s =>
        {
            s["OperationMode"] = (uint)adapter.VlanMode;
            switch (adapter.VlanMode)
            {
                case VlanOperationMode.Access:
                    s["AccessVlanId"] = (ushort)adapter.AccessVlanId;
                    s["NativeVlanId"] = (ushort)0;
                    s["TrunkVlanIdArray"] = null;
                    s["PvlanMode"] = (uint)0;
                    s["PrimaryVlanId"] = (ushort)0;
                    s["SecondaryVlanId"] = (ushort)0;
                    s["SecondaryVlanIdArray"] = null;
                    break;
                case VlanOperationMode.Trunk:
                    s["NativeVlanId"] = (ushort)adapter.NativeVlanId;
                    s["TrunkVlanIdArray"] = adapter.TrunkAllowedVlanIds?.Any() == true
                        ? adapter.TrunkAllowedVlanIds.Select(x => (ushort)x).ToArray()
                        : Array.Empty<ushort>();
                    s["AccessVlanId"] = (ushort)0;
                    s["PvlanMode"] = (uint)0;
                    s["PrimaryVlanId"] = (ushort)0;
                    s["SecondaryVlanId"] = (ushort)0;
                    s["SecondaryVlanIdArray"] = null;
                    break;
                case VlanOperationMode.Private:
                    uint pMode = (uint)adapter.PvlanMode == 0 ? 1u : (uint)adapter.PvlanMode;
                    ushort priId = (ushort)adapter.PvlanPrimaryId;
                    ushort secId = (ushort)adapter.PvlanSecondaryId;
                    s["PvlanMode"] = pMode;
                    s["PrimaryVlanId"] = priId;
                    if (pMode == 3)
                    {
                        s["SecondaryVlanId"] = (ushort)0;
                        s["SecondaryVlanIdArray"] = new ushort[] { priId };
                    }
                    else
                    {
                        s["SecondaryVlanId"] = secId;
                        s["SecondaryVlanIdArray"] = null;
                    }
                    s["AccessVlanId"] = (ushort)0;
                    s["NativeVlanId"] = (ushort)0;
                    s["TrunkVlanIdArray"] = null;
                    break;
            }
        });
    }

    public Task<(bool Success, string Message)> ApplyBandwidthSettingsAsync(
        string vmName, VmNetworkAdapter adapter)
        => EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortBandwidthSettingData", s =>
        {
            s["Limit"] = (ulong)(adapter.BandwidthLimit * 1000000);
            s["Reservation"] = (ulong)(adapter.BandwidthReservation * 1000000);
        });

    public Task<(bool Success, string Message)> ApplySecuritySettingsAsync(
        string vmName, VmNetworkAdapter adapter)
        => EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortSecuritySettingData", s =>
        {
            s["AllowMacSpoofing"] = adapter.MacSpoofingAllowed;
            s["EnableDhcpGuard"] = adapter.DhcpGuardEnabled;
            s["EnableRouterGuard"] = adapter.RouterGuardEnabled;
            s["AllowTeaming"] = adapter.TeamingAllowed;
            s["MonitorMode"] = (byte)adapter.MonitorMode;
            s["StormLimit"] = (uint)adapter.StormLimit;
        });

    public Task<(bool Success, string Message)> ApplyOffloadSettingsAsync(
        string vmName, VmNetworkAdapter adapter)
        => EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortOffloadSettingData", s =>
        {
            s["VMQOffloadWeight"] = (uint)(adapter.VmqEnabled ? 100 : 0);
            s["IOVOffloadWeight"] = (uint)(adapter.SriovEnabled ? 1 : 0);
            s["IPSecOffloadLimit"] = (uint)(adapter.IpsecOffloadEnabled ? 512 : 0);
        });

    // ── 核心内部逻辑 ──────────────────────────────────────────────

    private async Task<(bool Success, string Message)> EnsureAndModifyFeatureAsync(
        string portId, string featureClass, Action<ManagementObject> updateAction)
    {
        try
        {
            string escapedId = portId.Replace("\\", "\\\\");

            var xmlInfo = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'",
                port =>
                {
                    using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData")
                        .Cast<ManagementObject>().FirstOrDefault();
                    if (allocation == null) return null;

                    var existing = allocation.GetRelated(
                        featureClass, "Msvm_EthernetPortSettingDataComponent",
                        null, null, null, null, false, null)
                        .Cast<ManagementObject>().FirstOrDefault();

                    if (existing != null)
                    {
                        updateAction(existing);
                        return new { IsNew = false, Xml = existing.GetText(TextFormat.CimDtd20), Target = string.Empty };
                    }
                    else
                    {
                        var template = GetDefaultFeatureTemplate(featureClass);
                        if (template == null) return null;
                        template["InstanceID"] = Guid.NewGuid().ToString();
                        updateAction(template);
                        return new { IsNew = true, Xml = template.GetText(TextFormat.CimDtd20), Target = allocation.Path.Path };
                    }
                });

            if (!xmlInfo.HasData || xmlInfo.Data == null)
                return (false, Properties.Resources.Error_Net_ConfigObject);

            var info = xmlInfo.Data;
            string method = info.IsNew ? "AddFeatureSettings" : "ModifyFeatureSettings";

            var result = await WmiApi.InvokeAsync(
                ServiceWql,
                method,
                p =>
                {
                    p["FeatureSettings"] = new string[] { info.Xml };
                    if (info.IsNew) p["AffectedConfiguration"] = info.Target;
                });

            return result.Success
                ? (true, string.Empty)
                : (false, result.Error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── 业务逻辑 ──────────────────────────────────────────────────

    private void ParseFeatureSettings(VmNetworkAdapter adapter, ManagementObject feature)
    {
        string cls = feature.ClassPath.ClassName;

        if (cls == "Msvm_EthernetSwitchPortVlanSettingData")
        {
            uint rawMode = feature.TryGet<uint>("OperationMode") ?? 0;
            adapter.VlanMode = rawMode == 0 ? VlanOperationMode.Access : (VlanOperationMode)rawMode;
            adapter.AccessVlanId = (int)(feature.TryGet<uint>("AccessVlanId") ?? 0);
            adapter.NativeVlanId = (int)(feature.TryGet<uint>("NativeVlanId") ?? 0);
            adapter.PvlanMode = (PvlanMode)(feature.TryGet<uint>("PvlanMode") ?? 0);
            adapter.PvlanPrimaryId = (int)(feature.TryGet<uint>("PrimaryVlanId") ?? 0);
            adapter.PvlanSecondaryId = (int)(feature.TryGet<uint>("SecondaryVlanId") ?? 0);
            if (feature.HasProperty("TrunkVlanIdArray") && feature["TrunkVlanIdArray"] is ushort[] trunks)
                adapter.TrunkAllowedVlanIds = trunks.Select(x => (int)x).ToList();
        }
        else if (cls == "Msvm_EthernetSwitchPortBandwidthSettingData")
        {
            adapter.BandwidthLimit = (feature.TryGet<ulong>("Limit") ?? 0) / 1000000;
            adapter.BandwidthReservation = (feature.TryGet<ulong>("Reservation") ?? 0) / 1000000;
        }
        else if (cls == "Msvm_EthernetSwitchPortSecuritySettingData")
        {
            adapter.MacSpoofingAllowed = feature.TryGet<bool>("AllowMacSpoofing") ?? false;
            adapter.DhcpGuardEnabled = feature.TryGet<bool>("EnableDhcpGuard") ?? false;
            adapter.RouterGuardEnabled = feature.TryGet<bool>("EnableRouterGuard") ?? false;
            adapter.TeamingAllowed = feature.TryGet<bool>("AllowTeaming") ?? false;
            adapter.MonitorMode = (PortMonitorMode)(feature.TryGet<byte>("MonitorMode") ?? 0);
            adapter.StormLimit = feature.TryGet<uint>("StormLimit") ?? 0;
        }
        else if (cls == "Msvm_EthernetSwitchPortOffloadSettingData")
        {
            adapter.VmqEnabled = (feature.TryGet<uint>("VMQOffloadWeight") ?? 0) > 0;
            adapter.SriovEnabled = (feature.TryGet<uint>("IOVOffloadWeight") ?? 0) > 0;
            adapter.IpsecOffloadEnabled = (feature.TryGet<uint>("IPSecOffloadLimit") ?? 0) > 0;
        }
    }

    private async Task<string> GetSwitchNameByGuidAsync(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return Properties.Resources.Status_Unconnected;
        var response = await WmiApi.QueryFirstAsync(
            $"SELECT ElementName FROM Msvm_VirtualEthernetSwitch WHERE Name = '{guid}'",
            obj => obj["ElementName"]?.ToString());
        return response.HasData ? response.Data! : Properties.Resources.Common_UnknownSwitch;
    }

    private string? GetSwitchPathByName(string switchName)
    {
        using var svcForScope = WmiApi.GetVirtualSystemManagementService();
        using var searcher = new ManagementObjectSearcher(svcForScope.Scope,
            new ObjectQuery($"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{WmiApi.Escape(switchName)}'"));
        using var col = searcher.Get();
        return col.Cast<ManagementObject>().FirstOrDefault()?.Path.Path;
    }

    private ManagementObject? GetDefaultFeatureTemplate(string className)
    {
        using var svcForScope = WmiApi.GetVirtualSystemManagementService();
        using var searcher = new ManagementObjectSearcher(svcForScope.Scope,
            new ObjectQuery($"SELECT * FROM {className} WHERE InstanceID LIKE '%Default%'"));
        using var col = searcher.Get();
        return col.Cast<ManagementObject>().FirstOrDefault();
    }
}