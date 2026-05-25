using System.Diagnostics;
using System.Management;
using ExHyperV.Api;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class HyperVSwitchService
    {
        // ── VM 适配器查询 ─────────────────────────────────────────────────
        private static async Task<List<AdapterInfo>> GetVmAdaptersOnSwitchAsync(string switchGuid, string switchName)
        {
            var result = new List<AdapterInfo>();

            if (string.IsNullOrEmpty(switchGuid)) return result;

            // 查所有 Msvm_EthernetPortAllocationSettingData
            var allocResp = await WmiApi.QueryAsync(
                "SELECT * FROM Msvm_EthernetPortAllocationSettingData",
                obj => obj,
                WmiScope.HyperV);

            if (!allocResp.Success || allocResp.Data == null) return result;

            var tasks = allocResp.Data.Select(async allocObj =>
            {
                using (allocObj)
                {
                    // 检查 HostResource 是否指向目标 Switch
                    var hostResourceRaw = allocObj["HostResource"];
                    if (!(hostResourceRaw is string[] hostResource) || hostResource.Length == 0)
                        return (AdapterInfo?)null;

                    // 用正则从 HostResource 路径提取 ClassName 和 Name(GUID)
                    string hostResStr = hostResource[0];
                    var classMatch = System.Text.RegularExpressions.Regex.Match(
                        hostResStr, @":(\w+)\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!classMatch.Success) return null;
                    string className = classMatch.Groups[1].Value;

                    if (!string.Equals(className, "Msvm_VirtualEthernetSwitch", StringComparison.OrdinalIgnoreCase))
                        return null;

                    var hostGuidMatch = System.Text.RegularExpressions.Regex.Match(
                        hostResStr, @",Name=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!hostGuidMatch.Success) return null;
                    string hostGuid = hostGuidMatch.Groups[1].Value;

                    if (!string.Equals(hostGuid, switchGuid, StringComparison.OrdinalIgnoreCase))
                        return null;

                    // alloc 的 Parent 指向 Msvm_SyntheticEthernetPortSettingData
                    string parentPath = allocObj["Parent"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(parentPath)) return null;

                    var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                    try
                    {
                        using var portSetting = new ManagementObject(ms, new ManagementPath(parentPath), null);
                        portSetting.Get();

                        string rawMac = portSetting["Address"]?.ToString() ?? string.Empty;
                        string mac = Utils.FormatMac(rawMac);

                        // 从 portSetting 找所属 VM
                        var vmSettingsResp = await WmiApi.QueryRelatedAsync(
                            portSetting, "Msvm_VirtualSystemSettingData",
                            obj => obj, "Msvm_VirtualSystemSettingDataComponent");

                        if (!vmSettingsResp.Success || vmSettingsResp.Data == null || vmSettingsResp.Data.Count == 0)
                            return null;

                        string vmName = string.Empty;
                        using (var vmSetting = vmSettingsResp.Data[0])
                        {
                            var vmResp = await WmiApi.QueryRelatedAsync(
                                vmSetting, "Msvm_ComputerSystem",
                                obj => obj["ElementName"]?.ToString() ?? string.Empty,
                                "Msvm_SettingsDefineState");

                            if (vmResp.Success && vmResp.Data?.Count > 0)
                                vmName = vmResp.Data[0];
                        }

                        if (string.IsNullOrEmpty(vmName)) return null;

                        string ipAddresses = await Utils.GetVmIpAddressAsync(vmName, rawMac);
                        return (AdapterInfo?)new AdapterInfo(vmName, mac, "Unknown", ipAddresses);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GetVmAdaptersOnSwitchAsync] error: {ex.Message}");
                        return null;
                    }
                }
            });

            var taskResults = await Task.WhenAll(tasks);
            result.AddRange(taskResults.Where(a => a != null).Cast<AdapterInfo>());
            return result;
        }

        private static async Task<AdapterInfo?> GetHostAdapterOnSwitchAsync(string switchName)
        {
            string safe = WmiApi.Escape(switchName);
            var portResp = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_InternalEthernetPort WHERE ElementName = '{safe}'",
                obj => obj,
                WmiScope.HyperV);
            if (!portResp.Success || portResp.Data == null || portResp.Data.Count == 0)
                return null;
            using var port = portResp.Data[0];
            string rawMac = port["PermanentAddress"]?.ToString() ?? string.Empty;
            string mac = Utils.FormatMac(rawMac);
            string cleanMac = rawMac.ToUpper();
            string ipAddresses = string.Empty;
            var adapterResp = await WmiApi.QueryCimAsync(
                $"SELECT InterfaceIndex, Status FROM MSFT_NetAdapter WHERE PermanentAddress = '{cleanMac}'",
                obj => new
                {
                    Index = obj["InterfaceIndex"]?.ToString() ?? string.Empty,
                    Status = obj["Status"]?.ToString() ?? string.Empty
                },
                WmiScope.StdCimV2);
            string status = "Unknown";
            if (adapterResp.Success && adapterResp.Data?.Count > 0)
            {
                status = adapterResp.Data[0].Status;
                string ifIndex = adapterResp.Data[0].Index;
                if (!string.IsNullOrEmpty(ifIndex))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 2000)
                    {
                        var ipResp = await WmiApi.QueryCimAsync(
                            $"SELECT IPAddress FROM MSFT_NetIPAddress WHERE InterfaceIndex = {ifIndex}",
                            obj => obj["IPAddress"]?.ToString() ?? string.Empty,
                            WmiScope.StdCimV2);
                        if (ipResp.Success && ipResp.Data?.Count > 0)
                        {
                            ipAddresses = string.Join(",", ipResp.Data.Where(ip =>
                                !string.IsNullOrEmpty(ip) && System.Net.IPAddress.TryParse(ip, out var addr) &&
                                addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                            if (!string.IsNullOrEmpty(ipAddresses)) break;
                        }
                        await Task.Delay(200);
                    }
                }
            }
            return new AdapterInfo(
                ExHyperV.Properties.Resources.DisplayName_HostManagementOS,
                mac, status, ipAddresses);
        }
        // ══════════════════════════════════════════════════════════════════
        //  GetNetworkInfoAsync — WmiApi
        // ══════════════════════════════════════════════════════════════════
        public async Task<(List<SwitchInfo> Switches, List<PhysicalAdapterInfo> PhysicalAdapters)> GetNetworkInfoAsync()
        {
            try
            {
                var switchTask = GetSwitchListAsync();
                var adapterTask = GetPhysicalAdaptersAsync();
                await Task.WhenAll(switchTask, adapterTask);
                return (await switchTask, await adapterTask);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in GetNetworkInfoAsync: {ex}");
                throw new InvalidOperationException(Properties.Resources.Error_GetNetworkInfoFailed, ex);
            }
        }

        // ── 物理网卡列表 ─────────────────────────────────────────────────
        private static async Task<List<PhysicalAdapterInfo>> GetPhysicalAdaptersAsync()
        {
            var response = await WmiApi.QueryCimAsync(
                "SELECT InterfaceDescription FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE",
                obj => new PhysicalAdapterInfo(
                    obj["InterfaceDescription"]?.ToString() ?? string.Empty),
                WmiScope.StdCimV2);

            if (!response.Success)
            {
                Debug.WriteLine($"[NetworkService] GetPhysicalAdapters WMI error: {response.Error}");
                return new List<PhysicalAdapterInfo>();
            }

            return (response.Data ?? new List<PhysicalAdapterInfo>())
                .Where(a => !string.IsNullOrWhiteSpace(a.InterfaceDescription))
                .ToList();
        }

        // ── 虚拟交换机列表 ───────────────────────────────────────────────
        private async Task<List<SwitchInfo>> GetSwitchListAsync()
        {
            var switchObjects = await WmiApi.QueryAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitch",
                obj => obj,
                WmiScope.HyperV);

            if (!switchObjects.Success || switchObjects.Data == null)
            {
                Debug.WriteLine($"[NetworkService] GetSwitchList WMI error: {switchObjects.Error}");
                return new List<SwitchInfo>();
            }

            var tasks = switchObjects.Data.Select(async switchObj =>
            {
                using (switchObj) { return await ParseSwitchInfoAsync(switchObj); }
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(s => s != null).Cast<SwitchInfo>().ToList();
        }

        private async Task<SwitchInfo?> ParseSwitchInfoAsync(ManagementObject switchObj)
        {
            string switchName = switchObj["ElementName"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(switchName)) return null;

            string switchGuid = switchObj["Name"]?.ToString() ?? string.Empty;

            string switchId = string.Empty;
            var settingResponse = await WmiApi.QueryRelatedAsync(
                switchObj,
                "Msvm_VirtualEthernetSwitchSettingData",
                obj => obj["VirtualSystemIdentifier"]?.ToString() ?? string.Empty,
                associationClass: "Msvm_SettingsDefineState");

            if (settingResponse.Success && settingResponse.Data?.Count > 0)
                switchId = settingResponse.Data[0];

            bool hasExternal = false;
            bool hasInternal = false;
            string externalAdapterElementName = string.Empty;

            var portsResponse = await WmiApi.QueryRelatedAsync(
                switchObj, "Msvm_EthernetSwitchPort", obj => obj, "Msvm_SystemDevice");

            if (portsResponse.Success && portsResponse.Data != null)
            {
                foreach (var portObj in portsResponse.Data)
                {
                    using (portObj)
                    {
                        var portSettingsResp = await WmiApi.QueryRelatedAsync(
                            portObj, "Msvm_EthernetPortAllocationSettingData", obj => obj, "Msvm_ElementSettingData");

                        if (!portSettingsResp.Success || portSettingsResp.Data == null) continue;

                        foreach (var portSettings in portSettingsResp.Data)
                        {
                            using (portSettings)
                            {
                                var (portType, adapterName) = DeterminePortType(portSettings);
                                switch (portType)
                                {
                                    case PortConnectionKind.Internal:
                                        hasInternal = true;
                                        break;
                                    case PortConnectionKind.External:
                                        hasExternal = true;
                                        if (string.IsNullOrEmpty(externalAdapterElementName))
                                            externalAdapterElementName = adapterName;
                                        break;
                                }
                            }
                        }
                    }
                }
            }

            string switchType = hasExternal ? "External" : hasInternal ? "Internal" : "Private";
            bool allowManagementOS = hasInternal;

            string interfaceDescription = string.Empty;
            if (hasExternal && !string.IsNullOrEmpty(externalAdapterElementName))
                interfaceDescription = await ResolveInterfaceDescriptionAsync(externalAdapterElementName);

            // ICS（NAT）检测
            var icsResponse = ComApi.GetIcsSourceAdapter(switchName);
            if (icsResponse.Success && icsResponse.Data != null)
            {
                switchType = "NAT";
                // GetIcsSourceAdapter 返回适配器显示名（如 "WLAN"），转换为 InterfaceDescription
                interfaceDescription = await ResolveInterfaceDescriptionAsync(icsResponse.Data);
                if (string.IsNullOrEmpty(interfaceDescription))
                    interfaceDescription = icsResponse.Data;
            }

            return new SwitchInfo(
                switchName,
                switchType,
                allowManagementOS.ToString(),
                string.IsNullOrEmpty(switchId) ? switchGuid : switchId,
                interfaceDescription);
        }

        private enum PortConnectionKind { Nothing, Internal, External, VirtualMachine }

        private static (PortConnectionKind kind, string adapterElementName) DeterminePortType(
            ManagementObject portSettings)
        {
            if (portSettings["HostResource"] is string[] hostResource && hostResource.Length > 0)
            {
                var path = new ManagementPath(hostResource[0]);
                if (string.Equals(path.ClassName, "Msvm_ComputerSystem", StringComparison.OrdinalIgnoreCase))
                    return (PortConnectionKind.Internal, string.Empty);

                if (string.Equals(path.ClassName, "Msvm_ExternalEthernetPort", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path.ClassName, "Msvm_WiFiPort", StringComparison.OrdinalIgnoreCase))
                {
                    string elementName = string.Empty;
                    try
                    {
                        var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                        using var extPort = new ManagementObject(ms, new ManagementPath(hostResource[0]), null);
                        extPort.Get();
                        elementName = extPort["ElementName"]?.ToString() ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NetworkService] DeterminePortType ExternalPort error: {ex.Message}");
                    }
                    return (PortConnectionKind.External, elementName);
                }
            }

            string parent = portSettings["Parent"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(parent))
            {
                var parentPath = new ManagementPath(parent);
                if (string.Equals(parentPath.ClassName, "Msvm_SyntheticEthernetPortSettingData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parentPath.ClassName, "Msvm_EmulatedEthernetPortSettingData", StringComparison.OrdinalIgnoreCase))
                    return (PortConnectionKind.VirtualMachine, string.Empty);
            }

            return (PortConnectionKind.Nothing, string.Empty);
        }

        private static async Task<string> ResolveInterfaceDescriptionAsync(string elementName)
        {
            if (string.IsNullOrWhiteSpace(elementName)) return string.Empty;

            string safe = elementName.Replace("'", "\\'");
            var response = await WmiApi.QueryCimAsync(
                $"SELECT InterfaceDescription FROM MSFT_NetAdapter WHERE Name = '{safe}'",
                obj => obj["InterfaceDescription"]?.ToString() ?? string.Empty,
                WmiScope.StdCimV2);

            if (response.Success && response.Data?.Count > 0 && !string.IsNullOrEmpty(response.Data[0]))
                return response.Data[0];

            return elementName;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CreateSwitchAsync — WmiApi
        // ══════════════════════════════════════════════════════════════════
        public async Task CreateSwitchAsync(string name, string type, string? adapterDescription)
        {
            try
            {
                switch (type.ToUpper())
                {
                    case "EXTERNAL":
                        if (string.IsNullOrEmpty(adapterDescription))
                            throw new ArgumentException(Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);
                        // External Switch 不预加 Internal 端口，避免产生 vEthernet ()
                        // 用户可通过 Host Connection 开关事后开启
                        await CreateSwitchWmiAsync(name, isExternal: true, adapterDescription, allowManagementOS: true);
                        break;

                    case "NAT":
                        await CreateSwitchWmiAsync(name, isExternal: false, null, allowManagementOS: true);
                        await Task.Delay(3000);
                        await UpdateSwitchConfigurationAsync(name, "NAT", adapterDescription, true, true);
                        break;

                    case "INTERNAL":
                    default:
                        await CreateSwitchWmiAsync(name, isExternal: false, null, allowManagementOS: true);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in CreateSwitchAsync: {ex}");
                throw new InvalidOperationException(
                    string.Format(Properties.Resources.Error_CreateSwitchFailed, name, ex.Message), ex);
            }
        }

        private static async Task CreateSwitchWmiAsync(
            string name, bool isExternal, string? adapterInterfaceDescription, bool allowManagementOS)
        {
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

            // 1. 构造 SettingData XML（只有名称，其余用默认值）
            string settingXml;
            using (var settingClass = new ManagementClass(ms, new ManagementPath("Msvm_VirtualEthernetSwitchSettingData"), null))
            using (var settingInstance = settingClass.CreateInstance())
            {
                settingInstance["ElementName"] = name;
                settingXml = settingInstance.GetText(TextFormat.CimDtd20);
            }

            // 2. DefineSystem：ResourceSettings 传 null，与 PS 底层 BeginCreateVirtualSwitch 行为一致
            var defineResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                "DefineSystem",
                p =>
                {
                    p["SystemSettings"] = settingXml;
                    p["ResourceSettings"] = null;
                    p["ReferenceConfiguration"] = null;
                },
                WmiScope.HyperV);

            if (!defineResult.Success)
                throw new InvalidOperationException(defineResult.Error);

            // 3. 创建后再绑端口（等价于 ConfigureConnections -> AddConnections）
            using var switchObj = await GetSwitchObjectAsync(name);
            string settingPath = await GetSwitchSettingPathAsync(switchObj);

            var resourceXmls = new List<string>();

            if (isExternal && !string.IsNullOrEmpty(adapterInterfaceDescription))
            {
                string extPortPath = await FindExternalEthernetPortPathAsync(adapterInterfaceDescription);
                if (string.IsNullOrEmpty(extPortPath))
                    throw new InvalidOperationException(
                        Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);

                using var extAllocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var extAllocInstance = extAllocClass.CreateInstance();
                extAllocInstance["HostResource"] = new string[] { extPortPath };
                resourceXmls.Add(extAllocInstance.GetText(TextFormat.CimDtd20));
            }

            if (allowManagementOS || !isExternal)
            {
                string hostSystemPath = GetHostComputerSystemPath(ms);
                using var intAllocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var intAllocInstance = intAllocClass.CreateInstance();
                intAllocInstance["ElementName"] = name;
                intAllocInstance["HostResource"] = new string[] { hostSystemPath };
                resourceXmls.Add(intAllocInstance.GetText(TextFormat.CimDtd20));
            }

            if (resourceXmls.Count > 0)
            {
                var addResult = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "AddResourceSettings",
                    p =>
                    {
                        p["AffectedConfiguration"] = settingPath;
                        p["ResourceSettings"] = resourceXmls.ToArray();
                    },
                    WmiScope.HyperV);

                if (!addResult.Success)
                    throw new InvalidOperationException(addResult.Error);
            }
            else
            {
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  DeleteSwitchAsync — WmiApi + ComApi
        // ══════════════════════════════════════════════════════════════════
        public async Task DeleteSwitchAsync(string switchName)
        {
            try
            {
                // ICS 清理加超时保护，避免桥接状态下枚举网络连接卡死
                var icsTask = ComApi.DisableAllIcsSharingAsync();
                await Task.WhenAny(icsTask, Task.Delay(5000));
                if (!icsTask.IsCompleted)
                    Debug.WriteLine("[DeleteSwitch] DisableAllIcsSharing timeout, continuing anyway.");

                var switchResp = await WmiApi.QueryAsync(
                    $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{WmiApi.Escape(switchName)}'",
                    obj => obj.Path.Path,
                    WmiScope.HyperV);

                if (!switchResp.Success || switchResp.Data == null || switchResp.Data.Count == 0)
                    throw new InvalidOperationException($"Switch '{switchName}' not found.");

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "DestroySystem",
                    p => p["AffectedSystem"] = switchResp.Data[0],
                    WmiScope.HyperV);

                if (!result.Success)
                    throw new InvalidOperationException(result.Error);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in DeleteSwitchAsync: {ex}");
                throw new InvalidOperationException(
                    string.Format(Properties.Resources.Error_DeleteSwitchFailed, switchName), ex);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  UpdateSwitchConfigurationAsync — WmiApi + ComApi
        // ══════════════════════════════════════════════════════════════════
        public async Task UpdateSwitchConfigurationAsync(
            string switchName, string mode, string? adapterDescription,
            bool allowManagementOS, bool enableDhcp)
        {
            switch (mode)
            {
                case "Bridge":
                    await SetBridgeModeAsync(switchName, adapterDescription, allowManagementOS);
                    break;
                case "NAT":
                    await SetNatModeAsync(switchName, adapterDescription);
                    break;
                case "Isolated":
                    await SetIsolatedModeAsync(switchName, allowManagementOS);
                    break;
                default:
                    throw new ArgumentException(
                        string.Format(Properties.Resources.Utils_UnknownNetMode, mode));
            }
        }

        private async Task SetBridgeModeAsync(string switchName, string? adapterDescription, bool allowManagementOS = true)
        {
            if (string.IsNullOrEmpty(adapterDescription))
                throw new ArgumentException("Bridge mode requires a physical adapter.");

            await ComApi.DisableAllIcsSharingAsync();

            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchName);

            await RemoveInternalPortsAsync(switchObj, ms);

            string extPortPath = await FindExternalEthernetPortPathAsync(adapterDescription);
            if (string.IsNullOrEmpty(extPortPath))
                throw new InvalidOperationException(
                    Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);

            string settingPath = await GetSwitchSettingPathAsync(switchObj);

            // 构造端口列表：External 端口必加，Internal 端口按 allowManagementOS 决定
            var resourceXmls = new List<string>();

            using var extAllocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
            using var extAllocInstance = extAllocClass.CreateInstance();
            extAllocInstance["HostResource"] = new string[] { extPortPath };
            resourceXmls.Add(extAllocInstance.GetText(TextFormat.CimDtd20));

            if (allowManagementOS)
            {
                string hostSystemPath = GetHostComputerSystemPath(ms);
                using var intAllocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var intAllocInstance = intAllocClass.CreateInstance();
                intAllocInstance["ElementName"] = switchName;
                intAllocInstance["HostResource"] = new string[] { hostSystemPath };
                resourceXmls.Add(intAllocInstance.GetText(TextFormat.CimDtd20));
            }

            var result = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                "AddResourceSettings",
                p =>
                {
                    p["AffectedConfiguration"] = settingPath;
                    p["ResourceSettings"] = resourceXmls.ToArray();
                },
                WmiScope.HyperV);

            if (!result.Success) throw new InvalidOperationException(result.Error);
        }

        private async Task SetNatModeAsync(string switchName, string? adapterDescription)
        {
            if (string.IsNullOrEmpty(adapterDescription))
                throw new ArgumentException("NAT mode requires a physical adapter.");

            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchName);
            await EnsureInternalModeAsync(switchObj, ms, switchName);

            await ComApi.DisableAllIcsSharingAsync();

            string vEthernetName = $"vEthernet ({switchName})";
            string physicalAdapterName = await ResolveAdapterNameAsync(adapterDescription);

            var icsResult = await ComApi.EnableIcsSharingAsync(physicalAdapterName, vEthernetName);
            if (!icsResult.Success) throw new InvalidOperationException(icsResult.Error);
        }

        private async Task SetIsolatedModeAsync(string switchName, bool allowManagementOS)
        {
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchName);
            await EnsureInternalModeAsync(switchObj, ms, switchName);
            await ComApi.DisableAllIcsSharingAsync();

            bool hasInternal = await HasInternalPortAsync(switchObj);

            if (allowManagementOS && !hasInternal)
            {
                string hostSystemPath = GetHostComputerSystemPath(ms);
                string settingPath = await GetSwitchSettingPathAsync(switchObj);

                using var allocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var allocInstance = allocClass.CreateInstance();
                allocInstance["ElementName"] = switchObj["ElementName"]?.ToString() ?? string.Empty;
                allocInstance["HostResource"] = new string[] { hostSystemPath };

                var addResult = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "AddResourceSettings",
                    p =>
                    {
                        p["AffectedConfiguration"] = settingPath;
                        p["ResourceSettings"] = new string[] { allocInstance.GetText(TextFormat.CimDtd20) };
                    },
                    WmiScope.HyperV);

                if (!addResult.Success) throw new InvalidOperationException(addResult.Error);
            }
            else if (!allowManagementOS && hasInternal)
            {
                await RemoveInternalPortsAsync(switchObj, ms);
            }
        }

        // ── Switch 操作辅助 ───────────────────────────────────────────────

        private static async Task<ManagementObject> GetSwitchObjectAsync(string switchName)
        {
            var resp = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{WmiApi.Escape(switchName)}'",
                obj => obj,
                WmiScope.HyperV);

            if (!resp.Success || resp.Data == null || resp.Data.Count == 0)
                throw new InvalidOperationException($"Switch '{switchName}' not found.");

            return resp.Data[0];
        }

        private static async Task<string> GetSwitchSettingPathAsync(ManagementObject switchObj)
        {
            var resp = await WmiApi.QueryRelatedAsync(
                switchObj, "Msvm_VirtualEthernetSwitchSettingData",
                obj => obj.Path.Path, "Msvm_SettingsDefineState");

            if (!resp.Success || resp.Data == null || resp.Data.Count == 0)
                throw new InvalidOperationException("Cannot find switch SettingData.");

            return resp.Data[0];
        }

        private static async Task RemoveInternalPortsAsync(ManagementObject switchObj, ManagementScope ms)
        {
            var portsResp = await WmiApi.QueryRelatedAsync(
                switchObj, "Msvm_EthernetSwitchPort", obj => obj, "Msvm_SystemDevice");

            if (!portsResp.Success || portsResp.Data == null) return;

            var internalPortPaths = new List<string>();
            foreach (var port in portsResp.Data)
            {
                using (port)
                {
                    var settingsResp = await WmiApi.QueryRelatedAsync(
                        port, "Msvm_EthernetPortAllocationSettingData", obj => obj, "Msvm_ElementSettingData");

                    if (!settingsResp.Success || settingsResp.Data == null) continue;
                    foreach (var ps in settingsResp.Data)
                    {
                        using (ps)
                        {
                            var (kind, _) = DeterminePortType(ps);
                            if (kind == PortConnectionKind.Internal)
                                internalPortPaths.Add(ps.Path.Path);
                        }
                    }
                }
            }

            if (internalPortPaths.Count == 0) return;

            var removeResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                "RemoveResourceSettings",
                p => p["ResourceSettings"] = internalPortPaths.ToArray(),
                WmiScope.HyperV);

            if (!removeResult.Success)
                Debug.WriteLine($"[NetworkService] RemoveInternalPorts warning: {removeResult.Error}");
        }

        private static async Task<bool> HasInternalPortAsync(ManagementObject switchObj)
        {
            var portsResp = await WmiApi.QueryRelatedAsync(
                switchObj, "Msvm_EthernetSwitchPort", obj => obj, "Msvm_SystemDevice");

            if (!portsResp.Success || portsResp.Data == null) return false;

            foreach (var port in portsResp.Data)
            {
                using (port)
                {
                    var settingsResp = await WmiApi.QueryRelatedAsync(
                        port, "Msvm_EthernetPortAllocationSettingData", obj => obj, "Msvm_ElementSettingData");

                    if (!settingsResp.Success || settingsResp.Data == null) continue;
                    foreach (var ps in settingsResp.Data)
                    {
                        using (ps)
                        {
                            var (kind, _) = DeterminePortType(ps);
                            if (kind == PortConnectionKind.Internal) return true;
                        }
                    }
                }
            }
            return false;
        }

        private static async Task EnsureInternalModeAsync(ManagementObject switchObj, ManagementScope ms, string switchName = "")
        {
            var portsResp = await WmiApi.QueryRelatedAsync(
                switchObj, "Msvm_EthernetSwitchPort", obj => obj, "Msvm_SystemDevice");

            if (!portsResp.Success || portsResp.Data == null) return;

            var externalPortPaths = new List<string>();
            bool hasInternal = false;

            foreach (var port in portsResp.Data)
            {
                using (port)
                {
                    var settingsResp = await WmiApi.QueryRelatedAsync(
                        port, "Msvm_EthernetPortAllocationSettingData", obj => obj, "Msvm_ElementSettingData");

                    if (!settingsResp.Success || settingsResp.Data == null) continue;
                    foreach (var ps in settingsResp.Data)
                    {
                        using (ps)
                        {
                            var (kind, _) = DeterminePortType(ps);
                            if (kind == PortConnectionKind.External) externalPortPaths.Add(ps.Path.Path);
                            if (kind == PortConnectionKind.Internal) hasInternal = true;
                        }
                    }
                }
            }

            if (externalPortPaths.Count > 0)
            {
                await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "RemoveResourceSettings",
                    p => p["ResourceSettings"] = externalPortPaths.ToArray(),
                    WmiScope.HyperV);
            }

            if (!hasInternal)
            {
                string hostSystemPath = GetHostComputerSystemPath(ms);
                string settingPath = await GetSwitchSettingPathAsync(switchObj);

                using var allocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var allocInstance = allocClass.CreateInstance();
                allocInstance["ElementName"] = switchObj["ElementName"]?.ToString() ?? string.Empty;
                allocInstance["HostResource"] = new string[] { hostSystemPath };

                await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "AddResourceSettings",
                    p =>
                    {
                        p["AffectedConfiguration"] = settingPath;
                        p["ResourceSettings"] = new string[] { allocInstance.GetText(TextFormat.CimDtd20) };
                    },
                    WmiScope.HyperV);
            }
        }

        private static async Task<string> FindExternalEthernetPortPathAsync(string interfaceDescription)
        {
            // InterfaceDescription 对应 WMI 里的 Name 字段
            // 有线网卡在 Msvm_ExternalEthernetPort，Wi-Fi 在 Msvm_WiFiPort，两个都要查
            string safe = interfaceDescription.Replace("'", "\\'");

            var ethResp = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_ExternalEthernetPort WHERE Name = '{safe}'",
                obj => obj.Path.Path,
                WmiScope.HyperV);

            if (ethResp.Success && ethResp.Data?.Count > 0 && !string.IsNullOrEmpty(ethResp.Data[0]))
                return ethResp.Data[0];

            var wifiResp = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_WiFiPort WHERE Name = '{safe}'",
                obj => obj.Path.Path,
                WmiScope.HyperV);

            string wifiResult = (wifiResp.Success && wifiResp.Data?.Count > 0) ? wifiResp.Data[0] : string.Empty;
            return wifiResult;
        }

        private static string GetHostComputerSystemPath(ManagementScope ms)
        {
            // 宿主机的 Msvm_ComputerSystem 用 Name = 主机名 查询（非虚拟机）
            // Caption = "Hosting Computer System" 是另一个可靠的过滤条件
            string hostName = System.Environment.MachineName;
            using var searcher = new ManagementObjectSearcher(ms,
                new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{hostName}'"));
            using var col = searcher.Get();
            var host = col.Cast<ManagementObject>().FirstOrDefault();
            return host?.Path.Path ?? string.Empty;
        }

        private static async Task<string> ResolveAdapterNameAsync(string interfaceDescription)
        {
            string safe = interfaceDescription.Replace("'", "\\'");
            var resp = await WmiApi.QueryCimAsync(
                $"SELECT Name FROM MSFT_NetAdapter WHERE InterfaceDescription = '{safe}'",
                obj => obj["Name"]?.ToString() ?? string.Empty,
                WmiScope.StdCimV2);

            return (resp.Success && resp.Data?.Count > 0 && !string.IsNullOrEmpty(resp.Data[0]))
                ? resp.Data[0] : interfaceDescription;
        }

        // ══════════════════════════════════════════════════════════════════
        //  GetFullSwitchNetworkStateAsync — WmiApi + CimApi
        // ══════════════════════════════════════════════════════════════════
        public async Task<List<AdapterInfo>> GetFullSwitchNetworkStateAsync(string switchName)
        {
            try
            {
                var allAdapters = new List<AdapterInfo>();

                // 1. 找到 Switch 对象路径，用于过滤端口
                string safe = WmiApi.Escape(switchName);
                var switchResp = await WmiApi.QueryAsync(
                    $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{safe}'",
                    obj => obj.Path.Path,
                    WmiScope.HyperV);

                if (!switchResp.Success || switchResp.Data == null || switchResp.Data.Count == 0)
                    return allAdapters;

                string switchPath = switchResp.Data[0];

                // 从路径提取 Switch GUID（Name 字段）
                var guidMatch = System.Text.RegularExpressions.Regex.Match(
                    switchPath, @",Name=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string switchGuid = guidMatch.Success ? guidMatch.Groups[1].Value : string.Empty;

                // 2. 查所有 VM 的 Msvm_SyntheticEthernetPort，过滤连接到此 Switch 的
                var vmAdapters = await GetVmAdaptersOnSwitchAsync(switchGuid, switchName);
                allAdapters.AddRange(vmAdapters);

                // 3. 查 ManagementOS 的 Internal 端口
                var hostAdapter = await GetHostAdapterOnSwitchAsync(switchName);
                if (hostAdapter != null)
                    allAdapters.Add(hostAdapter);

                return allAdapters;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting full network state for switch '{switchName}': {ex.Message}");
                return new List<AdapterInfo>();
            }
        }
    }
}