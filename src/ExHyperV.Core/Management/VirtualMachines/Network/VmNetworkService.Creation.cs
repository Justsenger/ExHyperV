using ExHyperV.Tools;
using System.Management;
using System.Text.Json;

namespace ExHyperV.Services;

public static partial class VmNetworkService
{
    private static async Task<VmNetworkAdapterCreationResult> CreateNetworkAdapterAsync(Guid vmId)
    {
        bool workflowStarted = false;
        static VmNetworkAdapterCreationResult BeforeCreation(string error, ApiErrorKind kind) =>
            new(false, null, VmNetworkAdapterCreationState.NotCreated,
                VmNetworkAdapterInitializationState.NotStarted, [], error, ErrorKind: kind);
        try
        {
            if (vmId == Guid.Empty) return BeforeCreation("虚拟机 GUID 不能为空。", ApiErrorKind.InvalidArgument);
            using var vm = WmiApi.GetVmComputerSystem(vmId);
            if (vm is null) return BeforeCreation(Properties.Resources.Error_Net_VmNotFound, ApiErrorKind.NotFound);
            using var service = WmiApi.GetVirtualSystemManagementService();
            using var portSearcher = new ManagementObjectSearcher(service.Scope,
                new ObjectQuery($"SELECT * FROM {VmNetworkAdapterCreation.PortClass} WHERE InstanceID LIKE '%Default%'"));
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
            string portXml = port.GetText(TextFormat.CimDtd20);
            string configurationPath = vm.Path.Path;
            workflowStarted = true;
            return await VmNetworkAdapterCreation.ExecuteAsync(vmId,
                () => WmiApi.InvokeCreationAsync(ServiceWql, "AddResourceSettings", VmNetworkAdapterCreation.PortClass, input =>
                {
                    input["AffectedConfiguration"] = configurationPath;
                    input["ResourceSettings"] = new[] { portXml };
                }),
                path =>
                {
                    try
                    {
                        allocation["Parent"] = path;
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
                path => WmiApi.InvokeAsync(ServiceWql, "RemoveResourceSettings", input => input["ResourceSettings"] = new[] { path }));
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
            foreach (string className in new[] { VmNetworkAdapterCreation.PortClass, VmNetworkAdapterCreation.AllocationClass })
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
                            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new { p.Name, Type = p.Type.ToString(), p.IsArray, p.Value }));
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
