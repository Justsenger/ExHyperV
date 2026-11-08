using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;


public static partial class VmPowerService
{
    private const string BatteryClass = "Msvm_BatterySettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string RealizedType = "Microsoft:Hyper-V:System:Realized";

    /// <summary>读取虚拟电池设备是否可用及是否已添加到指定虚拟机。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。</param>
    /// <returns>有效虚拟机上缺少电池 WMI 支持时返回 Available=false；查询失败通过响应错误返回。</returns>
    public static async Task<ApiResponse<VmBatteryState>> GetBatteryStateAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<VmBatteryState>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.InvalidArgument);

        string vmIdText = WmiApi.Escape(vmId.ToString("D"));
        var vmResult = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE Name = '{vmIdText}'",
            _ => true);
        if (!vmResult.Success)
            return ForwardFailure<bool, VmBatteryState>(vmResult);
        if (!vmResult.HasData)
            return ApiResponse<VmBatteryState>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

        var templateResult = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM {BatteryClass} WHERE InstanceID LIKE '%Default%'",
            _ => true);
        if (!templateResult.Success)
        {
            if (IsInvalidClass(templateResult))
                return ApiResponse<VmBatteryState>.Ok(new VmBatteryState(false, false));

            return ForwardFailure<bool, VmBatteryState>(templateResult);
        }

        if (!templateResult.HasData)
            return ApiResponse<VmBatteryState>.Ok(new VmBatteryState(false, false));

        var batteryResult = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM {BatteryClass} " +
            $"WHERE InstanceID LIKE 'Microsoft:{vmIdText}%'",
            _ => true);
        if (!batteryResult.Success)
            return ForwardFailure<bool, VmBatteryState>(batteryResult);

        return ApiResponse<VmBatteryState>.Ok(
            new VmBatteryState(Available: true, Enabled: batteryResult.HasData));
    }

    /// <summary>添加或移除虚拟电池设备；设备已处于目标状态时不重复修改。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID；字符串形式也应使用 GUID，而非显示名称。</param>
    /// <param name="enabled">true 请求启用该功能，false 请求关闭；互锁限制由对应操作执行。</param>
    /// <returns>修改或无需修改的成功响应；虚拟机不存在、主机不支持或底层失败时返回错误。</returns>
    public static async Task<ApiResponse> SetBatteryEnabledAsync(Guid vmId, bool enabled)
    {
        if (vmId == Guid.Empty)
            return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.InvalidArgument);

        string vmIdText = WmiApi.Escape(vmId.ToString("D"));
        var vmResult = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE Name = '{vmIdText}'",
            _ => true);
        if (!vmResult.Success)
            return ForwardFailure(vmResult);
        if (!vmResult.HasData)
            return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

        var batteryResult = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM {BatteryClass} " +
            $"WHERE InstanceID LIKE 'Microsoft:{vmIdText}%'",
            obj => obj.Path.Path);
        if (!batteryResult.Success)
            return IsInvalidClass(batteryResult)
                ? Unsupported(batteryResult)
                : ForwardFailure(batteryResult);

        if (enabled)
        {
            if (batteryResult.HasData)
                return ApiResponse.Ok();

            var settingsResult = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemSettingData " +
                $"WHERE VirtualSystemIdentifier = '{vmIdText}' " +
                $"AND VirtualSystemType = '{RealizedType}'",
                obj => obj.Path.Path);
            if (!settingsResult.Success)
                return ForwardFailure(settingsResult);
            if (!settingsResult.HasData)
                return ApiResponse.Fail(Properties.Resources.Error_Cpu_ConfigNotFound);

            var templateResult = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM {BatteryClass} WHERE InstanceID LIKE '%Default%'",
                obj => obj.GetText(TextFormat.CimDtd20));
            if (!templateResult.Success)
                return IsInvalidClass(templateResult)
                    ? Unsupported(templateResult)
                    : ForwardFailure(templateResult);
            if (!templateResult.HasData)
                return ApiResponse.Fail(Properties.Resources.Error_VerNotSupport);

            return await WmiApi.InvokeAsync(ServiceWql, "AddResourceSettings", p =>
            {
                p["AffectedConfiguration"] = settingsResult.Data!;
                p["ResourceSettings"] = new[] { templateResult.Data! };
            });
        }

        if (!batteryResult.HasData)
            return ApiResponse.Ok();

        return await WmiApi.InvokeAsync(
            ServiceWql,
            "RemoveResourceSettings",
            p => p["ResourceSettings"] = new[] { batteryResult.Data! });
    }

    private static bool IsInvalidClass<T>(ApiResponse<T> response)
        => !response.Success
           && response.ErrorSource == ApiErrorSource.Wmi
           && response.Code == (int)ManagementStatus.InvalidClass;

    private static ApiResponse Unsupported<T>(ApiResponse<T> response)
        => ApiResponse.Fail(
            Properties.Resources.Error_VerNotSupport,
            response.Code,
            response.ErrorSource);

    private static ApiResponse ForwardFailure<T>(ApiResponse<T> response)
        => ApiResponse.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

    private static ApiResponse<TTarget> ForwardFailure<TSource, TTarget>(
        ApiResponse<TSource> response)
        => ApiResponse<TTarget>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
}
