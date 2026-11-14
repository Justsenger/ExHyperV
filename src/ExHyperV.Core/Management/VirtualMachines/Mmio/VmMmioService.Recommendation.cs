using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmMmioService
{

        /// <summary>比较指定虚拟机当前高 MMIO 空间与主机分配要求。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>成功时为满足或需要扩展；读取失败返回错误，不当作空间不足。</returns>
        /// <queryentry label="MMIO 空间检查" category="MMIO"/>
        public static async Task<ApiResponse<MmioCheckResultType>> CheckMmioSpaceAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse<MmioCheckResultType>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var response = await WmiApi.QueryFirstAsync(
                $"SELECT HighMmioGapSize FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                obj => Convert.ToUInt64(obj["HighMmioGapSize"]));
            if (!response.Success) return ApiResponse<MmioCheckResultType>.Fail(response.Error, response.Code,
                response.ErrorSource, response.Exception, response.ErrorKind);
            if (!response.HasData) return ApiResponse<MmioCheckResultType>.Fail("找不到指定虚拟机的 MMIO 配置。", kind: ApiErrorKind.NotFound);
            ulong requiredMiB = VmMmioService.ComputeMmioPlan()?.HighSizeMiB ?? VmMmioService.DefaultHighSizeMiB;
            return ApiResponse<MmioCheckResultType>.Ok(response.Data < requiredMiB
                ? MmioCheckResultType.NeedsExpansion : MmioCheckResultType.Ok);
        }


        /// <summary>确保指定虚拟机完全关机后应用主机推荐的 MMIO 配置。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>关机及配置结果；失败前可能已经关机或完成部分探测。</returns>
        /// <remarks>先强制关闭目标虚拟机，再配置 MMIO；探测期间可能反复启停，结束时保持关闭。执行前确认允许这些电源操作。</remarks>
        /// <editentry label="应用推荐 MMIO 配置" category="MMIO"/>
        public static async Task<ApiResponse> UpdateMmioSpaceAsync(Guid vmId)
        {
            var stopped = await VmPowerService.EnsureStoppedForDeviceChangeAsync(vmId);
            return stopped.Success ? await VmMmioService.ConfigureMmioAsync(vmId) : stopped;
        }

}
