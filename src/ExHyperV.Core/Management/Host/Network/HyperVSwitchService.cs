using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>读取和配置本机 Hyper-V 虚拟交换机及 ICS 上游共享关系。</summary>
    /// <remarks>变更需要 Hyper-V 与网络管理权限，并可能短暂中断宿主/VM 网络。交换机及 ICS 配置在同一宿主跨 Core 进程串行；不阻止外部工具修改。底层结果未知时停止后续修改及恢复，调用方必须先核对实际状态。</remarks>
    public static partial class HyperVSwitchService
    {

        // ── VM 适配器查询 ─────────────────────────────────────────────────
        private static async Task<List<SwitchEndpointInfo>> GetVmAdaptersOnSwitchAsync(string switchGuid, string switchName)
        {
            var result = new List<SwitchEndpointInfo>();

            if (string.IsNullOrEmpty(switchGuid)) return result;

            // 查所有 Msvm_EthernetPortAllocationSettingData
            var allocResp = await WmiApi.QueryAsync(
                "SELECT * FROM Msvm_EthernetPortAllocationSettingData",
                obj => new ManagementObject(obj.Scope, obj.Path, null),
                WmiScope.HyperV);

            if (!allocResp.Success || allocResp.Data == null) return result;

            var tasks = allocResp.Data.Select(async allocObj =>
            {
                using (allocObj)
                {
                    var hostResourceRaw = allocObj["HostResource"];
                    if (!(hostResourceRaw is string[] hostResource) || hostResource.Length == 0)
                        return (SwitchEndpointInfo?)null;

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
                            obj => new ManagementObject(obj.Scope, obj.Path, null), "Msvm_VirtualSystemSettingDataComponent");

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

                        string ipAddresses = await VmIpService.LookupAsync(vmName, rawMac);
                        return (SwitchEndpointInfo?)new SwitchEndpointInfo { Name = vmName, MacAddress = mac, IpAddress = Ipv4.SelectBest(ipAddresses) };
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GetVmAdaptersOnSwitchAsync] error: {ex.Message}");
                        return null;
                    }
                }
            });

            var taskResults = await Task.WhenAll(tasks);
            result.AddRange(taskResults.Where(a => a != null).Cast<SwitchEndpointInfo>());
            return result;
        }

        private static async Task<SwitchEndpointInfo?> GetHostAdapterOnSwitchAsync(string switchName)
        {
            string safe = WmiApi.Escape(switchName);
            var portResp = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_InternalEthernetPort WHERE ElementName = '{safe}'",
                obj => new ManagementObject(obj.Scope, obj.Path, null),
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
            return new SwitchEndpointInfo
            {
                Name = Properties.Resources.DisplayName_HostManagementOS,
                MacAddress = mac,
                IpAddress = Ipv4.SelectBest(ipAddresses)
            };
        }
        // ══════════════════════════════════════════════════════════════════
        //  GetNetworkInfoAsync — WmiApi
        // ══════════════════════════════════════════════════════════════════
        /// <summary>读取交换机、连接模式和可用物理上游网卡快照。</summary>
        /// <returns>交换机和物理网卡列表；网卡等读取失败可抛出异常，交换机主查询失败当前返回空交换机列表，不能与确实没有交换机区分。逐交换机识别问题见 StateError。</returns>
        public static Task<(List<SwitchInfo> Switches, List<SwitchUpstreamAdapter> PhysicalAdapters)> GetNetworkInfoAsync()
            => ReadNetworkInfoAsync(GetPhysicalAdaptersAsync, ComApi.GetConnectionsAsync,
                readContext => GetSwitchListAsync(readContext: readContext));

        // One display refresh owns these DTO reads. Configuration transactions do not use this context.
        internal sealed class SwitchDisplayReadContext(
            Task<List<SwitchUpstreamAdapter>> physicalAdapters,
            Func<Task<ApiResponse<List<IcsConnection>>>> readConnections)
        {
            internal Task<List<SwitchUpstreamAdapter>> PhysicalAdapters { get; } = physicalAdapters;
            internal Lazy<Task<ApiResponse<List<IcsConnection>>>> Connections { get; } = new(readConnections);
        }

        internal static Task<(List<SwitchInfo> Switches, List<SwitchUpstreamAdapter> PhysicalAdapters)> ReadNetworkInfoAsync(
            Func<Task<List<SwitchUpstreamAdapter>>> readAdapters,
            Func<Task<ApiResponse<List<IcsConnection>>>> readConnections,
            Func<SwitchDisplayReadContext, Task<List<SwitchInfo>>> readSwitches)
            => Task.Run(async () =>
            {
                try
                {
                    // Start every reader on a worker: ManagementObject property access can issue synchronous WMI.
                    var adapterTask = readAdapters();
                    var readContext = new SwitchDisplayReadContext(adapterTask, readConnections);
                    var switchTask = readSwitches(readContext);
                    await Task.WhenAll(switchTask, adapterTask).ConfigureAwait(false);
                    return (await switchTask.ConfigureAwait(false), await adapterTask.ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in GetNetworkInfoAsync: {ex}");
                    throw;
                }
            });

        // 保留实际连接 GUID，按 PnP 身份关联物理设备；NDIS 占位接口不参与 ICS 连接选择。
        private static async Task<List<SwitchUpstreamAdapter>> GetPhysicalAdaptersAsync()
        {
            var physical = await WmiApi.QueryCimAsync(
                "SELECT PnPDeviceID FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE",
                p => p["PnPDeviceID"]?.ToString() ?? "", WmiScope.StdCimV2);
            RequireSuccess(physical);
            var ids = new HashSet<string>(physical.Data ?? new(), StringComparer.OrdinalIgnoreCase);
            var connections = await WmiApi.QueryAsync(
                "SELECT GUID, PNPDeviceID, Name, NetConnectionID, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL",
                p => new SwitchUpstreamAdapter(Guid.Parse(p["GUID"].ToString()!), p["PNPDeviceID"]?.ToString() ?? "",
                    p["Name"]?.ToString() ?? "", p["NetConnectionID"]?.ToString() ?? "", "",
                    Convert.ToInt32(p["NetConnectionStatus"] ?? 0) == 2), WmiScope.CimV2);
            RequireSuccess(connections);
            var ports = new Dictionary<Guid, string>();
            foreach (var cls in new[] { "Msvm_ExternalEthernetPort", "Msvm_WiFiPort" })
            {
                var response = await WmiApi.QueryAsync($"SELECT * FROM {cls}", p =>
                    (Id: Guid.Parse(p["DeviceID"].ToString()!.Replace("Microsoft:", "", StringComparison.OrdinalIgnoreCase)), Path: p.Path.Path), WmiScope.HyperV);
                RequireSuccess(response);
                foreach (var port in response.Data ?? new()) ports[port.Id] = port.Path;
            }
            return (connections.Data ?? new()).Where(a => ids.Contains(a.DeviceId))
                .Select(a => a with { ExternalPortPath = ports.GetValueOrDefault(a.ConnectionId, "") }).ToList();
        }

        /// <summary>筛选已找到 Hyper-V 外部端口的宿主物理网卡。</summary>
        /// <returns>桥接候选列表；未在此排除其他交换机占用或断开链路，提交前仍需校验。</returns>
        public static async Task<List<SwitchUpstreamAdapter>> GetBridgeableAdaptersAsync()
            => (await GetPhysicalAdaptersAsync()).Where(a => !string.IsNullOrEmpty(a.ExternalPortPath)).ToList();

        // ── 虚拟交换机列表 ───────────────────────────────────────────────
        private static async Task<List<SwitchInfo>> GetSwitchListAsync(
            bool requireSuccess = false, SwitchDisplayReadContext? readContext = null)
        {
            var switchObjects = await WmiApi.QueryAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitch",
                obj => new ManagementObject(obj.Scope, obj.Path, null),
                WmiScope.HyperV);

            if (!switchObjects.Success || switchObjects.Data == null)
            {
                Debug.WriteLine($"[NetworkService] GetSwitchList WMI error: {switchObjects.Error}");
                if (requireSuccess) throw new InvalidOperationException(switchObjects.Error);
                return new List<SwitchInfo>();
            }

            var tasks = switchObjects.Data.Select(async switchObj =>
            {
                using (switchObj)
                {
                    try { return await ParseSwitchInfoAsync(switchObj, readContext); }
                    catch (SwitchPortReadException ex)
                    {
                        // A runtime port can outlive its configuration association.
                        // Keep this switch visible without losing every healthy switch.
                        // Configuration transactions still use the strict reader below.
                        Debug.WriteLine(ex);
                        return new SwitchInfo
                        {
                            SwitchName = switchObj["ElementName"]?.ToString() ?? string.Empty,
                            Id = switchObj["Name"]?.ToString() ?? string.Empty,
                            StateError = Properties.Resources.Network_PortStateUnavailable
                        };
                    }
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(s => s != null).Cast<SwitchInfo>().ToList();
        }

        private static async Task<SwitchInfo?> ParseSwitchInfoAsync(
            ManagementObject switchObj, SwitchDisplayReadContext? readContext)
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

            bool hasInternal = false;
            Guid externalAdapterId = Guid.Empty;

            var ports = await GetPortAllocationsAsync(switchObj);
            hasInternal = ports.Any(p => p.Kind == PortConnectionKind.Internal);
            var externalPorts = ports.Where(p => p.Kind == PortConnectionKind.External).ToList();
            if (externalPorts.Count == 1)
            {
                using var external = new ManagementObject(switchObj.Scope, new ManagementPath(externalPorts[0].HostResource), null);
                external.Get();
                Guid.TryParse(external["DeviceID"]?.ToString()?.Replace("Microsoft:", "", StringComparison.OrdinalIgnoreCase), out externalAdapterId);
            }

            var networkState = await ReadSwitchNetworkStateAsync(externalPorts.Count, externalAdapterId,
                () => GetHostConnectionIdAsync(Guid.Parse(switchGuid)), readContext);

            return new SwitchInfo
            {
                SwitchName = switchName,
                SwitchType = networkState.Type,
                AllowManagementOS = hasInternal,
                Id = string.IsNullOrEmpty(switchId) ? switchGuid : switchId,
                Upstream = networkState.Upstream,
                StateError = networkState.StateError
            };
        }

        internal static async Task<(SwitchMode Type, SwitchUpstreamAdapter? Upstream, string? StateError)> ReadSwitchNetworkStateAsync(
            int externalPortCount, Guid externalAdapterId, Func<Task<Guid>> readHostConnectionId,
            SwitchDisplayReadContext? readContext = null)
        {
            SwitchMode switchType = externalPortCount > 0 ? SwitchMode.Bridge : SwitchMode.Isolated;

            var adapters = await (readContext?.PhysicalAdapters ?? GetPhysicalAdaptersAsync());
            var upstream = adapters.SingleOrDefault(a => a.ConnectionId == externalAdapterId);
            var icsResponse = await (readContext?.Connections.Value ?? ComApi.GetConnectionsAsync());
            string? stateError = !icsResponse.Success ? icsResponse.Error : externalPortCount > 1 ? "Multiple uplink ports require explicit configuration." : null;
            if (icsResponse.Success)
            {
                var privateId = await readHostConnectionId();
                var shared = icsResponse.Data!;
                if (shared.Any(c => c.Id == privateId && c.Enabled && c.Type == 1))
                {
                    switchType = SwitchMode.NAT;
                    var source = shared.SingleOrDefault(c => c.Enabled && c.Type == 0);
                    upstream = adapters.SingleOrDefault(a => a.ConnectionId == source?.Id);
                    if (source == null) stateError = "ICS private connection has no public connection.";
                }
            }

            return (switchType, upstream, stateError);
        }

        private enum PortConnectionKind { Nothing, Internal, External, VirtualMachine }

        private static PortConnectionKind DeterminePortType(
            ManagementObject portSettings)
        {
            if (portSettings["HostResource"] is string[] hostResource && hostResource.Length > 0)
            {
                var path = new ManagementPath(hostResource[0]);
                if (string.Equals(path.ClassName, "Msvm_ComputerSystem", StringComparison.OrdinalIgnoreCase))
                    return PortConnectionKind.Internal;

                if (string.Equals(path.ClassName, "Msvm_ExternalEthernetPort", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path.ClassName, "Msvm_WiFiPort", StringComparison.OrdinalIgnoreCase))
                    return PortConnectionKind.External;
            }

            string parent = portSettings["Parent"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(parent))
            {
                var parentPath = new ManagementPath(parent);
                if (string.Equals(parentPath.ClassName, "Msvm_SyntheticEthernetPortSettingData", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parentPath.ClassName, "Msvm_EmulatedEthernetPortSettingData", StringComparison.OrdinalIgnoreCase))
                    return PortConnectionKind.VirtualMachine;
            }

            return PortConnectionKind.Nothing;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CreateSwitchAsync — WmiApi
        // ══════════════════════════════════════════════════════════════════
        /// <summary>校验当前主机状态后创建交换机，并按模式建立上游或共享。</summary>
        /// <remarks>在宿主网络锁内重新读取配置。新建时三种模式均保留宿主连接；NAT 使用 ICS，可能替换既有共享并改变私有侧 IPv4/DNS。明确失败才恢复共享并删除本次新交换机；结果未知时保留现场。CoreOperationException.Result 保留错误类别，Steps 的 ResourceId 提供失败时已确认的新交换机 GUID；成功调用不返回该 GUID，需重新查询。</remarks>
        /// <param name="name">新交换机显示名称。</param>
        /// <param name="mode">目标连接模式：桥接、ICS NAT 或隔离。</param>
        /// <param name="upstream">桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。</param>
        public static Task CreateSwitchAsync(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream) =>
            RunConfigurationAsync(() => CreateSwitchCoreAsync(name, mode, upstream));

        private static async Task CreateSwitchCoreAsync(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream)
        {
            string? createdPath = null;
            List<IcsConnection>? oldSharing = null;
            try
            {
                // Re-read under the configuration gate; UI snapshots are only for form feedback.
                var existing = await GetSwitchListAsync(requireSuccess: true);
                var physical = await GetPhysicalAdaptersAsync();
                var bridgeable = await GetBridgeableAdaptersAsync();
                var creationError = SwitchConfigurationRules.GetCreationError(name, mode, upstream, existing, physical, bridgeable);
                if (creationError != null) throw new ArgumentException(creationError);
                await ValidateChangeAsync(mode, upstream);
                var sharing = await ComApi.GetConnectionsAsync();
                RequireSuccess(sharing);
                oldSharing = sharing.Data!;
                switch (mode)
                {
                    case SwitchMode.Bridge:
                        if (upstream == null)
                            throw new ArgumentException(Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);
                        // 桥接：外部端口 + 主机管理端口都加，主机与虚拟机一同接入该外部交换机
                        // (会生成 vEthernet (交换机名) 主机网卡；桥接下主机连接固定开启，无单独开关)
                        await CreateSwitchWmiAsync(name, isExternal: true, upstream!.ExternalPortPath, allowManagementOS: true, created => createdPath = created);
                        break;

                    case SwitchMode.NAT:
                        await CreateSwitchWmiAsync(name, isExternal: false, null, allowManagementOS: true, created => createdPath = created);
                        await Task.Delay(3000);
                        await SetNatModeAsync(SwitchIdFromPath(createdPath!), upstream!);
                        break;

                    case SwitchMode.Isolated:
                    default:
                        await CreateSwitchWmiAsync(name, isExternal: false, null, allowManagementOS: true, created => createdPath = created);
                        break;
                }
            }
            catch (Exception ex)
            {
                await FailConfigurationAsync(ex, createdPath == null ? null : async () =>
                    {
                        if (oldSharing != null) await ComApi.RestoreConnectionsAsync(oldSharing);
                        await DestroySwitchPathAsync(createdPath);
                    }, CreatedSwitchSteps(createdPath));
            }
        }

        // 整体放进 Task.Run：首个 await 前的同步 WMI(GetManagementScope/ManagementClass.CreateInstance)及
        // GetHostComputerSystemPath(searcher.Get) 都在调用线程；新建交换机从 UI 线程 await 调到会卡。
        private static Task CreateSwitchWmiAsync(
            string name, bool isExternal, string? externalPortPath, bool allowManagementOS, Action<string> created) => Task.Run(async () =>
        {
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

            string settingXml;
            using (var settingClass = new ManagementClass(ms, new ManagementPath("Msvm_VirtualEthernetSwitchSettingData"), null))
            using (var settingInstance = settingClass.CreateInstance())
            {
                settingInstance["ElementName"] = name;
                settingXml = settingInstance.GetText(TextFormat.CimDtd20);
            }

            // DefineSystem：ResourceSettings 传 null，与 PS 底层 BeginCreateVirtualSwitch 行为一致
            var defineResult = await WmiApi.InvokeWithResultAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                "DefineSystem",
                p =>
                {
                    p["SystemSettings"] = settingXml;
                    p["ResourceSettings"] = null;
                    p["ReferenceConfiguration"] = null;
                },
                WmiScope.HyperV, resultField: "ResultingSystem");

            RequireSuccess(defineResult);

            string createdSwitchPath = defineResult.Data?.SingleOrDefault()
                ?? throw new CoreOperationException(ApiResponse.Fail("DefineSystem 未返回新交换机身份，无法确认所创建资源；未尝试按名称清理。",
                    kind: ApiErrorKind.OutcomeUnknown));
            created(createdSwitchPath);
            using var switchObj = new ManagementObject(ms, new ManagementPath(createdSwitchPath), null);
            string settingPath = await GetSwitchSettingPathAsync(switchObj);

            var resourceXmls = new List<string>();

            if (isExternal && !string.IsNullOrEmpty(externalPortPath))
            {
                string extPortPath = externalPortPath;
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

                RequireSuccess(addResult);
            }
        });

        // ══════════════════════════════════════════════════════════════════
        //  DeleteSwitchAsync — WmiApi + ComApi
        // ══════════════════════════════════════════════════════════════════
        /// <summary>关闭相关 ICS 后删除指定交换机；默认交换机受规则保护。</summary>
        /// <remarks>名称在宿主网络锁内解析一次；重名返回 AmbiguousTarget，不选第一项。删除可能影响已连接 VM；失败由 CoreOperationException.Result 表达，不能推定先行步骤已撤销。</remarks>
        /// <param name="switchName">目标交换机的唯一显示名称。</param>
        public static Task DeleteSwitchAsync(string switchName) =>
            RunConfigurationAsync(async () => await DeleteSwitchCoreAsync(await ResolveSwitchIdAsync(switchName)));

        /// <summary>按稳定 GUID 关闭相关 ICS 并删除交换机，默认交换机受保护。</summary>
        /// <param name="switchId">查询返回的交换机 GUID，不受显示名称修改影响。</param>
        /// <remarks>跨 Core 进程串行执行。失败抛出 CoreOperationException，Result 保留失败类别；OutcomeUnknown 时不得直接重试或清理。先行 ICS 修改不保证撤销。</remarks>
        public static Task DeleteSwitchAsync(Guid switchId) =>
            RunConfigurationAsync(() => DeleteSwitchCoreAsync(switchId));

        private static async Task DeleteSwitchCoreAsync(Guid switchId)
        {
            try
            {
                using var target = await GetSwitchObjectAsync(switchId);
                string? error = SwitchConfigurationRules.GetDeletionError(switchId.ToString("D"));
                if (error != null) throw new SwitchConfigurationConflictException(error);

                await DisableIcsIfPresentAsync(switchId);

                var result = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "DestroySystem",
                    p => p["AffectedSystem"] = target.Path.Path,
                    WmiScope.HyperV);

                RequireSuccess(result);
            }
            catch (SwitchConfigurationConflictException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in DeleteSwitchAsync: {ex}");
                throw ConfigurationFailure(ex);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  UpdateSwitchConfigurationAsync — WmiApi + ComApi
        // ══════════════════════════════════════════════════════════════════
        /// <summary>按唯一名称解析交换机身份，再切换连接模式。</summary>
        /// <remarks>名称在宿主网络锁内解析一次，重名返回 AmbiguousTarget。明确失败才尝试恢复；结果未知时停止后续修改及恢复。CoreOperationException.Result 保留失败类别、原始错误和恢复结果，调用方应重新读取实际状态。</remarks>
        /// <param name="switchName">目标交换机的唯一显示名称；可优先使用 GUID 重载。</param>
        /// <param name="mode">目标连接模式：桥接、ICS NAT 或隔离。</param>
        /// <param name="upstream">桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。</param>
        /// <param name="allowManagementOS">桥接或隔离模式下是否保留宿主连接；NAT 按共享要求处理。</param>
        public static Task UpdateSwitchConfigurationAsync(
            string switchName, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS) =>
            RunConfigurationAsync(async () => await UpdateSwitchCoreAsync(await ResolveSwitchIdAsync(switchName), mode, upstream, allowManagementOS));

        /// <summary>按稳定 GUID 修改交换机端口和 ICS 共享；在宿主网络锁内重新校验配置。</summary>
        /// <param name="switchId">查询返回的交换机 GUID。</param>
        /// <param name="mode">目标桥接、ICS NAT 或隔离模式。</param>
        /// <param name="upstream">桥接或 NAT 的宿主上游网卡；隔离可为 null。</param>
        /// <param name="allowManagementOS">桥接或隔离时是否保留宿主连接；NAT 按共享要求处理。</param>
        /// <remarks>明确失败时尝试恢复原配置；结果未知时不恢复、不重试。CoreOperationException.Result 保留 OutcomeUnknown、CleanupRequired 等类别和原始失败。锁覆盖修改与恢复，但不阻止未遵循 Core 锁协议的工具。</remarks>
        public static Task UpdateSwitchConfigurationAsync(
            Guid switchId, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS) =>
            RunConfigurationAsync(() => UpdateSwitchCoreAsync(switchId, mode, upstream, allowManagementOS));

        private static async Task UpdateSwitchCoreAsync(
            Guid switchId, SwitchMode mode, SwitchUpstreamAdapter? upstream, bool allowManagementOS)
        {
            // 预检和快照不修改配置；只有实际切换开始后的失败才执行恢复。
            var existing = await GetSwitchListAsync(requireSuccess: true);
            using var target = await GetSwitchObjectAsync(switchId);
            var conflict = SwitchConfigurationRules.GetUpdateConflict(target["Name"]?.ToString() ?? string.Empty,
                mode, upstream, existing);
            if (conflict != null) throw new SwitchConfigurationConflictException(conflict);
            await ValidateChangeAsync(mode, upstream);
            var saved = await CaptureConfigurationAsync(switchId);
            try
            {
                switch (mode)
                {
                    case SwitchMode.Bridge: await SetBridgeModeAsync(switchId, upstream!.ExternalPortPath, allowManagementOS); break;
                    case SwitchMode.NAT: await SetNatModeAsync(switchId, upstream!); break;
                    case SwitchMode.Isolated: await SetIsolatedModeAsync(switchId, allowManagementOS); break;
                    default: throw new ArgumentOutOfRangeException(nameof(mode));
                }
            }
            catch (Exception failure)
            {
                await FailConfigurationAsync(failure, () => RestoreConfigurationAsync(switchId, saved));
            }
        }

        private static async Task ValidateChangeAsync(SwitchMode mode, SwitchUpstreamAdapter? upstream)
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (mode == SwitchMode.Isolated) return;
            if (mode == SwitchMode.NAT)
            {
                var hotspot = await ComApi.IsMobileHotspotActiveAsync();
                if (!hotspot.Success)
                    throw new InvalidOperationException(string.Format(Properties.Resources.Network_HotspotUnknown, hotspot.Error));
                if (hotspot.Data)
                    throw new InvalidOperationException(Properties.Resources.Network_HotspotConflict);
            }
            var available = await GetPhysicalAdaptersAsync();
            var actual = available.SingleOrDefault(a => a.ConnectionId == upstream?.ConnectionId &&
                string.Equals(a.DeviceId, upstream.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (actual == null || (mode == SwitchMode.Bridge &&
                (string.IsNullOrEmpty(actual.ExternalPortPath) || actual.ExternalPortPath != upstream!.ExternalPortPath)))
                throw new InvalidOperationException(Properties.Resources.Network_UpstreamMissing);
            if (mode == SwitchMode.NAT)
            {
                var connections = await ComApi.GetConnectionsAsync();
                RequireSuccess(connections);
                if (!connections.Data!.Any(c => c.Id == actual.ConnectionId))
                    throw new InvalidOperationException(Properties.Resources.Network_UpstreamMissing);
            }
        }

        /// <summary>ICS 会修改私有侧地址；恢复端口后同时恢复原来的 IPv4 与 DNS 配置。</summary>
        private sealed record HostIpv4Settings(bool Dhcp, string[] Addresses, string[] Masks,
            string[] Gateways, ushort[] GatewayMetrics, string[]? Dns)
        {
            private static string AdapterQuery(Guid id)
                => $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{{{id}}}'";

            public static async Task<HostIpv4Settings?> CaptureAsync(Guid id)
            {
                if (id == Guid.Empty) return null;
                // 在 WmiApi 管理的对象生命周期内完成快照，保留属性解析失败的诊断。
                var result = await WmiApi.WithFirstAsync(AdapterQuery(id),
                    config => Task.FromResult(ReadSettings(config, id)), WmiScope.CimV2);
                if (!result.Success)
                    throw new InvalidOperationException($"Cannot read host IP configuration: {id}. {result.Error}");
                return result.Data ?? throw new InvalidOperationException($"Host IP adapter is missing: {id}");
            }

            private static HostIpv4Settings ReadSettings(ManagementObject config, Guid id)
            {
                var addresses = config["IPAddress"] as string[] ?? [];
                var masks = config["IPSubnet"] as string[] ?? [];
                if (addresses.Length != masks.Length) throw new InvalidOperationException("Host IP address and subnet arrays differ.");
                var indices = Enumerable.Range(0, addresses.Length).Where(i => !addresses[i].Contains(':')).ToArray();
                var gateways = config["DefaultIPGateway"] as string[] ?? [];
                var metrics = config["GatewayCostMetric"] as ushort[] ?? [];
                var gatewayIndices = Enumerable.Range(0, gateways.Length).Where(i => !gateways[i].Contains(':')).ToArray();
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{{{id}}}");
                string configuredDns = key?.GetValue("NameServer") as string ?? "";
                return new HostIpv4Settings((bool)(config["DHCPEnabled"] ?? false),
                    indices.Select(i => addresses[i]).ToArray(), indices.Select(i => masks[i]).ToArray(),
                    gatewayIndices.Select(i => gateways[i]).ToArray(), gatewayIndices.Select(i => i < metrics.Length ? metrics[i] : (ushort)1).ToArray(),
                    string.IsNullOrWhiteSpace(configuredDns) ? null : configuredDns.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries));
            }

            public async Task RestoreAsync(Guid id)
            {
                // 新建回来的主机网卡可能尚未出现在 Win32 配置列表里。
                bool found = false;
                for (int attempt = 0; attempt < 20 && !found; attempt++)
                {
                    var result = await WmiApi.QueryAsync(AdapterQuery(id), config => config.Path.Path, WmiScope.CimV2);
                    if (!result.Success)
                        throw new InvalidOperationException($"Cannot find host IP adapter: {id}. {result.Error}");
                    found = result.Data?.Count == 1;
                    if (!found) await Task.Delay(250);
                }
                if (!found) throw new InvalidOperationException($"Host IP adapter did not reappear: {id}");
                var current = (await CaptureAsync(id))!;
                // DHCP 租约不属于需要还原的静态配置；配置已一致时避免重复启用未连接网卡的 IP。
                if (!MatchesAddressConfiguration(current))
                {
                    if (Dhcp) await InvokeAsync(id, "EnableDHCP");
                    else
                    {
                        if (Addresses.Length == 0) throw new InvalidOperationException("Original static IPv4 configuration had no address; automatic recovery is unavailable.");
                        await InvokeAsync(id, "EnableStatic", ("IPAddress", Addresses), ("SubnetMask", Masks));
                        await InvokeAsync(id, "SetGateways", ("DefaultIPGateway", Gateways), ("GatewayCostMetric", GatewayMetrics));
                    }
                    current = (await CaptureAsync(id))!;
                }
                if (!MatchesDnsConfiguration(current))
                    await InvokeAsync(id, "SetDNSServerSearchOrder", ("DNSServerSearchOrder", Dns));

                var restored = (await CaptureAsync(id))!;
                if (!MatchesAddressConfiguration(restored) || !MatchesDnsConfiguration(restored))
                    throw new InvalidOperationException($"Host IP recovery readback mismatch: {id}");
            }

            private bool MatchesAddressConfiguration(HostIpv4Settings actual)
                => Dhcp == actual.Dhcp && (Dhcp ||
                    (Addresses.SequenceEqual(actual.Addresses) && Masks.SequenceEqual(actual.Masks) &&
                     Gateways.SequenceEqual(actual.Gateways) && GatewayMetrics.SequenceEqual(actual.GatewayMetrics)));

            private bool MatchesDnsConfiguration(HostIpv4Settings actual)
                => (Dns ?? []).SequenceEqual(actual.Dns ?? [], StringComparer.OrdinalIgnoreCase);

            private static async Task InvokeAsync(Guid id, string method, params (string Name, object? Value)[] values)
            {
                var result = await WmiApi.InvokeAsync(AdapterQuery(id), method,
                    parameters => { foreach (var value in values) parameters[value.Name] = value.Value; }, WmiScope.CimV2);
                RequireSuccess(result);
            }
        }

        private sealed record ConfigurationSnapshot(List<PortAllocation> Ports, List<IcsConnection> Sharing,
            Guid HostId, HostIpv4Settings? HostIp);

        private static async Task<ConfigurationSnapshot> CaptureConfigurationAsync(Guid switchId)
        {
            using var sw = await GetSwitchObjectAsync(switchId);
            var ports = await GetPortAllocationsAsync(sw);
            if (ports.Count(p => p.Kind == PortConnectionKind.External) > 1 || ports.Count(p => p.Kind == PortConnectionKind.Internal) > 1)
                throw new InvalidOperationException("Multiple uplink/host ports require explicit configuration.");
            var sharing = await ComApi.GetConnectionsAsync();
            RequireSuccess(sharing);
            var hostId = await GetHostConnectionIdAsync(switchId);
            var ip = await HostIpv4Settings.CaptureAsync(hostId);
            return new(ports, sharing.Data!, hostId, ip);
        }

        private static async Task RestoreConfigurationAsync(Guid switchId, ConfigurationSnapshot saved)
        {
            using var sw = await GetSwitchObjectAsync(switchId);
            string settingPath = await GetSwitchSettingPathAsync(sw);
            var actual = await GetPortAllocationsAsync(sw);
            foreach (var kind in new[] { PortConnectionKind.External, PortConnectionKind.Internal })
            {
                var desired = saved.Ports.SingleOrDefault(p => p.Kind == kind);
                var existing = actual.SingleOrDefault(p => p.Kind == kind);
                if (existing != null && desired == null)
                    await InvokePortChangeAsync("RemoveResourceSettings", new[] { existing.Path });
                else if (existing == null && desired != null)
                    await InvokePortChangeAsync("AddResourceSettings", new[] { desired.Xml }, settingPath);
                else if (existing != null && desired != null && existing.HostResource != desired.HostResource)
                {
                    using var allocation = new ManagementObject(sw.Scope, new ManagementPath(existing.Path), null);
                    allocation.Get();
                    allocation["HostResource"] = new[] { desired.HostResource };
                    await InvokePortChangeAsync("ModifyResourceSettings", new[] { allocation.GetText(TextFormat.CimDtd20) });
                }
            }
            var restored = await GetPortAllocationsAsync(sw);
            var expectedPorts = saved.Ports.Select(p => (p.Kind, p.HostResource)).ToHashSet();
            if (!expectedPorts.SetEquals(restored.Select(p => (p.Kind, p.HostResource))))
                throw new InvalidOperationException("Switch recovery readback mismatch.");
            Guid hostId = await GetHostConnectionIdAsync(switchId);
            var sharing = saved.Sharing.Select(c => c.Id == saved.HostId && hostId != Guid.Empty ? c with { Id = hostId } : c).ToList();
            await ComApi.RestoreConnectionsAsync(sharing);
            if (saved.HostIp != null)
                await saved.HostIp.RestoreAsync(hostId);
        }

        private static async Task DestroySwitchPathAsync(string path)
        {
            var result = await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                "DestroySystem", p => p["AffectedSystem"] = path, WmiScope.HyperV);
            RequireSuccess(result);
        }

        private static async Task InvokePortChangeAsync(string method, string[] resources, string? setting = null)
        {
            var result = await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualEthernetSwitchManagementService", method,
                p => { p["ResourceSettings"] = resources; if (setting != null) p["AffectedConfiguration"] = setting; }, WmiScope.HyperV);
            RequireSuccess(result);
        }

        // 仅当本交换机当前确实配了 ICS(即它就是那台 NAT 交换机)时才清理。
        // ICS 全局只有一份共享,无条件 DisableAll 会把别的 NAT 交换机也一并关掉;
        // 按主机私有侧连接 GUID 判断归属，读取失败不能当作共享已关闭。
        private static async Task DisableIcsIfPresentAsync(Guid switchId)
        {
            Guid hostId = await GetHostConnectionIdAsync(switchId);
            var snapshot = await ComApi.GetConnectionsAsync();
            RequireSuccess(snapshot);
            if (snapshot.Data!.Any(c => c.Id == hostId && c.Enabled && c.Type == 1))
                await ComApi.RestoreConnectionsAsync(snapshot.Data!.Select(c => c with { Enabled = false, Type = -1 }).ToList());
        }

        private static async Task SetBridgeModeAsync(Guid switchId, string externalPortPath, bool allowManagementOS = true)
        {
            if (string.IsNullOrEmpty(externalPortPath))
                throw new ArgumentException("Bridge mode requires a physical adapter.");

            // 先确认目标存在；保留已有主机端口，避免破坏其身份与配置。
            string extPortPath = externalPortPath;
            if (string.IsNullOrEmpty(extPortPath))
                throw new InvalidOperationException(
                    Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);

            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchId);
            string settingPath = await GetSwitchSettingPathAsync(switchObj);
            var ports = await GetPortAllocationsAsync(switchObj);
            var externalPorts = ports.Where(p => p.Kind == PortConnectionKind.External).ToList();
            bool hasInternal = ports.Any(p => p.Kind == PortConnectionKind.Internal);
            if (externalPorts.Count > 1)
                throw new InvalidOperationException("Multiple uplink ports require explicit teaming configuration.");

            var resourceXmls = new List<string>();
            string? modifiedExternalXml = null;
            if (externalPorts.Count == 0)
            {
                using var extAllocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var extAllocInstance = extAllocClass.CreateInstance();
                extAllocInstance["HostResource"] = new string[] { extPortPath };
                resourceXmls.Add(extAllocInstance.GetText(TextFormat.CimDtd20));
            }
            else if (!string.Equals(externalPorts[0].HostResource, extPortPath, StringComparison.OrdinalIgnoreCase))
            {
                // 已是桥接时修改现有上联，不重复添加第二个外部端口。
                using var extAlloc = new ManagementObject(ms, new ManagementPath(externalPorts[0].Path), null);
                extAlloc.Get();
                extAlloc["HostResource"] = new string[] { extPortPath };
                modifiedExternalXml = extAlloc.GetText(TextFormat.CimDtd20);
            }

            // 保留现有主机 vNIC 的身份及端口设置，仅在缺失时补建。
            if (allowManagementOS && !hasInternal)
            {
                string hostSystemPath = GetHostComputerSystemPath(ms);
                if (string.IsNullOrEmpty(hostSystemPath))
                    throw new InvalidOperationException("Cannot find the host computer system.");
                using var intAllocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var intAllocInstance = intAllocClass.CreateInstance();
                intAllocInstance["ElementName"] = switchObj["ElementName"]?.ToString() ?? string.Empty;
                intAllocInstance["HostResource"] = new string[] { hostSystemPath };
                resourceXmls.Add(intAllocInstance.GetText(TextFormat.CimDtd20));
            }

            await DisableIcsIfPresentAsync(switchId);

            if (modifiedExternalXml != null)
            {
                var modifyResult = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "ModifyResourceSettings",
                    p => p["ResourceSettings"] = new string[] { modifiedExternalXml },
                    WmiScope.HyperV);
                RequireSuccess(modifyResult);
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
                RequireSuccess(addResult);
            }

            // 只有明确要求关闭主机连接，才在上联配置成功后移除主机端口。
            if (!allowManagementOS && hasInternal)
                await RemoveInternalPortsAsync(switchObj, ms);

            var actualPorts = await GetPortAllocationsAsync(switchObj);
            if (!actualPorts.Any(p => p.Kind == PortConnectionKind.External &&
                    string.Equals(p.HostResource, extPortPath, StringComparison.OrdinalIgnoreCase)) ||
                actualPorts.Any(p => p.Kind == PortConnectionKind.Internal) != allowManagementOS)
                throw new InvalidOperationException("Switch port configuration did not match the requested bridge configuration.");
        }

        private static async Task SetNatModeAsync(Guid switchId, SwitchUpstreamAdapter upstream)
        {
            if (upstream == null)
                throw new ArgumentException("NAT mode requires a physical adapter.");

            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchId);
            await EnsureInternalModeAsync(switchObj, ms);

            // ICS 内部先验证目标，再变更共享；外层事务负责恢复交换机端口和主机 IP。

            Guid privateId = await GetHostConnectionIdAsync(switchId);
            var icsResult = await ComApi.EnableIcsSharingForConnectionsAsync(upstream.ConnectionId, privateId);
            RequireSuccess(icsResult);
        }

        private static async Task SetIsolatedModeAsync(Guid switchId, bool allowManagementOS)
        {
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var switchObj = await GetSwitchObjectAsync(switchId);
            await EnsureInternalModeAsync(switchObj, ms);
            await DisableIcsIfPresentAsync(switchId);

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

                RequireSuccess(addResult);
            }
            else if (!allowManagementOS && hasInternal)
            {
                await RemoveInternalPortsAsync(switchObj, ms);
            }
        }

        // ── Switch 操作辅助 ───────────────────────────────────────────────

        private static async Task<ManagementObject> GetSwitchObjectAsync(Guid switchId)
        {
            if (switchId == Guid.Empty) throw new ArgumentException("交换机 GUID 不能为空。", nameof(switchId));
            var resp = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE Name = '{switchId:D}'",
                obj => new ManagementObject(obj.Scope, obj.Path, null),
                WmiScope.HyperV);

            RequireSuccess(resp);
            if (resp.Data?.Count == 1) return resp.Data[0];
            foreach (var item in resp.Data ?? []) item.Dispose();
            throw new CoreOperationException(ApiResponse.Fail($"无法唯一确定交换机 {switchId:D}。",
                kind: resp.Data?.Count > 1 ? ApiErrorKind.AmbiguousTarget : ApiErrorKind.NotFound));
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

        private sealed record PortAllocation(string Path, PortConnectionKind Kind, string HostResource, string Xml);
        private sealed class SwitchPortReadException(string message) : InvalidOperationException(message);

        private static async Task<List<PortAllocation>> GetPortAllocationsAsync(ManagementObject switchObj)
        {
            // 返回值只保留属性快照，避免 QueryRelatedAsync 释放对象后再读取。
            var portPaths = await WmiApi.QueryRelatedAsync(
                switchObj, "Msvm_EthernetSwitchPort", p => p.Path.Path, "Msvm_SystemDevice");
            if (!portPaths.Success || portPaths.Data == null)
                throw new InvalidOperationException(portPaths.Error);

            var allocations = new List<PortAllocation>();
            foreach (var path in portPaths.Data)
            {
                using var port = new ManagementObject(switchObj.Scope, new ManagementPath(path), null);
                var settings = await WmiApi.QueryRelatedAsync(
                    port, "Msvm_EthernetPortAllocationSettingData",
                    p => new PortAllocation(p.Path.Path, DeterminePortType(p),
                        (p["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty, p.GetText(TextFormat.CimDtd20)),
                    "Msvm_ElementSettingData");
                if (!settings.Success || settings.Data == null || settings.Data.Count == 0)
                    throw new SwitchPortReadException($"Cannot read switch port allocation: {path}. {settings.Error}");
                allocations.AddRange(settings.Data);
            }
            return allocations;
        }

        private static async Task RemoveInternalPortsAsync(ManagementObject switchObj, ManagementScope ms)
        {
            var ports = await GetPortAllocationsAsync(switchObj);
            var internalPortPaths = ports.Where(p => p.Kind == PortConnectionKind.Internal).Select(p => p.Path).ToArray();
            if (internalPortPaths.Length == 0) return;

            var removeResult = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                "RemoveResourceSettings",
                p => p["ResourceSettings"] = internalPortPaths,
                WmiScope.HyperV);
            RequireSuccess(removeResult);
        }

        private static async Task<bool> HasInternalPortAsync(ManagementObject switchObj)
            => (await GetPortAllocationsAsync(switchObj)).Any(p => p.Kind == PortConnectionKind.Internal);

        private static async Task EnsureInternalModeAsync(ManagementObject switchObj, ManagementScope ms)
        {
            var ports = await GetPortAllocationsAsync(switchObj);
            var externalPortPaths = ports.Where(p => p.Kind == PortConnectionKind.External).Select(p => p.Path).ToArray();
            bool hasInternal = ports.Any(p => p.Kind == PortConnectionKind.Internal);

            // 先准备好缺失主机端口的配置，再修改现有上联。
            string? hostXml = null;
            string? settingPath = null;
            if (!hasInternal)
            {
                string hostSystemPath = GetHostComputerSystemPath(ms);
                if (string.IsNullOrEmpty(hostSystemPath))
                    throw new InvalidOperationException("Cannot find the host computer system.");
                settingPath = await GetSwitchSettingPathAsync(switchObj);
                using var allocClass = new ManagementClass(ms, new ManagementPath("Msvm_EthernetPortAllocationSettingData"), null);
                using var allocInstance = allocClass.CreateInstance();
                allocInstance["ElementName"] = switchObj["ElementName"]?.ToString() ?? string.Empty;
                allocInstance["HostResource"] = new string[] { hostSystemPath };
                hostXml = allocInstance.GetText(TextFormat.CimDtd20);
            }

            if (externalPortPaths.Length > 0)
            {
                var removeResult = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "RemoveResourceSettings",
                    p => p["ResourceSettings"] = externalPortPaths,
                    WmiScope.HyperV);
                RequireSuccess(removeResult);
            }

            if (hostXml != null)
            {
                var addResult = await WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_VirtualEthernetSwitchManagementService",
                    "AddResourceSettings",
                    p =>
                    {
                        p["AffectedConfiguration"] = settingPath;
                        p["ResourceSettings"] = new string[] { hostXml };
                    },
                    WmiScope.HyperV);
                RequireSuccess(addResult);
            }

            var actualPorts = await GetPortAllocationsAsync(switchObj);
            if (actualPorts.Any(p => p.Kind == PortConnectionKind.External) ||
                !actualPorts.Any(p => p.Kind == PortConnectionKind.Internal))
                throw new InvalidOperationException("Switch port configuration did not match the requested internal configuration.");
        }

        private static async Task<Guid> GetHostConnectionIdAsync(Guid switchId)
        {
            // 沿实际连接关系定位宿主 vNIC，不使用可重名或独立改名的 ElementName。
            var endpoints = await WmiApi.QueryAsync(
                $"SELECT * FROM Msvm_LANEndpoint WHERE SystemCreationClassName = 'Msvm_VirtualEthernetSwitch' AND SystemName = '{switchId:D}'",
                p => p.Path.Path, WmiScope.HyperV);
            RequireSuccess(endpoints);
            var ids = new HashSet<Guid>();
            var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            foreach (string endpointPath in endpoints.Data ?? [])
            {
                using var endpoint = new ManagementObject(scope, new ManagementPath(endpointPath), null);
                var peers = await WmiApi.QueryRelatedAsync(endpoint, "Msvm_LANEndpoint", p => p.Path.Path, "Msvm_ActiveConnection");
                RequireSuccess(peers);
                foreach (string peerPath in peers.Data ?? [])
                {
                    using var peer = new ManagementObject(scope, new ManagementPath(peerPath), null);
                    var ports = await WmiApi.QueryRelatedAsync(peer, "Msvm_InternalEthernetPort",
                        p => Guid.Parse(p["DeviceID"].ToString()!.Replace("Microsoft:", "", StringComparison.OrdinalIgnoreCase)),
                        "Msvm_EthernetDeviceSAPImplementation");
                    RequireSuccess(ports);
                    ids.UnionWith(ports.Data ?? []);
                }
            }
            if (ids.Count > 1) throw new CoreOperationException(ApiResponse.Fail(
                "此交换机连接多个宿主网卡，需要明确选择。", kind: ApiErrorKind.AmbiguousTarget));
            return ids.SingleOrDefault();
        }

        private static string GetHostComputerSystemPath(ManagementScope ms)
        {
            string hostName = WmiApi.Escape(System.Environment.MachineName);
            using var searcher = new ManagementObjectSearcher(ms,
                new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{hostName}'"));
            using var col = searcher.Get();
            var host = col.Cast<ManagementObject>().FirstOrDefault();
            return host?.Path.Path ?? string.Empty;
        }

        // ══════════════════════════════════════════════════════════════════
        //  GetFullSwitchNetworkStateAsync — WmiApi + CimApi
        // ══════════════════════════════════════════════════════════════════
        /// <summary>读取接入指定交换机的宿主和虚拟机网卡。</summary>
        /// <returns>已读取的端点列表；失败可能被省略或转为空列表，不能将空列表当作已确认没有端点。</returns>
        /// <param name="switchName">交换机显示名称；当前实现遇到同名对象时取查询首项，调用方须避免歧义。</param>
        /// <remarks>每个端点返回显示名称、MAC 和一个择优 IPv4 地址。</remarks>
        public static async Task<List<SwitchEndpointInfo>> GetFullSwitchNetworkStateAsync(string switchName)
        {
            try
            {
                var allAdapters = new List<SwitchEndpointInfo>();

                // 找到 Switch 对象路径，用于过滤端口
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

                // 查所有 VM 的 Msvm_SyntheticEthernetPort，过滤连接到此 Switch 的
                var vmAdapters = await GetVmAdaptersOnSwitchAsync(switchGuid, switchName);
                allAdapters.AddRange(vmAdapters);

                // 查 ManagementOS 的 Internal 端口
                var hostAdapter = await GetHostAdapterOnSwitchAsync(switchName);
                if (hostAdapter != null)
                    allAdapters.Add(hostAdapter);

                return allAdapters;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting full network state for switch '{switchName}': {ex.Message}");
                return new List<SwitchEndpointInfo>();
            }
        }
    }
}
