using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services;

// ConfigurationId 是当前 VSSD 的 InstanceID，不是 VM 名称或 VM GUID。
internal sealed record VmCpuGroupBinding(
    Guid VmId, string ConfigurationId, string ProcessorInstanceId, Guid GroupId);

internal static class VmCpuGroupBindingStore
{
    private const string Realized = "Microsoft:Hyper-V:System:Realized";
    private const string Definition = "Microsoft:Hyper-V:System:Definition";

    internal static async Task<ApiResponse<VmCpuGroupBinding>> ReadAsync(Guid vmId)
    {
        var result = await ReadCurrentAsync(vmId).ConfigureAwait(false);
        return result.HasData
            ? ApiResponse<VmCpuGroupBinding>.Ok(result.Data.Binding)
            : ApiResponse<VmCpuGroupBinding>.Fail(result.Error, result.Code,
                result.ErrorSource, result.Exception, result.ErrorKind);
    }

    internal static async Task<ApiResponse> SetAsync(VmCpuGroupBinding expected, Guid targetGroupId)
    {
        if (expected is null || expected.VmId == Guid.Empty ||
            string.IsNullOrWhiteSpace(expected.ConfigurationId) ||
            string.IsNullOrWhiteSpace(expected.ProcessorInstanceId))
        {
            return ApiResponse.Fail("CPU 组绑定缺少有效的虚拟机或配置身份。",
                kind: ApiErrorKind.InvalidArgument);
        }

        // 重新读取当前关联对象；不能使用第一次读取时缓存的 XML 覆盖后来更改的处理器设置。
        var current = await ReadCurrentAsync(expected.VmId, expected, targetGroupId).ConfigureAwait(false);
        if (!current.HasData)
            return ApiResponse.Fail(current.Error, current.Code,
                current.ErrorSource, current.Exception, current.ErrorKind);

        if (current.Data.Xml is null)
            return ApiResponse.Ok();

        // 检查与提交不是 WMI 原子比较交换；调用方仍须持有本次 VM 操作的协调锁。
        // 原样返回提交结果，尤其不能把 OutcomeUnknown 当作确定失败并触发回滚。
        return await WmiApi.InvokeAsync(
            "SELECT * FROM Msvm_VirtualSystemManagementService",
            "ModifyResourceSettings",
            parameters => parameters["ResourceSettings"] = new[] { current.Data.Xml })
            .ConfigureAwait(false);
    }

    private sealed record CurrentBinding(VmCpuGroupBinding Binding, string? Xml);

    private static async Task<ApiResponse<CurrentBinding>> ReadCurrentAsync(
        Guid vmId, VmCpuGroupBinding? expected = null, Guid? targetGroupId = null)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<CurrentBinding>.Fail("虚拟机 GUID 不能为空。",
                kind: ApiErrorKind.InvalidArgument);

        var query = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
            vm => ReadCurrent(vm, vmId, expected, targetGroupId)).ConfigureAwait(false);

        if (!query.Success)
            return ApiResponse<CurrentBinding>.Fail(query.Error, query.Code,
                query.ErrorSource, query.Exception, query.ErrorKind);
        if (!query.HasData || query.Data.Count == 0)
            return ApiResponse<CurrentBinding>.Fail("未找到指定虚拟机。", kind: ApiErrorKind.NotFound);
        if (query.Data.Count != 1)
            return ApiResponse<CurrentBinding>.Fail("虚拟机 GUID 匹配了多个对象，未修改 CPU 组绑定。",
                kind: ApiErrorKind.AmbiguousTarget);
        return query.Data[0];
    }

    private static ApiResponse<CurrentBinding> ReadCurrent(
        ManagementObject vm, Guid vmId, VmCpuGroupBinding? expected, Guid? targetGroupId)
    {
        if (!Guid.TryParse(vm["Name"]?.ToString(), out var actualVmId) || actualVmId != vmId)
            return ApiResponse<CurrentBinding>.Fail("查询返回的虚拟机身份与请求不符。",
                kind: ApiErrorKind.Conflict);

        using var configurationCollection = vm.GetRelated(
            "Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState",
            null, null, null, null, false, null);
        var configurations = new List<ManagementObject>();
        try
        {
            foreach (ManagementObject configuration in configurationCollection)
                configurations.Add(configuration);

            // 只接受当前配置的两种类型；检查点及其它设置不参与候选。
            var realized = configurations.Where(item =>
                string.Equals(item["VirtualSystemType"]?.ToString(), Realized, StringComparison.Ordinal)).ToList();
            var candidates = realized.Count > 0 ? realized : configurations.Where(item =>
                string.Equals(item["VirtualSystemType"]?.ToString(), Definition, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0)
                return ApiResponse<CurrentBinding>.Fail("未找到虚拟机的当前配置。",
                    kind: ApiErrorKind.NotFound);
            if (candidates.Count != 1)
                return ApiResponse<CurrentBinding>.Fail("虚拟机存在多个当前配置，无法确定 CPU 组绑定。",
                    kind: ApiErrorKind.AmbiguousTarget);

            var settings = candidates[0];
            string? configurationId = settings["InstanceID"]?.ToString();
            if (string.IsNullOrWhiteSpace(configurationId) ||
                !Guid.TryParse(settings["VirtualSystemIdentifier"]?.ToString(), out var configurationVmId) ||
                configurationVmId != vmId)
            {
                return ApiResponse<CurrentBinding>.Fail("当前配置缺少有效身份或不属于指定虚拟机。",
                    kind: ApiErrorKind.InvalidState);
            }

            using var processorCollection = settings.GetRelated(
                "Msvm_ProcessorSettingData", "Msvm_VirtualSystemSettingDataComponent",
                null, null, null, null, false, null);
            var processors = new List<ManagementObject>();
            try
            {
                foreach (ManagementObject processor in processorCollection)
                    processors.Add(processor);
                if (processors.Count == 0)
                    return ApiResponse<CurrentBinding>.Fail("当前配置没有处理器设置。",
                        kind: ApiErrorKind.NotFound);
                if (processors.Count != 1)
                    return ApiResponse<CurrentBinding>.Fail("当前配置存在多个处理器设置，无法确定 CPU 组绑定。",
                        kind: ApiErrorKind.AmbiguousTarget);

                var processorSettings = processors[0];
                string? processorId = processorSettings["InstanceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(processorId))
                    return ApiResponse<CurrentBinding>.Fail("处理器设置缺少有效身份。",
                        kind: ApiErrorKind.InvalidState);
                if (!processorSettings.Properties.Cast<PropertyData>().Any(property =>
                    string.Equals(property.Name, "CpuGroupId", StringComparison.OrdinalIgnoreCase)))
                {
                    return ApiResponse<CurrentBinding>.Fail("当前处理器设置不支持 CpuGroupId。",
                        kind: ApiErrorKind.Unsupported);
                }
                // 提供程序文档以字符串 "0" 表示未绑定；也接受完整的全零 GUID。
                string? groupValue = processorSettings["CpuGroupId"]?.ToString();
                Guid groupId = Guid.Empty;
                if (groupValue != "0" && !Guid.TryParse(groupValue, out groupId))
                    return ApiResponse<CurrentBinding>.Fail("CpuGroupId 为空或不是有效 GUID，无法确认当前绑定。",
                        kind: ApiErrorKind.InvalidState);

                var binding = new VmCpuGroupBinding(vmId, configurationId, processorId, groupId);
                if (expected is not null &&
                    (expected.VmId != binding.VmId ||
                     !string.Equals(expected.ConfigurationId, binding.ConfigurationId, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(expected.ProcessorInstanceId, binding.ProcessorInstanceId, StringComparison.OrdinalIgnoreCase) ||
                     expected.GroupId != binding.GroupId))
                {
                    return ApiResponse<CurrentBinding>.Fail("虚拟机的当前配置或 CPU 组绑定已改变，未提交修改。",
                        kind: ApiErrorKind.Conflict);
                }

                string? xml = null;
                if (targetGroupId is { } target && target != groupId)
                {
                    // 全零 GUID 表示解除绑定；仅更改新读取对象的这一项属性。
                    processorSettings["CpuGroupId"] = target.ToString("D");
                    xml = processorSettings.GetText(TextFormat.CimDtd20);
                }
                return ApiResponse<CurrentBinding>.Ok(new CurrentBinding(binding, xml));
            }
            finally
            {
                foreach (var processor in processors)
                    processor.Dispose();
            }
        }
        finally
        {
            foreach (var configuration in configurations)
                configuration.Dispose();
        }
    }
}
