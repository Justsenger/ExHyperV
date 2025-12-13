using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Xml;
using System.Diagnostics;

namespace ExHyperV.Tools
{
    public static class HcsManager
    {
        [DllImport("vmcompute.dll", SetLastError = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int HcsModifyServiceSettings(string settings, out IntPtr result);

        [DllImport("vmcompute.dll", SetLastError = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int HcsGetServiceProperties(string propertyQuery, out IntPtr properties, out IntPtr result);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        public static void SetVmCpuGroup(Guid vmId, Guid groupId)
        {
            // --- 这部分 WMI 绑定/解绑逻辑保持您提供的版本，不做任何改动 ---
            bool isUnbinding = groupId == Guid.Empty;
            string operationType = isUnbinding ? "解除绑定" : "绑定";
            Debug.WriteLine($"================== [WMI Operation Start: {operationType}] ==================");
            Debug.WriteLine($"[HcsManager - WMI] VM ID: '{vmId}'");
            Debug.WriteLine($"[HcsManager - WMI] Target GroupId (from caller): '{groupId}'");
            string scope = @"\\.\root\virtualization\v2";
            ManagementScope managementScope = new ManagementScope(scope);
            try
            {
                managementScope.Connect();
                ManagementObject processorSetting = GetProcessorSettingData(managementScope, vmId);
                if (processorSetting == null) throw new Exception($"无法找到虚拟机 (ID: {vmId}) 的处理器配置。");
                string originalProcessorSettingData = processorSetting.GetText(TextFormat.WmiDtd20);
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(originalProcessorSettingData);
                XmlNode cpuGroupIdPropertyNode = doc.SelectSingleNode("//PROPERTY[@NAME='CpuGroupId']");
                XmlNode cpuGroupIdValueNode = cpuGroupIdPropertyNode?.SelectSingleNode("VALUE");
                if (cpuGroupIdValueNode == null)
                {
                    Debug.WriteLine("[HcsManager - WMI] 未找到 CpuGroupId/VALUE 节点，将创建新节点。");
                    if (cpuGroupIdPropertyNode == null)
                    {
                        cpuGroupIdPropertyNode = doc.CreateElement("PROPERTY");
                        XmlAttribute nameAttr = doc.CreateAttribute("NAME"); nameAttr.Value = "CpuGroupId"; cpuGroupIdPropertyNode.Attributes.Append(nameAttr);
                        XmlAttribute typeAttr = doc.CreateAttribute("TYPE"); typeAttr.Value = "string"; cpuGroupIdPropertyNode.Attributes.Append(typeAttr);
                        doc.DocumentElement.AppendChild(cpuGroupIdPropertyNode);
                    }
                    cpuGroupIdValueNode = doc.CreateElement("VALUE");
                    cpuGroupIdPropertyNode.AppendChild(cpuGroupIdValueNode);
                }
                string targetValue = isUnbinding ? "00000000-0000-0000-0000-000000000000" : groupId.ToString("D"); // 使用全零 GUID 解绑
                Debug.WriteLine($"[HcsManager - WMI] 正在将 CpuGroupId/VALUE 节点的 InnerText 设置为: '{targetValue}'");
                cpuGroupIdValueNode.InnerText = targetValue;
                ManagementObject managementService = GetVirtualSystemManagementService(managementScope);
                var inParams = managementService.GetMethodParameters("ModifyResourceSettings");
                string modifiedXml = doc.OuterXml;
                inParams["ResourceSettings"] = new string[] { modifiedXml };
                Debug.WriteLine($"[HcsManager - WMI] 准备调用 ModifyResourceSettings，传入的 ResourceSettings XML ({operationType}) 如下:");
                Debug.WriteLine(modifiedXml);
                var outParams = managementService.InvokeMethod("ModifyResourceSettings", inParams, null);
                uint returnValue = (uint)outParams["ReturnValue"];
                Debug.WriteLine($"[HcsManager - WMI] ModifyResourceSettings 返回值: {returnValue}");
                if (returnValue == 4096)
                {
                    Debug.WriteLine("[HcsManager - WMI] 操作为异步，开始等待作业完成...");
                    ManagementObject job = new ManagementObject((string)outParams["Job"]);
                    while ((ushort)job["JobState"] == 4 || (ushort)job["JobState"] == 7)
                    {
                        System.Threading.Thread.Sleep(500);
                        job.Get();
                    }
                    ushort finalJobState = (ushort)job["JobState"];
                    Debug.WriteLine($"[HcsManager - WMI] 作业完成，最终状态: {finalJobState}");
                    if (finalJobState != 10) throw new Exception($"应用CPU组的作业失败。作业状态: {finalJobState}. 错误: {job["ErrorDescription"]}");
                }
                else if (returnValue != 0)
                {
                    throw new Exception($"应用CPU组失败。错误码: {returnValue}");
                }
                Debug.WriteLine($"[HcsManager - WMI] {operationType} 操作成功完成。");
            }
            finally
            {
                Debug.WriteLine($"================== [WMI Operation End: {operationType}] ==================\n");
            }
        }

        // --- WMI 辅助方法 (保持不变) ---
        private static ManagementObject GetVirtualSystemManagementService(ManagementScope scope) { var sp = new ManagementPath("Msvm_VirtualSystemManagementService") { NamespacePath = scope.Path.Path, Server = scope.Path.Server }; var co = new ManagementClass(sp); return co.GetInstances().Cast<ManagementObject>().FirstOrDefault(); }
        private static ManagementObject GetProcessorSettingData(ManagementScope scope, Guid vmId) { string q = $"SELECT * FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE '%{vmId}%'"; using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(q))) { return s.Get().Cast<ManagementObject>().FirstOrDefault(); } }

        // --- HCS 方法 (进行关键修正) ---
        public static string GetAllCpuGroupsAsJson()
        {
            return ExecuteHcsQuery("{\"PropertyTypes\":[\"CpuGroup\"]}");
        }
        public static string GetVmCpuGroupAsJson(Guid vmId)
        {
            string scope = @"\\.\root\virtualization\v2";
            string query = $"SELECT * FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE '%{vmId}%'";
            string resultJson = null;
            try
            {
                // 注意：WMI 查询最好也在 STA 线程中进行，以保持一致性
                CoInitializeEx(IntPtr.Zero, 2);
                using (var searcher = new ManagementObjectSearcher(scope, query))
                {
                    var vmSetting = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmSetting?["CpuGroupId"] != null && !string.IsNullOrEmpty(vmSetting["CpuGroupId"].ToString()) && Guid.TryParse(vmSetting["CpuGroupId"].ToString(), out Guid parsedGuid) && parsedGuid != Guid.Empty)
                    {
                        resultJson = $"{{ \"CpuGroupId\": \"{vmSetting["CpuGroupId"]}\" }}";
                    }
                    else
                    {
                        resultJson = $"{{ \"CpuGroupId\": \"{Guid.Empty}\" }}";
                    }
                }
            }
            finally { CoUninitialize(); }
            return resultJson;
        }
        public static void CreateCpuGroup(Guid groupId, uint[] processorIndexes)
        {
            var processors = string.Join(",", processorIndexes);
            string createJson = $@"{{""PropertyType"":""CpuGroup"",""Settings"":{{""Operation"":""CreateGroup"",""OperationDetails"":{{""GroupId"":""{groupId}"",""LogicalProcessorCount"":{processorIndexes.Length},""LogicalProcessors"":[{processors}]}}}}}}";
            ExecuteHcsModification(createJson);
        }
        public static void DeleteCpuGroup(Guid groupId)
        {
            string deleteJson = $@"{{""PropertyType"":""CpuGroup"",""Settings"":{{""Operation"":""DeleteGroup"",""OperationDetails"":{{""GroupId"":""{groupId}""}}}}}}";
            ExecuteHcsModification(deleteJson);
        }
        public static void SetCpuGroupCap(Guid groupId, ushort cpuCap)
        {
            string setPropertyJson = $@"{{""PropertyType"":""CpuGroup"",""Settings"":{{""Operation"":""SetProperty"",""OperationDetails"":{{""GroupId"":""{groupId}"",""PropertyCode"":65536,""PropertyValue"":{cpuCap}}}}}}}";
            ExecuteHcsModification(setPropertyJson);
        }
        private static void ExecuteHcsModification(string jsonPayload)
        {
            Debug.WriteLine($"---------- [HCS Execute Start] ----------");
            Debug.WriteLine(jsonPayload);
            // =======================================================
            // 终极修正: 严格使用 0 (COINIT_MULTITHREADED)
            // 这是修复创建组失败的关键
            // =======================================================
            CoInitializeEx(IntPtr.Zero, 2);
            IntPtr resultPtr = IntPtr.Zero;
            try
            {
                int hResult = HcsModifyServiceSettings(jsonPayload, out resultPtr);
                if (hResult != 0)
                {
                    string errorJson = Marshal.PtrToStringUni(resultPtr);
                    Debug.WriteLine($"[HCS Execute] HCS 调用失败! HRESULT: 0x{hResult:X}. Details: {errorJson}");
                    throw new Exception($"HCS Modify call failed. HRESULT: 0x{hResult:X}. Details: {errorJson}");
                }
                Debug.WriteLine("[HCS Execute] HCS 调用成功。");
            }
            finally
            {
                if (resultPtr != IntPtr.Zero) CoTaskMemFree(resultPtr);
                CoUninitialize();
                Debug.WriteLine($"---------- [HCS Execute End] ----------\n");
            }
        }
        private static string ExecuteHcsQuery(string jsonPayload)
        {
            // =======================================================
            // 终极修正: 严格使用 0 (COINIT_MULTITHREADED)
            // =======================================================
            CoInitializeEx(IntPtr.Zero, 0);
            IntPtr propertiesPtr = IntPtr.Zero;
            IntPtr resultPtr = IntPtr.Zero;
            string resultJson = null;
            try
            {
                int hResult = HcsGetServiceProperties(jsonPayload, out propertiesPtr, out resultPtr);
                if (hResult != 0)
                {
                    string errorJson = Marshal.PtrToStringUni(resultPtr);
                    throw new Exception($"HCS Query call failed. HRESULT: 0x{hResult:X}. Details: {errorJson}");
                }
                if (propertiesPtr != IntPtr.Zero) { resultJson = Marshal.PtrToStringUni(propertiesPtr); }
            }
            finally
            {
                if (propertiesPtr != IntPtr.Zero) CoTaskMemFree(propertiesPtr);
                if (resultPtr != IntPtr.Zero) CoTaskMemFree(resultPtr);
                CoUninitialize();
            }
            return resultJson;
        }
    }
}