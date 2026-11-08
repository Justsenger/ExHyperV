using ExHyperV.Models;
using ExHyperV.Tools;
namespace ExHyperV.Services;

// 仅供领域服务提交已知的虚拟机布尔配置，不向调用方开放任意 WMI 属性。
internal static class VmConfigurationSwitch
{
 internal static async Task<ApiResponse> SetAsync(Guid vmId, string propertyName, bool enabled, bool requiresStopped = false)
    {
        if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        if (requiresStopped)
        {
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData) return ApiResponse.Fail(state.Success ? Properties.Resources.Error_Net_VmNotFound : state.Error,
                state.Code, state.ErrorSource, state.Exception, state.Success ? ApiErrorKind.NotFound : state.ErrorKind);
            if (!VmPowerService.CanEditHibernation(true, state.Data!.IsRunning)) return ApiResponse.Fail(Properties.Resources.Warn_VmOffRequired, kind: ApiErrorKind.InvalidState);
        }

        return await WmiApi.WithObjectAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj =>
            {
                if (!obj.HasProperty(propertyName))
                    throw new InvalidOperationException(Properties.Resources.Error_VerNotSupport);

                obj[propertyName] = enabled;
            });
    }

}
