using System.Management;
using System.Runtime.InteropServices;
using System.Xml;

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
            bool isUnbinding = groupId == Guid.Empty;
            string scope = @"\\.\root\virtualization\v2";
            ManagementScope managementScope = new ManagementScope(scope);

            managementScope.Connect();
            ManagementObject processorSetting = GetProcessorSettingData(managementScope, vmId);
            if (processorSetting == null)
            {
                throw new Exception($"无法找到虚拟机 (ID: {vmId}) 的处理器配置。");
            }

            string originalProcessorSettingData = processorSetting.GetText(TextFormat.WmiDtd20);
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(originalProcessorSettingData);
            XmlNode cpuGroupIdPropertyNode = doc.SelectSingleNode("//PROPERTY[@NAME='CpuGroupId']");
            XmlNode cpuGroupIdValueNode = cpuGroupIdPropertyNode?.SelectSingleNode("VALUE");

            if (cpuGroupIdValueNode == null)
            {
                if (cpuGroupIdPropertyNode == null)
                {
                    cpuGroupIdPropertyNode = doc.CreateElement("PROPERTY");
                    XmlAttribute nameAttr = doc.CreateAttribute("NAME");
                    nameAttr.Value = "CpuGroupId";
                    cpuGroupIdPropertyNode.Attributes.Append(nameAttr);
                    XmlAttribute typeAttr = doc.CreateAttribute("TYPE");
                    typeAttr.Value = "string";
                    cpuGroupIdPropertyNode.Attributes.Append(typeAttr);
                    doc.DocumentElement.AppendChild(cpuGroupIdPropertyNode);
                }
                cpuGroupIdValueNode = doc.CreateElement("VALUE");
                cpuGroupIdPropertyNode.AppendChild(cpuGroupIdValueNode);
            }

            cpuGroupIdValueNode.InnerText = isUnbinding ? Guid.Empty.ToString("D") : groupId.ToString("D");

            ManagementObject managementService = GetVirtualSystemManagementService(managementScope);
            var inParams = managementService.GetMethodParameters("ModifyResourceSettings");
            inParams["ResourceSettings"] = new string[] { doc.OuterXml };

            var outParams = managementService.InvokeMethod("ModifyResourceSettings", inParams, null);
            uint returnValue = (uint)outParams["ReturnValue"];

            if (returnValue == 4096)
            {
                ManagementObject job = new ManagementObject((string)outParams["Job"]);
                job.Get();
                while ((ushort)job["JobState"] == 4 || (ushort)job["JobState"] == 7)
                {
                    System.Threading.Thread.Sleep(500);
                    job.Get();
                }
                if ((ushort)job["JobState"] != 10)
                {
                    throw new Exception($"应用CPU组的作业失败。作业状态: {(ushort)job["JobState"]}. 错误: {job["ErrorDescription"]}");
                }
            }
            else if (returnValue != 0)
            {
                throw new Exception($"应用CPU组失败。错误码: {returnValue}");
            }
        }

        private static ManagementObject GetVirtualSystemManagementService(ManagementScope scope)
        {
            var path = new ManagementPath("Msvm_VirtualSystemManagementService") { NamespacePath = scope.Path.Path, Server = scope.Path.Server };
            var mgmtClass = new ManagementClass(path);
            return mgmtClass.GetInstances().Cast<ManagementObject>().FirstOrDefault();
        }

        private static ManagementObject GetProcessorSettingData(ManagementScope scope, Guid vmId)
        {
            string query = $"SELECT * FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE '%{vmId}%'";
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query)))
            {
                return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            }
        }

        public static string GetAllCpuGroupsAsJson()
        {
            return ExecuteHcsQuery("{\"PropertyTypes\":[\"CpuGroup\"]}");
        }

        public static string GetVmCpuGroupAsJson(Guid vmId)
        {
            string scope = @"\\.\root\virtualization\v2";
            string query = $"SELECT * FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE '%{vmId}%'";
            string resultJson;
            CoInitializeEx(IntPtr.Zero, 2);
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, query))
                {
                    var vmSetting = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmSetting?["CpuGroupId"] != null && Guid.TryParse(vmSetting["CpuGroupId"].ToString(), out Guid parsedGuid) && parsedGuid != Guid.Empty)
                    {
                        resultJson = $"{{ \"CpuGroupId\": \"{vmSetting["CpuGroupId"]}\" }}";
                    }
                    else
                    {
                        resultJson = $"{{ \"CpuGroupId\": \"{Guid.Empty}\" }}";
                    }
                }
            }
            finally
            {
                CoUninitialize();
            }
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
            CoInitializeEx(IntPtr.Zero, 0);
            IntPtr resultPtr = IntPtr.Zero;
            try
            {
                int hResult = HcsModifyServiceSettings(jsonPayload, out resultPtr);
                if (hResult != 0)
                {
                    string errorJson = Marshal.PtrToStringUni(resultPtr);
                    throw new Exception($"HCS Modify call failed. HRESULT: 0x{hResult:X}. Details: {errorJson}");
                }
            }
            finally
            {
                if (resultPtr != IntPtr.Zero) CoTaskMemFree(resultPtr);
                CoUninitialize();
            }
        }

        private static string ExecuteHcsQuery(string jsonPayload)
        {
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
                if (propertiesPtr != IntPtr.Zero)
                {
                    resultJson = Marshal.PtrToStringUni(propertiesPtr);
                }
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