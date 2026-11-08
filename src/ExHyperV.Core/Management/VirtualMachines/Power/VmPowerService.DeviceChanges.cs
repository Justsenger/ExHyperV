using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmPowerService
{

        internal static async Task<ApiResponse> EnsureStoppedForDeviceChangeAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            async Task<ApiResponse<ushort>> ReadState() => await WmiApi.QueryFirstAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => Convert.ToUInt16(obj["EnabledState"]));
            var state = await ReadState();
            if (!state.Success) return ApiResponse.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);
            if (!state.HasData) return ApiResponse.Fail("找不到指定虚拟机。", kind: ApiErrorKind.NotFound);
            if (state.Data == 3) return ApiResponse.Ok();
            var off = await VmPowerService.ExecuteControlActionAsync(vmId, VmPowerAction.TurnOff);
            if (!off.Success) return off;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(1000);
                state = await ReadState();
                if (!state.Success) return ApiResponse.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);
                if (!state.HasData) return ApiResponse.Fail("等待关机时虚拟机已不可查询。", kind: ApiErrorKind.NotFound);
                if (state.Data == 3) return ApiResponse.Ok();
            }
            return ApiResponse.Fail("未能在限定时间内确认虚拟机已完全关机。", kind: ApiErrorKind.InvalidState);
        }

}
