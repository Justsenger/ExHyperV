using Microsoft.Win32;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>管理主机原生 NVMe 功能的注册表覆盖开关；配置需要管理员权限并在重启后由系统采用。</summary>
    public static class HostNvmeService
    {
        private const string OverridesKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
        private const string FeatureValue = "1176759950"; // KB5066835 原生 NVMe 的 Feature ID

        /// <summary>判断原生 NVMe 开关是否满足 Windows build ≥ 26100 的版本条件。</summary>
        /// <returns>构建号至少为 26100 时为 true。</returns>
        /// <param name="buildNumber">待判断的 Windows 构建号；null 使用当前系统构建号。</param>
        /// <remarks>此结果只检查构建号；驱动实际采用情况由 Windows 决定。</remarks>
        public static bool IsNativeNvmeSupported(int? buildNumber = null) =>
            (buildNumber ?? Environment.OSVersion.Version.Build) >= 26100;

        internal static void EnsureNativeNvmeSupported(int? buildNumber = null)
        {
            if (!IsNativeNvmeSupported(buildNumber)) throw new PlatformNotSupportedException();
        }

        /// <summary>读取原生 NVMe 的启用配置；修改此开关要求 Windows build ≥ 26100。</summary>
        /// <returns>成功时覆盖 DWORD 为 1 返回 true，不存在或其他值返回 false；读取失败返回错误。</returns>
        /// <remarks>读取保存的启用配置；修改通过 SetNativeNvmeEnabled 提交，重启后由系统采用。</remarks>
        public static ApiResponse<bool> IsNativeNvmeEnabled() => ReadOverride(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(OverridesKey);
            return key?.GetValue(FeatureValue);
        });

        internal static ApiResponse<bool> ReadOverride(Func<object?> read)
        {
            try { return ApiResponse<bool>.Ok(read() is int value && value == 1); }
            catch (Exception ex)
            {
                return ApiResponse<bool>.Fail(ex.Message, exception: ex,
                    kind: ex is UnauthorizedAccessException or System.Security.SecurityException
                        ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
            }
        }

        /// <summary>设置原生 NVMe 开关，供系统重启后采用。</summary>
        /// <param name="enabled">true 写入启用配置；false 删除启用配置并恢复系统默认，不强制关闭系统可能默认启用的功能。</param>
        /// <returns>配置写入结果；修改后需要重启。</returns>
        /// <remarks>可用条件为 Windows build ≥ 26100；Core 在访问注册表前执行检查。不满足时抛出 PlatformNotSupportedException；注册表操作失败通过响应返回。配置需重启后由系统采用。</remarks>
        /// <exception cref="PlatformNotSupportedException">当前系统构建号不满足应用提供开关的规则。</exception>
        public static ApiResponse SetNativeNvmeEnabled(bool enabled) => ChangeOverride(() =>
            {
                if (enabled)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(OverridesKey);
                    if (key == null) throw new InvalidOperationException("无法打开原生 NVMe 的主机配置项。");
                    key.SetValue(FeatureValue, 1, RegistryValueKind.DWord);
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(OverridesKey, writable: true);
                    key?.DeleteValue(FeatureValue, throwOnMissingValue: false);
                }
            });


        internal static ApiResponse ChangeOverride(Action change, int? buildNumber = null)
        {
            EnsureNativeNvmeSupported(buildNumber);
            try
            {
                change();
                return ApiResponse.Ok();
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, exception: ex, kind: ex is UnauthorizedAccessException or System.Security.SecurityException
                    ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
            }
        }
    }
}
