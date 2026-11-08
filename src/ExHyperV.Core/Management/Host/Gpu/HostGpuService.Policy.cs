using Microsoft.Win32;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public static partial class HostGpuService
    {
        private const string GpuAssignmentPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\HyperV";

        /// <summary>读取宿主是否已放宽 GPU 分配限制。</summary>
        /// <returns>成功时两个策略均为 DWORD 0 才返回 true；未配置或其他值返回 false，读取异常返回失败。</returns>
        public static ApiResponse<bool> GetAssignmentRestrictionsRelaxed() => ReadPolicy(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(GpuAssignmentPolicyKey);
            return (key?.GetValue("RequireSecureDeviceAssignment"), key?.GetValue("RequireSupportedDeviceAssignment"));
        });

        internal static ApiResponse<bool> ReadPolicy(Func<(object? Secure, object? Supported)> read)
        {
            try
            {
                var values = read();
                return ApiResponse<bool>.Ok(values.Secure is int secure && secure == 0
                    && values.Supported is int supported && supported == 0);
            }
            catch (Exception ex)
            {
                return ApiResponse<bool>.Fail(ex.Message, exception: ex, kind: FailureKind(ex));
            }
        }

        private static ApiErrorKind FailureKind(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
            ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed;

        /// <summary>设置是否放宽宿主 GPU 分配限制。</summary>
        /// <param name="relaxed">true 放宽安全和设备支持限制；false 删除这两个覆盖值，恢复系统默认策略。</param>
        /// <returns>两个策略值均完成修改时成功；否则返回失败原因。</returns>
        /// <remarks>写入或删除可能部分完成；第二个值失败时不撤销第一个值。false 表示恢复默认，不恢复此前自定义的值。通过 <see cref="HostGpuService.GetAssignmentRestrictionsRelaxed"/> 读取是否已放宽限制。</remarks>
        public static ApiResponse SetAssignmentRestrictionsRelaxed(bool relaxed) => ChangePolicy(() =>
            {
                if (relaxed)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(GpuAssignmentPolicyKey);
                    if (key == null) throw new InvalidOperationException("无法打开 GPU 分配的主机策略项。");
                    key.SetValue("RequireSecureDeviceAssignment", 0, RegistryValueKind.DWord);
                    key.SetValue("RequireSupportedDeviceAssignment", 0, RegistryValueKind.DWord);
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(GpuAssignmentPolicyKey, writable: true);
                    if (key == null) return;
                    key.DeleteValue("RequireSecureDeviceAssignment", throwOnMissingValue: false);
                    key.DeleteValue("RequireSupportedDeviceAssignment", throwOnMissingValue: false);
                }
            });


        internal static ApiResponse ChangePolicy(Action change)
        {
            try
            {
                change();
                return ApiResponse.Ok();
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, exception: ex, kind: FailureKind(ex));
            }
        }
    }
}
