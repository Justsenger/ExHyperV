using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public readonly record struct VmBatteryState(bool Available, bool Enabled);

/// <summary>通过增删 Msvm_BatterySettingData 为虚拟机提供合成电池设备。</summary>
public static class VmBatteryService
{
    private const string BatteryClass = "Msvm_BatterySettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string RealizedType = "Microsoft:Hyper-V:System:Realized";

    public static async Task<ApiResponse<VmBatteryState>> GetStateAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<VmBatteryState>.Fail(Properties.Resources.Error_Net_VmNotFound);

        string vmIdText = WmiApi.Escape(vmId.ToString("D"));
        var vmResult = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE Name = '{vmIdText}'",
            _ => true);
        if (!vmResult.Success)
            return ForwardFailure<bool, VmBatteryState>(vmResult);
        if (!vmResult.HasData)
            return ApiResponse<VmBatteryState>.Fail(Properties.Resources.Error_Net_VmNotFound);

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

    public static async Task<ApiResponse> SetEnabledAsync(Guid vmId, bool enabled)
    {
        if (vmId == Guid.Empty)
            return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound);

        string vmIdText = WmiApi.Escape(vmId.ToString("D"));
        var vmResult = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE Name = '{vmIdText}'",
            _ => true);
        if (!vmResult.Success)
            return ForwardFailure(vmResult);
        if (!vmResult.HasData)
            return ApiResponse.Fail(Properties.Resources.Error_Net_VmNotFound);

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
        => ApiResponse.Fail(response.Error, response.Code, response.ErrorSource);

    private static ApiResponse<TTarget> ForwardFailure<TSource, TTarget>(
        ApiResponse<TSource> response)
        => ApiResponse<TTarget>.Fail(response.Error, response.Code, response.ErrorSource);
}
