using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ExHyperV.Services
{
    public class VmNetworkService
    {
        // ==========================================
        // 1. 基础常量与日志工具
        // ==========================================

        private const string ServiceClass = "Msvm_VirtualSystemManagementService";
        private const string ScopeNamespace = @"root\virtualization\v2";

        // 输出调试日志
        private void Log(string message) => Debug.WriteLine($"[VmNetDebug][{DateTime.Now:HH:mm:ss.fff}] {message}");

        // ==========================================
        // 2. 数据获取与查询 (Read Operations)
        // ==========================================

        // 获取指定虚拟机的所有网卡信息（包含连接状态、高级配置等）
        public async Task<List<VmNetworkAdapter>> GetNetworkAdaptersAsync(string vmName)
        {
            Log($"==============================================================");
            Log(string.Format(Properties.Resources.VmNet_StartGetInfo, vmName));

            var resultList = new List<VmNetworkAdapter>();
            if (string.IsNullOrEmpty(vmName))
            {
                Log(Properties.Resources.Error_Net_NameEmpty);
                return resultList;
            }

            // 步骤 1: 获取 VM 的系统 GUID
            Log(string.Format(Properties.Resources.VmNet_QueryGuid, vmName));
            var vmQueryResult = await WmiTools.QueryAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'",
                (vm) => vm["Name"]?.ToString());

            string vmGuid = vmQueryResult.FirstOrDefault();
            if (string.IsNullOrEmpty(vmGuid))
            {
                Log(string.Format(Properties.Resources.VmNet_VmNotFound, vmName));
                return resultList;
            }
            Log(string.Format(Properties.Resources.VmNet_GotGuid, vmGuid));

            // 步骤 2: 并发查询该 VM 的所有网卡端口设置和端口分配设置
            Log(string.Format(Properties.Resources.VmNet_QueryPorts, vmGuid));
            var portsTask = WmiTools.QueryAsync(
                $"SELECT ElementName, InstanceID, Address, StaticMacAddress FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
                (o) => (ManagementObject)o);

            var allocsTask = WmiTools.QueryAsync(
                $"SELECT EnabledState, InstanceID, HostResource FROM Msvm_EthernetPortAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'",
                (o) => (ManagementObject)o);

            await Task.WhenAll(portsTask, allocsTask);

            var allPorts = portsTask.Result;
            var allAllocs = allocsTask.Result;
            Log(string.Format(Properties.Resources.VmNet_QueryComplete, allPorts.Count, allAllocs.Count));
            Log($"--------------------------------------------------------------");

            // 步骤 3: 遍历网卡端口，并匹配其对应的分配设置
            Log(Properties.Resources.Msg_Net_Scanning);
            int counter = 0;
            foreach (var port in allPorts)
            {
                counter++;
                string elementName = port["ElementName"]?.ToString() ?? Properties.Resources.Common_NoName;
                Log($"\n--- [处理第 {counter}/{allPorts.Count} 个网卡: '{elementName}'] ---");

                string fullPortId = port["InstanceID"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(fullPortId))
                {
                    Log(Properties.Resources.Warn_Net_EmptyInstance);
                    continue;
                }

                string deviceGuid = fullPortId.Split('\\').Last();

                var adapter = new VmNetworkAdapter
                {
                    Id = fullPortId,
                    Name = elementName,
                    MacAddress = FormatMac(port["Address"]?.ToString()),
                    IsStaticMac = GetBool(port, "StaticMacAddress")
                };

                // 核心匹配逻辑
                var allocation = allAllocs.FirstOrDefault(a =>
                    a["InstanceID"]?.ToString().Contains(deviceGuid, StringComparison.OrdinalIgnoreCase) == true);

                if (allocation != null)
                {
                    string stateStr = allocation["EnabledState"]?.ToString();
                    adapter.IsConnected = (stateStr == "2");

                    // 无论是否连接，都去尝试读取 HostResource
                    if (allocation["HostResource"] is string[] hostResources && hostResources.Length > 0)
                    {
                        string switchWmiPath = hostResources[0];
                        string swGuid = switchWmiPath.Split('"').Reverse().Skip(1).FirstOrDefault();
                        adapter.SwitchName = await GetSwitchNameByGuidAsync(swGuid);
                    }
                    else
                    {
                        adapter.SwitchName = null;
                    }

                    // =========================================================
                    // 高级特性
                    // =========================================================
                    try
                    {
                        string rawId = allocation["InstanceID"]?.ToString();

                        if (!string.IsNullOrEmpty(rawId))
                        {
                            // 关键步骤：WQL 字符串字面量中，反斜杠必须是双反斜杠
                            string wqlSafeId = rawId.Replace(@"\", @"\\").Replace("'", "\\'");

                            // 构造相对路径: ClassName.Property="Value"
                            string relPath = $"Msvm_EthernetPortAllocationSettingData.InstanceID=\"{wqlSafeId}\"";

                            // 查询关联的 FeatureSettings (VLAN, Security, Bandwidth etc.)
                            string query = $"ASSOCIATORS OF {{{relPath}}} " +
                                           $"WHERE AssocClass = Msvm_EthernetPortSettingDataComponent " +
                                           $"ResultClass = Msvm_EthernetSwitchPortFeatureSettingData";

                            using var searcher = new ManagementObjectSearcher(ScopeNamespace, query);
                            using var features = searcher.Get();

                            int featureCount = 0;
                            foreach (var feature in features.Cast<ManagementObject>())
                            {
                                ParseFeatureSettings(adapter, feature);
                                featureCount++;
                            }

                            if (featureCount == 0)
                                Log(Properties.Resources.Warn_Net_DefaultSettings);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format(Properties.Resources.VmNetworkService_2, ex.Message));
                    }
                }
                else
                {
                    adapter.IsConnected = false;
                    adapter.SwitchName = Properties.Resources.Status_Unconnected;
                }

                resultList.Add(adapter);
            }

            Log(Properties.Resources.Msg_Net_ScanDone);
            return resultList;
        }

        // 获取宿主机上所有可用的虚拟交换机名称列表
        public async Task<List<string>> GetAvailableSwitchesAsync()
        {
            var res = await WmiTools.QueryAsync("SELECT ElementName FROM Msvm_VirtualEthernetSwitch", (s) => s["ElementName"]?.ToString());
            return res.Where(s => !string.IsNullOrEmpty(s)).OrderBy(s => s).ToList();
        }

        // 后台任务：尝试通过 ARP 填充网卡的动态 IP 地址
        public async Task FillDynamicIpsAsync(string vmName, IEnumerable<VmNetworkAdapter> adapters)
        {
            var targetAdapters = adapters.Where(a => (a.IpAddresses == null || a.IpAddresses.Count == 0) && !string.IsNullOrEmpty(a.MacAddress)).ToList();
            if (targetAdapters.Count == 0) return;

            Log(Properties.Resources.VmNetworkService_3);

            foreach (var adapter in targetAdapters)
            {
                if (adapter.IpAddresses != null && adapter.IpAddresses.Count > 0) continue;

                try
                {
                    string arpIp = await Utils.GetVmIpAddressAsync(vmName, adapter.MacAddress);
                    if (!string.IsNullOrEmpty(arpIp))
                    {
                        var newIps = arpIp.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(x => x.Trim()).ToList();
                        adapter.IpAddresses = newIps;
                    }
                }
                catch (Exception ex)
                {
                    Log(string.Format(Properties.Resources.VmNet_IpFail, ex.Message));
                }
            }
        }

        // ==========================================
        // 3. 网卡生命周期与连接管理 (Lifecycle & Connection)
        // ==========================================

        // 为虚拟机新增一块网络适配器
        public async Task<(bool Success, string Message)> AddNetworkAdapterAsync(string vmName)
        {
            try
            {
                Log(string.Format(Properties.Resources.VmNetworkService_4, vmName));

                // 1. 构造脚本
                string script = $"Add-VMNetworkAdapter -VMName '{vmName.Replace("'", "''")}'";

                // 2. 使用 Run2 (异步且带错误流检测)
                await Utils.Run2(script);

                return (true, Properties.Resources.VmNet_AddSuccess);
            }
            catch (PowerShellScriptException psEx)
            {
                // 这里会捕获到你刚才在终端看到的“一代机运行中无法添加”的具体报错
                Log(string.Format(Properties.Resources.VmNet_PsError, psEx.Message));

                // 使用你现有的格式化工具，把冗长的 PS 报错简化
                string friendlyMsg = Utils.GetFriendlyErrorMessages(psEx.Message);
                return (false, friendlyMsg);
            }
            catch (Exception ex)
            {
                Log(string.Format(Properties.Resources.VmNet_SysError, ex.Message));
                return (false, ex.Message);
            }
        }
        // 移除指定的网络适配器
        public async Task<(bool Success, string Message)> RemoveNetworkAdapterAsync(string vmName, string id)
        {
            string escapedId = id.Replace("\\", "\\\\");
            var paths = await WmiTools.QueryAsync($"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'", (p) => p.Path.Path);
            return await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", "RemoveResourceSettings", new Dictionary<string, object> { { "ResourceSettings", new string[] { paths.First() } } });
        }

        // 更新网卡的连接状态（连接到指定交换机或断开连接）
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
            if (string.IsNullOrEmpty(res.FirstOrDefault())) return (false, Properties.Resources.Error_Net_AllocNotFound);
            return await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", "ModifyResourceSettings", new Dictionary<string, object> { { "ResourceSettings", new string[] { res.First() } } });
        }

        // ==========================================
        // 4. 高级特性配置 (Apply Settings)
        // ==========================================

        public async Task<(bool Success, string Message)> ApplyVlanSettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            // ---------------------------------------------------------
            // 1. 【UI 归一逻辑】直接操作 adapter 属性，使界面立刻同步
            // ---------------------------------------------------------
            if (adapter.VlanMode == VlanOperationMode.Private)
            {
                // 自动补全 0 值
                if (adapter.PvlanPrimaryId == 0) adapter.PvlanPrimaryId = 100;
                if (adapter.PvlanSecondaryId == 0) adapter.PvlanSecondaryId = 101;

                // 如果是网关模式，强制 UI 上的“辅 ID”跳回和“主 ID”一致
                if (adapter.PvlanMode == PvlanMode.Promiscuous)
                {
                    Log(string.Format(Properties.Resources.VmNet_GatewayModeLog, adapter.PvlanSecondaryId, adapter.PvlanPrimaryId));
                    adapter.PvlanSecondaryId = adapter.PvlanPrimaryId;
                }
            }

            // ---------------------------------------------------------
            // 2. 【WMI 写入逻辑】根据 Hyper-V 协议强制转换底层参数
            // ---------------------------------------------------------
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortVlanSettingData", (s) => {

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
                        s["TrunkVlanIdArray"] = adapter.TrunkAllowedVlanIds?.Any() == true ? adapter.TrunkAllowedVlanIds.Select(x => (ushort)x).ToArray() : Array.Empty<ushort>();
                        s["AccessVlanId"] = (ushort)0;
                        s["PvlanMode"] = (uint)0;
                        s["PrimaryVlanId"] = (ushort)0;
                        s["SecondaryVlanId"] = (ushort)0;
                        s["SecondaryVlanIdArray"] = null;
                        break;

                    case VlanOperationMode.Private:
                        uint pMode = (uint)adapter.PvlanMode;
                        if (pMode == 0) pMode = 1;

                        ushort priId = (ushort)adapter.PvlanPrimaryId;
                        ushort secId = (ushort)adapter.PvlanSecondaryId;

                        s["PvlanMode"] = pMode;
                        s["PrimaryVlanId"] = priId;

                        // --- 核心修复：针对 Promiscuous (3) 的特殊协议转换 ---
                        if (pMode == 3)
                        {
                            // 协议规定：网关模式下，底层 SecondaryVlanId 必须为 0
                            s["SecondaryVlanId"] = (ushort)0;
                            // 协议规定：网关能互通的 ID 必须写在数组里
                            s["SecondaryVlanIdArray"] = new ushort[] { priId };

                            Log(string.Format(Properties.Resources.VmNet_WmiAlignmentLog, priId));
                        }
                        else
                        {
                            // Isolated 或 Community 模式，正常写入辅 ID
                            s["SecondaryVlanId"] = secId;
                            s["SecondaryVlanIdArray"] = null;
                        }
                        // --------------------------------------------------

                        s["AccessVlanId"] = (ushort)0;
                        s["NativeVlanId"] = (ushort)0;
                        s["TrunkVlanIdArray"] = null;
                        break;
                }
            });
        }

        public async Task<(bool Success, string Message)> ApplyBandwidthSettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortBandwidthSettingData", (s) => {
                s["Limit"] = (ulong)(adapter.BandwidthLimit * 1000000);
                s["Reservation"] = (ulong)(adapter.BandwidthReservation * 1000000);
            });
        }

        // 应用安全设置 (MacSpoofing, Guards, StormLimit)
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

        // 应用硬件卸载设置 (VMQ, IOV, IPSec)
        public async Task<(bool Success, string Message)> ApplyOffloadSettingsAsync(string vmName, VmNetworkAdapter adapter)
        {
            return await EnsureAndModifyFeatureAsync(adapter.Id, "Msvm_EthernetSwitchPortOffloadSettingData", (s) => {
                s["VMQOffloadWeight"] = (uint)(adapter.VmqEnabled ? 100 : 0);
                s["IOVOffloadWeight"] = (uint)(adapter.SriovEnabled ? 1 : 0);
                s["IPSecOffloadLimit"] = (uint)(adapter.IpsecOffloadEnabled ? 512 : 0);
            });
        }

        // ==========================================
        // 5. 核心内部逻辑 (Internal Core Logic)
        // ==========================================

        // 通用方法：确保高级特性存在并修改其属性
        private async Task<(bool Success, string Message)> EnsureAndModifyFeatureAsync(string portId, string featureClass, Action<ManagementObject> updateAction)
        {
            try
            {
                string escapedId = portId.Replace("\\", "\\\\");

                // 第一步：获取或创建配置对象
                var xmlInfo = await WmiTools.QueryAsync($"SELECT * FROM Msvm_SyntheticEthernetPortSettingData WHERE InstanceID = '{escapedId}'", (port) => {
                    using var allocation = port.GetRelated("Msvm_EthernetPortAllocationSettingData").Cast<ManagementObject>().FirstOrDefault();
                    if (allocation == null) return null;

                    var existing = allocation.GetRelated(featureClass, "Msvm_EthernetPortSettingDataComponent", null, null, null, null, false, null).Cast<ManagementObject>().FirstOrDefault();

                    if (existing != null)
                    {
                        // 修改现有对象
                        updateAction(existing);
                        return new { IsNew = false, Obj = existing, Target = string.Empty };
                    }
                    else
                    {
                        // 创建新对象模板
                        var template = GetDefaultFeatureTemplate(featureClass);
                        if (template == null) return null;

                        template["InstanceID"] = Guid.NewGuid().ToString();
                        updateAction(template); // 应用属性
                        return new { IsNew = true, Obj = template, Target = allocation.Path.Path };
                    }
                });

                var info = xmlInfo.FirstOrDefault();
                if (info == null) return (false, Properties.Resources.Error_Net_ConfigObject);

                // 第二步：生成 XML
                string finalXml = info.Obj.GetText(TextFormat.CimDtd20);

                // --- [DEBUG START] 输出 XML 到调试窗口 ---
                Log(string.Format(Properties.Resources.VmNet_SubmitSet, featureClass));
                // 打印 XML (为了不刷屏，只打印关键部分，或者全部打印)
                Debug.WriteLine($"-------- [WMI XML DEBUG] --------\n{finalXml}\n---------------------------------");
                // --- [DEBUG END] ---

                // 第三步：提交给 Hyper-V 服务
                var inParams = new Dictionary<string, object> { { "FeatureSettings", new string[] { finalXml } } };

                string methodName = info.IsNew ? "AddFeatureSettings" : "ModifyFeatureSettings";
                if (info.IsNew) inParams["AffectedConfiguration"] = info.Target;

                var result = await WmiTools.ExecuteMethodAsync($"SELECT * FROM {ServiceClass}", methodName, inParams);

                if (!result.Success)
                {
                    Log(string.Format(Properties.Resources.VmNet_WmiError, methodName, result.Message));
                }

                return result;
            }
            catch (Exception ex)
            {
                Log(string.Format(Properties.Resources.VmNet_Exception, ex.Message, ex.StackTrace));
                return (false, ex.Message);
            }
        }
        // 将 WMI 获取的 FeatureSettings 对象解析为 Model 属性
        private void ParseFeatureSettings(VmNetworkAdapter adapter, ManagementObject feature)
        {
            string cls = feature.ClassPath.ClassName;
            if (cls == "Msvm_EthernetSwitchPortVlanSettingData")
            {
                uint rawMode = (uint)GetUint(feature, "OperationMode");
                

                // 核心映射：如果 WMI 返回 0 (Unknown)，UI 显示为 Access (1)
                adapter.VlanMode = (rawMode == 0) ? VlanOperationMode.Access : (VlanOperationMode)rawMode;

                adapter.AccessVlanId = (int)GetUint(feature, "AccessVlanId");
                adapter.NativeVlanId = (int)GetUint(feature, "NativeVlanId");
                adapter.PvlanMode = (PvlanMode)(uint)GetUint(feature, "PvlanMode");
                adapter.PvlanPrimaryId = (int)GetUint(feature, "PrimaryVlanId");
                adapter.PvlanSecondaryId = (int)GetUint(feature, "SecondaryVlanId");

                if (HasProperty(feature, "TrunkVlanIdArray") && feature["TrunkVlanIdArray"] is ushort[] trunks)
                    adapter.TrunkAllowedVlanIds = trunks.Select(x => (int)x).ToList();
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
        // 6. WMI 辅助工具 (WMI Helpers)
        // ==========================================

        // 根据交换机 GUID 查找显示名称
        private async Task<string> GetSwitchNameByGuidAsync(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return Properties.Resources.Status_Unconnected;
            var res = await WmiTools.QueryAsync(
                $"SELECT ElementName FROM Msvm_VirtualEthernetSwitch WHERE Name = '{guid}'",
                (s) => s["ElementName"]?.ToString());
            return res.FirstOrDefault() ?? Properties.Resources.Common_UnknownSwitch;
        }

        // 根据交换机名称查找其 WMI 路径
        private string GetSwitchPathByName(string switchName)
        {
            var searcher = new ManagementObjectSearcher(ScopeNamespace, $"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{switchName.Replace("'", "''")}'");
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault()?.Path.Path;
        }

        // 获取特定功能类的默认设置模板
        private ManagementObject GetDefaultFeatureTemplate(string className)
        {
            var searcher = new ManagementObjectSearcher(ScopeNamespace, $"SELECT * FROM {className} WHERE InstanceID LIKE '%Default%'");
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        }

        // ==========================================
        // 7. 通用静态工具 (Static Utilities)
        // ==========================================

        // 格式化 MAC 地址
        private static string FormatMac(string rawMac) => string.IsNullOrEmpty(rawMac) ? "00-15-5D-00-00-00" : Regex.Replace(rawMac.Replace(":", "").Replace("-", ""), ".{2}", "$0-").TrimEnd('-').ToUpperInvariant();

        // 检查 WMI 对象是否包含指定属性
        private static bool HasProperty(ManagementObject obj, string name) => obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // 安全获取布尔属性值
        private static bool GetBool(ManagementObject obj, string name) => HasProperty(obj, name) && obj[name] != null && Convert.ToBoolean(obj[name]);

        // 安全获取 ulong 属性值 (转为 ulong)
        private static ulong GetUint(ManagementObject obj, string name) => HasProperty(obj, name) && obj[name] != null ? Convert.ToUInt64(obj[name]) : 0;

        // 安全获取 ulong 属性值
        private static ulong GetUlong(ManagementObject obj, string name) => HasProperty(obj, name) && obj[name] != null ? Convert.ToUInt64(obj[name]) : 0;
    }
}