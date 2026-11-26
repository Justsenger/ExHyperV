using ExHyperV.Tools;
using System.Management;
using System.Text.Json;

namespace ExHyperV.Services;

internal sealed record NetworkPropertyFingerprint(string Name, string Type, bool IsArray, object? Value);

public static partial class VmNetworkService
{
    /// <summary>返回 12 位大写静态 MAC 地址，格式无效时返回 null。</summary>
    /// <param name="text">候选静态 MAC 文本。</param>
    /// <returns>十二位大写十六进制地址，输入无效时为 null。</returns>
    public static string? NormalizeStaticMac(string text)
    {
        text = text.Trim();
        if (text.Length == 17 && text[2] is ':' or '-')
        {
            string[] pairs = text.Split(text[2]);
            if (pairs.Length != 6 || pairs.Any(pair => pair.Length != 2)) return null;
            text = string.Concat(pairs);
        }
        return text.Length == 12 && text.All(Uri.IsHexDigit) ? text.ToUpperInvariant() : null;
    }

    private static async Task<VmNetworkAdapterCreationResult> CreateNetworkAdapterAsync(
        Guid vmId, Guid? switchId = null, bool isConnected = false, string? staticMacAddress = null,
        bool isLegacy = false, bool applyConnectionOptions = false)
    {
        bool workflowStarted = false;
        static VmNetworkAdapterCreationResult BeforeCreation(string error, ApiErrorKind kind) =>
            new(false, null, VmNetworkAdapterCreationState.NotCreated,
                VmNetworkAdapterInitializationState.NotStarted, [], error, ErrorKind: kind);
        try
        {
            if (vmId == Guid.Empty) return BeforeCreation("虚拟机 GUID 不能为空。", ApiErrorKind.InvalidArgument);
            if (applyConnectionOptions && isConnected && (switchId is null || switchId == Guid.Empty))
                return BeforeCreation("接通网卡必须指定目标交换机 GUID。", ApiErrorKind.InvalidArgument);
            if (applyConnectionOptions && switchId == Guid.Empty)
                return BeforeCreation("虚拟交换机 GUID 不能为空。", ApiErrorKind.InvalidArgument);

            string? normalizedMac = null;
            if (!string.IsNullOrWhiteSpace(staticMacAddress))
            {
                normalizedMac = VmNetworkService.NormalizeStaticMac(staticMacAddress);
                if (normalizedMac is null)
                    return BeforeCreation(Properties.Resources.Error_Net_MacInvalid, ApiErrorKind.InvalidArgument);
            }

            using var vm = WmiApi.GetVmComputerSystem(vmId);
            if (vm is null) return BeforeCreation(Properties.Resources.Error_Net_VmNotFound, ApiErrorKind.NotFound);

            if (isLegacy || normalizedMac is not null)
            {
                var vmState = VmMapper.MapState(Convert.ToUInt16(vm["EnabledState"]));
                var condition = GetMacModificationCondition(vmState);
                if (!condition.CanModify)
                    return BeforeCreation(condition.Reason, ApiErrorKind.InvalidState);
            }

            using var settings = WmiApi.GetVmSettings(vm);
            if (settings is null || string.IsNullOrEmpty(settings.Path?.Path))
                return BeforeCreation("无法读取当前虚拟机配置。", ApiErrorKind.OperationFailed);

            if (isLegacy)
            {
                string subType = settings["VirtualSystemSubType"]?.ToString() ?? string.Empty;
                if (string.Equals(subType, "Microsoft:Hyper-V:SubType:2", StringComparison.OrdinalIgnoreCase))
                    return BeforeCreation("第 2 代虚拟机不支持添加旧版网卡。", ApiErrorKind.Unsupported);
                if (!string.Equals(subType, "Microsoft:Hyper-V:SubType:1", StringComparison.OrdinalIgnoreCase))
                    return BeforeCreation("无法确认虚拟机为第 1 代，无法添加旧版网卡。", ApiErrorKind.InvalidState);
            }

            string portClass = isLegacy ? VmNetworkAdapterCreation.EmulatedPortClass : VmNetworkAdapterCreation.PortClass;
            using var service = WmiApi.GetVirtualSystemManagementService();
            string? switchPath = null;
            if (applyConnectionOptions && switchId.HasValue)
            {
                using var switchSearcher = new ManagementObjectSearcher(service.Scope,
                    new ObjectQuery($"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE Name = '{switchId.Value:D}'"));
                using var switches = switchSearcher.Get();
                using var targetSwitch = switches.Cast<ManagementObject>().FirstOrDefault();
                if (targetSwitch is null || string.IsNullOrEmpty(targetSwitch.Path?.Path))
                    return BeforeCreation("所选交换机已不存在，未按名称选择其他交换机。", ApiErrorKind.NotFound);
                switchPath = targetSwitch.Path.Path;
            }

            using var portSearcher = new ManagementObjectSearcher(service.Scope,
                new ObjectQuery($"SELECT * FROM {portClass} WHERE InstanceID LIKE '%Default%'"));
            using var ports = portSearcher.Get();
            using var port = ports.Cast<ManagementObject>().FirstOrDefault();
            if (port is null) return BeforeCreation(Properties.Resources.Error_Net_TemplateNotFound, ApiErrorKind.NotFound);
            using var allocationSearcher = new ManagementObjectSearcher(service.Scope,
                new ObjectQuery($"SELECT * FROM {VmNetworkAdapterCreation.AllocationClass} WHERE InstanceID LIKE '%Default%'"));
            using var allocations = allocationSearcher.Get();
            using var allocation = allocations.Cast<ManagementObject>().FirstOrDefault();
            if (allocation is null) return BeforeCreation("未找到网卡连接资源模板。", ApiErrorKind.NotFound);

            // 两个模板都在第一次写入前准备，避免已知前置条件缺失后再创建半成品。
            port["ElementName"] = Properties.Resources.Net_DefaultAdapterName;
            if (normalizedMac is not null)
            {
                port["StaticMacAddress"] = true;
                port["Address"] = normalizedMac;
            }
            string portXml = port.GetText(TextFormat.CimDtd20);
            string configurationPath = settings.Path.Path;
            workflowStarted = true;
            return await VmNetworkAdapterCreation.ExecuteAsync(vmId,
                () => WmiApi.InvokeCreationAsync(ServiceWql, "AddResourceSettings", portClass, input =>
                {
                    input["AffectedConfiguration"] = configurationPath;
                    input["ResourceSettings"] = new[] { portXml };
                }),
                path =>
                {
                    try
                    {
                        allocation["Parent"] = path;
                        if (applyConnectionOptions)
                        {
                            allocation["EnabledState"] = (ushort)(isConnected ? 2 : 3);
                            if (switchPath is not null)
                                allocation["HostResource"] = new[] { switchPath };
                        }
                        string xml = allocation.GetText(TextFormat.CimDtd20);
                        return WmiApi.InvokeCreationAsync(ServiceWql, "AddResourceSettings", VmNetworkAdapterCreation.AllocationClass, input =>
                        {
                            input["AffectedConfiguration"] = configurationPath;
                            input["ResourceSettings"] = new[] { xml };
                        });
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(new WmiCreationResult(ApiResponse.Fail(ex.Message,
                            exception: ex, kind: ApiErrorKind.OperationFailed), [], false));
                    }
                },
                () => ReadCreatedNetworkResourcesAsync(vmId),
                path => WmiApi.InvokeAsync(ServiceWql, "RemoveResourceSettings", input => input["ResourceSettings"] = new[] { path }),
                portClass);
        }
        catch (Exception ex)
        {
            if (workflowStarted)
                return new(false, null, VmNetworkAdapterCreationState.Unknown,
                    VmNetworkAdapterInitializationState.Unknown, [], ex.Message,
                    "创建工作流异常结束，无法确认相关资源状态，未继续自动撤销。", ApiErrorKind.OutcomeUnknown);
            return BeforeCreation(ex.Message, ApiErrorKind.OperationFailed);
        }
    }

    private static Task<ApiResponse<List<VmCreatedNetworkResource>>> ReadCreatedNetworkResourcesAsync(Guid vmId) => Task.Run(() =>
    {
        try
        {
            using var vm = WmiApi.GetVmComputerSystem(vmId);
            if (vm is null)
                return ApiResponse<List<VmCreatedNetworkResource>>.Fail("目标虚拟机已不存在，无法确认原创建资源的状态。", kind: ApiErrorKind.NotFound);
            using var settings = WmiApi.GetVmSettings(vm);
            if (settings is null)
                return ApiResponse<List<VmCreatedNetworkResource>>.Fail("无法读取当前虚拟机配置。", kind: ApiErrorKind.OperationFailed);

            var resources = new List<(VmCreatedNetworkResource Resource, string Parent)>();
            foreach (string className in new[] { VmNetworkAdapterCreation.PortClass, VmNetworkAdapterCreation.EmulatedPortClass, VmNetworkAdapterCreation.AllocationClass })
            {
                using var related = settings.GetRelated(className, "Msvm_VirtualSystemSettingDataComponent",
                    null, null, null, null, false, null);
                foreach (ManagementObject item in related)
                {
                    using (item)
                    {
                        if (!string.Equals(item.ClassPath.ClassName, className, StringComparison.Ordinal))
                            throw new InvalidOperationException("网卡资源类型与查询目标不一致。");
                        string instanceId = item["InstanceID"]?.ToString() ?? string.Empty;
                        if (!instanceId.StartsWith($"Microsoft:{vmId:D}\\", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("网卡资源身份与当前虚拟机不一致。");
                        var featurePaths = new List<string>();
                        if (className == VmNetworkAdapterCreation.AllocationClass)
                        {
                            using var features = item.GetRelated("Msvm_EthernetSwitchPortFeatureSettingData",
                                "Msvm_EthernetPortSettingDataComponent", null, null, null, null, false, null);
                            foreach (ManagementObject feature in features)
                                using (feature) featurePaths.Add(feature.Path.Path);
                        }
                        string fingerprint = JsonSerializer.Serialize(item.Properties.Cast<PropertyData>()
                            .OrderBy(p => p.Name, StringComparer.Ordinal)
                            .Select(p => new NetworkPropertyFingerprint(p.Name, p.Type.ToString(), p.IsArray, CimJsonValue.FromProperty(p.Value)))
                            .ToArray(), CoreJson.Default.NetworkPropertyFingerprintArray);
                        resources.Add((new(item.Path.Path, instanceId, className, fingerprint, featurePaths.ToArray()),
                            item["Parent"]?.ToString() ?? string.Empty));
                    }
                }
            }
            return ApiResponse<List<VmCreatedNetworkResource>>.Ok(resources.Select(item => item.Resource with
            {
                Children = item.Resource.Children.Concat(resources.Where(child =>
                    VmNetworkAdapterCreation.SamePath(child.Parent, item.Resource.Path)).Select(child => child.Resource.Path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            }).ToList());
        }
        catch (Exception ex)
        {
            return ApiResponse<List<VmCreatedNetworkResource>>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    });
}
