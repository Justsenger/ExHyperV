using System.Diagnostics;
using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public static class HyperVSwitchService
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
                        string mac = MacAddress.Format(rawMac);

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

                        string ipAddresses = await VmIpService.Lookup(vmName, rawMac);
                        return (AdapterInfo?)new AdapterInfo { Name = vmName, MacAddress = mac, IpAddress = Ipv4.SelectBest(ipAddresses) };
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
            string mac = MacAddress.Format(rawMac);
            string cleanMac = rawMac.ToUpper();
            string ipAddresses = string.Empty;
            var adapterResp = await WmiApi.QueryCimAsync(
                $"SELECT InterfaceIndex FROM MSFT_NetAdapter WHERE PermanentAddress = '{cleanMac}'",
                obj => obj["InterfaceIndex"]?.ToString() ?? string.Empty,
                WmiScope.StdCimV2);
            if (adapterResp.Success && adapterResp.Data?.Count > 0)
            {
                string ifIndex = adapterResp.Data[0];
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
            return new AdapterInfo
            {
                Name = Properties.Resources.DisplayName_HostManagementOS,
                MacAddress = mac,
                IpAddress = Ipv4.SelectBest(ipAddresses)
            };
        }
        // ══════════════════════════════════════════════════════════════════
        //  GetNetworkInfoAsync — WmiApi
        // ══════════════════════════════════════════════════════════════════
        public static async Task<(List<SwitchInfo> Switches, List<string> PhysicalAdapters)> GetNetworkInfoAsync()
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
        private static async Task<List<string>> GetPhysicalAdaptersAsync()
        {
            // 同一物理网卡(尤其蜂窝模组/WiFi)会暴露多个共用同一 InterfaceDescription 的 NDIS 接口,
            // 含 InterfaceOperationalStatus=6(NotPresent)的占位接口;它们 ConnectorPresent 也都是 TRUE
            // (同一块物理硬件),只按 ConnectorPresent 过滤会重复 N 次。实测:arm64/Surface 5G 模组 11+WiFi 4 = 15
            // 个接口收敛为 2 块物理设备;x64 单接口网卡分组后不变(零影响)。故按物理设备(PnPDeviceID)去重、
            // 每块取 OperStatus 最优(Up=1 < Down=2 < NotPresent=6)的接口描述。
            var response = await WmiApi.QueryCimAsync(
                "SELECT InterfaceDescription, PnPDeviceID, InterfaceOperationalStatus FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE",
                obj => (
                    Desc: obj["InterfaceDescription"]?.ToString() ?? string.Empty,
                    Pnp: obj["PnPDeviceID"]?.ToString() ?? string.Empty,
                    Oper: Convert.ToInt32(obj["InterfaceOperationalStatus"] ?? 0)
                ),
                WmiScope.StdCimV2);

            if (!response.Success)
            {
                Debug.WriteLine($"[NetworkService] GetPhysicalAdapters WMI error: {response.Error}");
                return new List<string>();
            }

            return (response.Data ?? new())
                .Where(a => !string.IsNullOrWhiteSpace(a.Desc) && !string.IsNullOrWhiteSpace(a.Pnp))
                .GroupBy(a => a.Pnp)
                .Select(g => g.OrderBy(a => a.Oper).First().Desc)
                .ToList();
        }

        /// <summary>
        /// 可桥接的物理网卡(外部/桥接交换机用):Hyper-V 认的 Msvm_ExternalEthernetPort(有线)+
        /// Msvm_WiFiPort(WiFi)的 Name,与真实物理网卡(GetPhysicalAdapters)的交集。
        /// 蜂窝/WWAN 两类端口皆无(点对点接口不支持二层桥接)→ 自然排除;调试网卡等非物理项也被交集滤掉。
        /// </summary>
        public static async Task<List<string>> GetBridgeableAdaptersAsync()
        {
            var ethResp = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_ExternalEthernetPort",
                obj => obj["Name"]?.ToString() ?? string.Empty, WmiScope.HyperV);
            var wifiResp = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_WiFiPort",
                obj => obj["Name"]?.ToString() ?? string.Empty, WmiScope.HyperV);

            var bridgeable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ethResp.Success && ethResp.Data != null)
                foreach (var n in ethResp.Data) if (!string.IsNullOrEmpty(n)) bridgeable.Add(n);
            if (wifiResp.Success && wifiResp.Data != null)
                foreach (var n in wifiResp.Data) if (!string.IsNullOrEmpty(n)) bridgeable.Add(n);

            var physical = await GetPhysicalAdaptersAsync();
            return physical.Where(p => bridgeable.Contains(p)).ToList();
        }

        // ── 虚拟交换机列表 ───────────────────────────────────────────────
        private static async Task<List<SwitchInfo>> GetSwitchListAsync()
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

        private static async Task<SwitchInfo?> ParseSwitchInfoAsync(ManagementObject switchObj)
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

            SwitchMode switchType = hasExternal ? SwitchMode.Bridge : SwitchMode.Isolated;
            bool allowManagementOS = hasInternal;

            string interfaceDescription = string.Empty;
            if (hasExternal && !string.IsNullOrEmpty(externalAdapterElementName))
                interfaceDescription = await ResolveInterfaceDescriptionAsync(externalAdapterElementName);

            // ICS（NAT）检测
            var icsResponse = ComApi.GetIcsSourceAdapter(switchName);
            if (icsResponse.Success && icsResponse.Data != null)
            {
                switchType = SwitchMode.NAT;
                // GetIcsSourceAdapter 返回适配器显示名（如 "WLAN"），转换为 InterfaceDescription
                interfaceDescription = await ResolveInterfaceDescriptionAsync(icsResponse.Data);
                if (string.IsNullOrEmpty(interfaceDescription))
                    interfaceDescription = icsResponse.Data;
            }

            return new SwitchInfo
            {
                SwitchName = switchName,
                SwitchType = switchType,
                AllowManagementOS = allowManagementOS,
                Id = string.IsNullOrEmpty(switchId) ? switchGuid : switchId,
                NetAdapterInterfaceDescription = interfaceDescription
            };
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
        public static async Task CreateSwitchAsync(string name, SwitchMode mode, string? adapterDescription)
        {
            try
            {
                switch (mode)
                {
                    case SwitchMode.Bridge:
                        if (string.IsNullOrEmpty(adapterDescription))
                            throw new ArgumentException(Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);
                        // 桥接：外部端口 + 主机管理端口都加，宿主与虚拟机一同接入该外部交换机
                        // (会生成 vEthernet (交换机名) 主机网卡；桥接下主机连接固定开启，无单独开关)
                        await CreateSwitchWmiAsync(name, isExternal: true, adapterDescription, allowManagementOS: true);
                        break;

                    case SwitchMode.NAT:
                        await CreateSwitchWmiAsync(name, isExternal: false, null, allowManagementOS: true);
                        await Task.Delay(3000);
                        await UpdateSwitchConfigurationAsync(name, SwitchMode.NAT, adapterDescription, true);
                        break;

                    case SwitchMode.Isolated:
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

        // 整体放进 Task.Run：首个 await 前的同步 WMI(GetManagementScope/ManagementClass.CreateInstance)及
        // GetHostComputerSystemPath(searcher.Get) 都在调用线程；新建交换机从 UI 线程 await 调到会卡。
        private static Task CreateSwitchWmiAsync(
            string name, bool isExternal, string? adapterInterfaceDescription, bool allowManagementOS) => Task.Run(async () =>
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
        });

        // ══════════════════════════════════════════════════════════════════
        //  DeleteSwitchAsync — WmiApi + ComApi
        // ══════════════════════════════════════════════════════════════════
        public static async Task DeleteSwitchAsync(string switchName)
        {
            try
            {
                // 仅当被删交换机自身配了 ICS(NAT)时才清理；超时保护避免枚举网络连接卡死
                // (无条件 DisableAll 会连带关掉别的 NAT 交换机——ICS 全局只有一份共享)
                var icsTask = DisableIcsIfPresentAsync(switchName);
                await Task.WhenAny(icsTask, Task.Delay(5000));
                if (!icsTask.IsCompleted)
                    Debug.WriteLine("[DeleteSwitch] ICS cleanup timeout, continuing anyway.");

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
        public static async Task UpdateSwitchConfigurationAsync(
            string switchName, SwitchMode mode, string? adapterDescription,
            bool allowManagementOS)
        {
            switch (mode)
            {
                case SwitchMode.Bridge:
                    await SetBridgeModeAsync(switchName, adapterDescription, allowManagementOS);
                    break;
                case SwitchMode.NAT:
                    await SetNatModeAsync(switchName, adapterDescription);
                    break;
                case SwitchMode.Isolated:
                    await SetIsolatedModeAsync(switchName, allowManagementOS);
                    break;
                default:
                    throw new ArgumentException(
                        string.Format(Properties.Resources.Utils_UnknownNetMode, mode));
            }
        }

        // 仅当本交换机当前确实配了 ICS(即它就是那台 NAT 交换机)时才清理。
        // ICS 全局只有一份共享,无条件 DisableAll 会把别的 NAT 交换机也一并关掉;
        // 判断复用工具自身的 NAT 检测函数 GetIcsSourceAdapter。
        private static async Task DisableIcsIfPresentAsync(string switchName)
        {
            var ics = await ComApi.GetIcsSourceAdapterAsync(switchName);
            if (ics.Success && ics.Data != null)
                await ComApi.DisableAllIcsSharingAsync();
        }

        private static async Task SetBridgeModeAsync(string switchName, string? adapterDescription, bool allowManagementOS = true)
        {
            if (string.IsNullOrEmpty(adapterDescription))
                throw new ArgumentException("Bridge mode requires a physical adapter.");

            await DisableIcsIfPresentAsync(switchName);

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

        private static async Task SetNatModeAsync(string switchName, string? adapterDescription)
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

        private static async Task SetIsolatedModeAsync(string switchName, bool allowManagementOS)
        {
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchName);
            await EnsureInternalModeAsync(switchObj, ms, switchName);
            await DisableIcsIfPresentAsync(switchName);

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
            string hostName = WmiApi.Escape(System.Environment.MachineName);
            using var searcher = new ManagementObjectSearcher(ms,
                new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{hostName}'"));
            using var col = searcher.Get();
            var host = col.Cast<ManagementObject>().FirstOrDefault();
            return host?.Path.Path ?? string.Empty;
        }

        private static async Task<string> ResolveAdapterNameAsync(string interfaceDescription)
        {
            var resp = await WmiApi.QueryCimAsync(
                $"SELECT Name, InterfaceOperationalStatus FROM MSFT_NetAdapter WHERE InterfaceDescription = '{WmiApi.Escape(interfaceDescription)}'",
                obj => (
                    Name: obj["Name"]?.ToString() ?? string.Empty,
                    Oper: Convert.ToInt32(obj["InterfaceOperationalStatus"] ?? 0)
                ),
                WmiScope.StdCimV2);

            // 同描述名多接口(蜂窝/WiFi)会返回多行,取 OperStatus 最优(Up=1 < Down=2 < NotPresent=6)的
            // 连接态接口名——否则取到 Data[0] 可能是 NotPresent 幽灵接口(HNetCfg 里不存在,ICS EnableSharing 会失败)。
            if (resp.Success && resp.Data != null)
            {
                string best = resp.Data
                    .Where(a => !string.IsNullOrEmpty(a.Name))
                    .OrderBy(a => a.Oper)
                    .Select(a => a.Name)
                    .FirstOrDefault() ?? string.Empty;
                if (!string.IsNullOrEmpty(best)) return best;
            }
            return interfaceDescription;
        }

        // ══════════════════════════════════════════════════════════════════
        //  GetFullSwitchNetworkStateAsync — WmiApi + CimApi
        // ══════════════════════════════════════════════════════════════════
        public static async Task<List<AdapterInfo>> GetFullSwitchNetworkStateAsync(string switchName)
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