using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;
using System.Diagnostics;
using System.Text.RegularExpressions;
namespace ExHyperV.Services
{
    public class VmNetworkService
    {
        private const string ServiceClass = "Msvm_VirtualSystemManagementService";
        private const string ScopeNamespace = @"root\virtualization\v2"; private void Log(string message) => Debug.WriteLine($"[VmNetDebug][{DateTime.Now:HH:mm:ss.fff}] {message}");

        public async Task<List<VmNetworkAdapter>> GetNetworkAdaptersAsync(string vmName)
        {
            Log($"==============================================================");
            Log($"开始为虚拟机 '{vmName}' 获取网卡信息...");

            var resultList = new List<VmNetworkAdapter>();
            if (string.IsNullOrEmpty(vmName))
            {
                Log("[错误] 传入的 vmName 为空，操作中止。");
                return resultList;
            }

            // 步骤 1: 获取 VM 的系统 GUID (在 WMI 中为 'Name' 属性)
            Log($"[1/4] 正在查询虚拟机 '{vmName}' 的 GUID...");
            var vmQueryResult = await WmiTools.QueryAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'",
                (vm) => vm["Name"]?.ToString());

            string vmGuid = vmQueryResult.FirstOrDefault();
            if (string.IsNullOrEmpty(vmGuid))
            {
                Log($"[错误] 找不到名为 '{vmName}' 的虚拟机。请检查虚拟机名称是否正确。");
                return resultList;
            }
            Log($"[成功] 获取到 VM GUID: {vmGuid}");

            // 步骤 2: 并发查询该 VM 的所有网卡端口设置和端口分配设置
            Log($"[2/4] 正在并发查询属于 GUID '{vmGuid}' 的网卡端口和分配设置...");
            var portsTask = WmiTools.QueryAsync(
                $"SELECT ElementName, InstanceID, Address, StaticMacAddress FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
                (o) => (ManagementObject)o);

            var allocsTask = WmiTools.QueryAsync(
                $"SELECT EnabledState, InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
                (o) => (ManagementObject)o);

            await Task.WhenAll(portsTask, allocsTask);

            var allPorts = portsTask.Result;
            var allAllocs = allocsTask.Result;
            Log($"[成功] 查询完成: 找到 {allPorts.Count} 个网卡端口, {allAllocs.Count} 个分配设置。");
            Log($"--------------------------------------------------------------");

            // 步骤 3: 遍历网卡端口，并匹配其对应的分配设置
            Log($"[3/4] 开始遍历和匹配每个网卡...");
            int counter = 0;
            foreach (var port in allPorts)
            {
                counter++;
                string elementName = port["ElementName"]?.ToString() ?? "（无名称）";
                Log($"\n--- [处理第 {counter}/{allPorts.Count} 个网卡: '{elementName}'] ---");

                string fullPortId = port["InstanceID"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(fullPortId))
                {
                    Log("  [警告] 此端口的 InstanceID 为空，跳过处理。");
                    continue;
                }
                Log($"  [端口信息] 完整 InstanceID: {fullPortId}");

                string deviceGuid = fullPortId.Split('\\').Last();
                Log($"  [端口信息] 提取的 Device GUID (用于匹配): {deviceGuid}");

                var adapter = new VmNetworkAdapter
                {
                    Id = fullPortId,
                    Name = elementName,
                    MacAddress = FormatMac(port["Address"]?.ToString()),
                    IsStaticMac = GetBool(port, "StaticMacAddress")
                };
                Log($"  [端口信息] MAC 地址: {adapter.MacAddress}, 是否静态: {adapter.IsStaticMac}");

                // 核心匹配逻辑
                Log($"  [匹配操作] 正在为 Device GUID '{deviceGuid}' 查找分配设置...");
                var allocation = allAllocs.FirstOrDefault(a =>
                    a["InstanceID"]?.ToString().Contains(deviceGuid, StringComparison.OrdinalIgnoreCase) == true);

                if (allocation != null)
                {
                    Log("  [匹配结果] >>> 成功找到匹配的分配设置! <<<");
                    Log($"    [分配信息] 匹配到的 InstanceID: {allocation["InstanceID"]}");

                    // 解析连接状态
                    string stateStr = allocation["EnabledState"]?.ToString();
                    Log($"    [状态解析] EnabledState (原始值): {stateStr ?? "null"} (2=已连接, 3=未连接)");
                    adapter.IsConnected = (stateStr == "2");
                    Log($"    [状态解析] IsConnected (布尔值): {adapter.IsConnected}");

                    // 解析交换机名
                    if (adapter.IsConnected && allocation["HostResource"] is string[] hostResources && hostResources.Length > 0)
                    {
                        string switchWmiPath = hostResources[0];
                        Log($"    [交换机解析] HostResource 路径: {switchWmiPath}");

                        string swGuid = switchWmiPath.Split('"').Reverse().Skip(1).FirstOrDefault();
                        if (!string.IsNullOrEmpty(swGuid))
                        {
                            Log($"    [交换机解析] 提取的交换机 GUID: {swGuid}");
                            adapter.SwitchName = await GetSwitchNameByGuidAsync(swGuid);
                            Log($"    [交换机解析] 查询到的交换机名称: {adapter.SwitchName}");
                        }
                        else
                        {
                            Log("    [交换机解析] [警告] 无法从 HostResource 中解析出交换机 GUID。");
                            adapter.SwitchName = "解析失败";
                        }
                    }
                    else
                    {
                        Log("    [交换机解析] 网卡未连接或无 HostResource，SwitchName 设为 '未连接'。");
                        adapter.SwitchName = "未连接";
                    }
                }
                else
                {
                    Log("  [匹配结果] >>> [失败] 未找到匹配的分配设置。将状态设为未连接。 <<<");
                    adapter.IsConnected = false;
                    adapter.SwitchName = "未连接";
                }

                resultList.Add(adapter);
                Log($"  [添加对象] 已创建并添加 VmNetworkAdapter 对象到结果列表。最终状态: Name='{adapter.Name}', Connected={adapter.IsConnected}, Switch='{adapter.SwitchName}'");
            }

            Log($"\n[4/4] 所有网卡处理完毕。");
            Log($"==============================================================");
            Log($"最终返回 {resultList.Count} 个网络适配器对象。");
            Log($"==============================================================");

            return resultList;
        }
        // 辅助方法：通过交换机 GUID 查找其显示名称 ElementName
        private async Task<string> GetSwitchNameByGuidAsync(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "未连接";

            var res = await WmiTools.QueryAsync(
                $"SELECT ElementName FROM Msvm_VirtualEthernetSwitch WHERE Name = '{guid}'",
                (s) => s["ElementName"]?.ToString());

            return res.FirstOrDefault() ?? "未知交换机";
        }        // ==========================================
        // [新增] 后台填充 IP 方法 (供 ViewModel 异步调用)
        // ==========================================
        public async Task FillDynamicIpsAsync(string vmName, IEnumerable<VmNetworkAdapter> adapters)
        {
            var targetAdapters = adapters.Where(a => (a.IpAddresses == null || a.IpAddresses.Count == 0) && !string.IsNullOrEmpty(a.MacAddress)).ToList();
            if (targetAdapters.Count == 0) return;

            Log($">>> [Background] 开始填充 IP...");

            foreach (var adapter in targetAdapters)
            {
                // 如果其他线程已经填充了，跳过
                if (adapter.IpAddresses != null && adapter.IpAddresses.Count > 0) continue;

                try
                {
                    string arpIp = await Utils.GetVmIpAddressAsync(vmName, adapter.MacAddress);
                    if (!string.IsNullOrEmpty(arpIp))
                    {
                        var newIps = arpIp.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(x => x.Trim()).ToList();

                        // 更新对象属性 (由于是引用类型，ViewModel 只要通知变更即可)
                        adapter.IpAddresses = newIps;
                        Log($"  [IP更新] {adapter.Name} -> {arpIp}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"IP获取失败: {ex.Message}");
                }
            }
        }

        // ==========================================
        // 2. 写入逻辑 (Apply 方法)
        // ==========================================

        public async Task<(bool Success, string Message)> ApplyVlanSettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortVlanSettingData", (s) => {
                s["OperationMode"] = (uint)adapter.VlanMode;
                s["AccessVlanId"] = (ushort)adapter.AccessVlanId;
                s["NativeVlanId"] = (ushort)adapter.NativeVlanId;
                if (adapter.TrunkAllowedVlanIds?.Any() == true)
                    s["TrunkVlanIdArray"] = adapter.TrunkAllowedVlanIds.Select(x => (ushort)x).ToArray();
                s["PrimaryVlanId"] = (ushort)adapter.PvlanPrimaryId;
                s["SecondaryVlanId"] = (ushort)adapter.PvlanSecondaryId;
                s["PvlanMode"] = (uint)adapter.PvlanMode;
            });
        }

        public async Task<(bool Success, string Message)> ApplyBandwidthSettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortBandwidthSettingData", (s) => {
                s["Limit"] = (ulong)(adapter.BandwidthLimit * 1000000);
                s["Reservation"] = (ulong)(adapter.BandwidthReservation * 1000000);
            });
        }

        public async Task<(bool Success, string Message)> ApplySecuritySettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortSecuritySettingData", (s) => {
                s["AllowMacSpoofing"] = adapter.MacSpoofingAllowed;
                s["EnableDhcpGuard"] = adapter.DhcpGuardEnabled;
                s["EnableRouterGuard"] = adapter.RouterGuardEnabled;
                s["AllowTeaming"] = adapter.TeamingAllowed;
                s["MonitorMode"] = (byte)adapter.MonitorMode;
                s["StormLimit"] = (uint)adapter.StormLimit;
            });
        }

        public async Task<(bool Success, string Message)> ApplyOffloadSettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortOffloadSettingData", (s) => {
                s["VMQOffloadWeight"] = (uint)(adapter.VmqEnabled ? 100 : 0);
                s["IOVOffloadWeight"] = (uint)(adapter.SriovEnabled ? 1 : 0);
                s["IPSecOffloadLimit"] = (uint)(adapter.IpsecOffloadEnabled ? 512 : 0);
            });
        }

        // ==========================================
        // 3. 核心机制
        // ==========================================
        private async Task<(bool Success, string Message)> EnsureAndModifyFeatureAsync(string portId, string featureClass, Action<ManagementObject> updateAction)
        {
            try
            {
                string escapedId = portId.Replace("\\", "\\\\");
                var xmlInfo = await WmiTools.QueryAsync($"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'", (port) => {
                    using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData").Cast<ManagementObject>().FirstOrDefault();
                    if (allocation == null) return null;
                    var existing = allocation.GetRelated(featureClass, "Msvm_EthernetPortSettingDataComponent", null, null, null, null, false, null).Cast<ManagementObject>().FirstOrDefault();
                    if (existing != null)
                    {
                        updateAction(existing);
                        return new { IsNew = false, Xml = existing.GetText(TextFormat.CimDtd20), Target = string.Empty };
                    }
                    else
                    {
                        var template = GetDefaultFeatureTemplate(featureClass);
                        if (template == null) return null;
                        updateAction(template);
                        template["InstanceID"] = Guid.NewGuid().ToString();
                        return new { IsNew = true, Xml = template.GetText(TextFormat.CimDtd20), Target = allocation.Path.Path };
                    }
                });

                var info = xmlInfo.FirstOrDefault();
                if (info == null) return (false, "无法定位配置对象。");
                var inParams = new Dictionary<string, object> { { "FeatureSettings", new string[] { info.Xml } } };
                if (info.IsNew) inParams["AffectedConfiguration"] = info.Target;
                return await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", info.IsNew ? "AddFeatureSettings" : "ModifyFeatureSettings", inParams);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private void ParseFeatureSettings(VmNetworkAdapter adapter, ManagementObject feature)
        {
            string cls = feature.ClassPath.ClassName;
            if (cls == "Msvm_EthernetSwitchPortVlanSettingData")
            {
                adapter.VlanMode = (VlanOperationMode)GetUint(feature, "OperationMode");
                adapter.AccessVlanId = (int)GetUint(feature, "AccessVlanId");
                adapter.NativeVlanId = (int)GetUint(feature, "NativeVlanId");
                adapter.PvlanMode = (PvlanMode)GetUint(feature, "PvlanMode");
                adapter.PvlanPrimaryId = (int)GetUint(feature, "PrimaryVlanId");
                adapter.PvlanSecondaryId = (int)GetUint(feature, "SecondaryVlanId");
                if (HasProperty(feature, "TrunkVlanIdArray") && feature["TrunkVlanIdArray"] is ushort[] trunks) adapter.TrunkAllowedVlanIds = trunks.Select(x => (int)x).ToList();
            }
            else if (cls == "Msvm_EthernetSwitchPortBandwidthSettingData")
            {
                adapter.BandwidthLimit = GetUlong(feature, "Limit") / 1000000;
                adapter.BandwidthReservation = GetUlong(feature, "Reservation") / 1000000;
            }
            else if (cls == "Msvm_EthernetSwitchPortSecuritySettingData")
            {
                adapter.MacSpoofingAllowed = GetBool(feature, "AllowMacSpoofing");
                adapter.DhcpGuardEnabled = GetBool(feature, "EnableDhcpGuard");
                adapter.RouterGuardEnabled = GetBool(feature, "EnableRouterGuard");
                adapter.TeamingAllowed = GetBool(feature, "AllowTeaming");
                adapter.MonitorMode = (PortMonitorMode)GetUint(feature, "MonitorMode");
                adapter.StormLimit = (uint)GetUint(feature, "StormLimit");
            }
            else if (cls == "Msvm_EthernetSwitchPortOffloadSettingData")
            {
                adapter.VmqEnabled = GetUint(feature, "VMQOffloadWeight") > 0;
                adapter.SriovEnabled = GetUint(feature, "IOVOffloadWeight") > 0;
                adapter.IpsecOffloadEnabled = GetUint(feature, "IPSecOffloadLimit") > 0;
            }
        }

        // ==========================================
        // 4. 其余辅助
        // ==========================================

        public async Task<(bool Success, string Message)> UpdateConnectionAsync(string vmName, VmNetworkAdapter adapter)
        {
            string escapedId = adapter.Id.Replace("\\", "\\\\");
            var res = await WmiTools.QueryAsync($"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'", (port) => {
                using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData").Cast<ManagementObject>().FirstOrDefault();
                if (allocation == null) return null;
                allocation["EnabledState"] = (ushort)(adapter.IsConnected ? 2 : 3);
                if (adapter.IsConnected && !string.IsNullOrEmpty(adapter.SwitchName))
                {
                    string path = GetSwitchPathByName(adapter.SwitchName);
                    if (!string.IsNullOrEmpty(path)) allocation["HostResource"] = new string[] { path };
                }
                else { allocation["HostResource"] = null; }
                return allocation.GetText(TextFormat.CimDtd20);
            });
            if (string.IsNullOrEmpty(res.FirstOrDefault())) return (false, "找不到分配对象");
            return await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", "ModifyResourceSettings", new Dictionary<string, object> { { "ResourceSettings", new string[] { res.First() } } });
        }

        public async Task<(bool Success, string Message)> AddNetworkAdapterAsync(string vmName)
        {
            var searcher = new ManagementObjectSearcher(ScopeNamespace, "SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID LIKE '%Default%'");
            var template = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (template == null) return (false, "模板缺失");
            template["InstanceID"] = Guid.NewGuid().ToString();
            string xml = template.GetText(TextFormat.CimDtd20);
            var vmPaths = await WmiTools.QueryAsync($"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'", (vm) => {
                var sets = vm.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                return sets.FirstOrDefault(s => s["VirtualSystemType"]?.ToString().Contains("Realized") == true)?.Path.Path ?? sets.FirstOrDefault()?.Path.Path;
            });
            return await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", "AddResourceSettings", new Dictionary<string, object> { { "SystemSettingData", vmPaths.First() }, { "ResourceSettings", new string[] { xml } } });
        }

        public async Task<(bool Success, string Message)> RemoveNetworkAdapterAsync(string vmName, string id)
        {
            string escapedId = id.Replace("\\", "\\\\");
            var paths = await WmiTools.QueryAsync($"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'", (p) => p.Path.Path);
            return await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", "RemoveResourceSettings", new Dictionary<string, object> { { "ResourceSettings", new string[] { paths.First() } } });
        }

        private string GetSwitchPathByName(string switchName)
        {
            var searcher = new ManagementObjectSearcher(ScopeNamespace, $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{switchName.Replace("'", "''")}'");
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault()?.Path.Path;
        }

        private ManagementObject GetDefaultFeatureTemplate(string className)
        {
            var searcher = new ManagementObjectSearcher(ScopeNamespace, $"SELECT * FROM {className} WHERE InstanceID LIKE '%Default%'");
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        }

        public async Task<List<string>> GetAvailableSwitchesAsync()
        {
            var res = await WmiTools.QueryAsync("SELECT ElementName FROM Msvm_VirtualEthernetSwitch", (s) => s["ElementName"]?.ToString());
            return res.Where(s => !string.IsNullOrEmpty(s)).OrderBy(s => s).ToList();
        }

        private string GetSwitchNameFromPath(string path)
        {
            try { using var obj = new ManagementObject(path); return obj["ElementName"]?.ToString(); }
            catch { return "未连接"; }
        }

        private static string FormatMac(string rawMac) => string.IsNullOrEmpty(rawMac) ? "00-15-5D-00-00-00" : Regex.Replace(rawMac.Replace(":", "").Replace("-", ""), ".{2}", "$0-").TrimEnd('-').ToUpperInvariant();
        private static bool HasProperty(ManagementObject obj, string name) => obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        private static bool GetBool(ManagementObject obj, string name) => HasProperty(obj, name) && obj[name] != null && Convert.ToBoolean(obj[name]);
        private static ulong GetUint(ManagementObject obj, string name) => HasProperty(obj, name) && obj[name] != null ? Convert.ToUInt64(obj[name]) : 0;
        private static ulong GetUlong(ManagementObject obj, string name) => HasProperty(obj, name) && obj[name] != null ? Convert.ToUInt64(obj[name]) : 0;
    }
}