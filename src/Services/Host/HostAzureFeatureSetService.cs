using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>
/// 临时管理 Hyper-V 内部的 Azure 功能暂存开关。
/// 该开关会改变整个宿主机上的 VMMS/VMWP 行为，因此不得作为应用设置长期启用。
/// </summary>
public static class HostAzureFeatureSetService
{
    private const string VirtualizationKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
    private const string AzureFeatureSetValue = "AzureFeatureSet";

    // 注册表状态作用于整个宿主机。串行执行本进程内的临时切换，
    // 避免一个操作在另一个操作尚未结束时提前恢复状态。
    private static readonly SemaphoreSlim TransientChangeLock = new(1, 1);

    /// <summary>
    /// 清理由旧版 ExHyperV 或异常中断的临时操作遗留的注册表值。
    /// 在应用启动和退出时调用。
    /// </summary>
    public static void EnsureDisabledAtRest()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(VirtualizationKey, writable: true);
            key?.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);
            key?.Flush();
        }
        catch
        {
            // 生命周期清理采用尽力而为策略；真正依赖该开关的操作会通过
            // RunWithTemporaryStateAsync 向上报告注册表错误。
        }
    }

    public static Task<T> RunTemporarilyEnabledAsync<T>(Func<Task<T>> action) =>
        RunWithTemporaryStateAsync(enabled: true, action);

    public static Task<T> RunTemporarilyDisabledAsync<T>(Func<Task<T>> action) =>
        RunWithTemporaryStateAsync(enabled: false, action);

    private static async Task<T> RunWithTemporaryStateAsync<T>(
        bool enabled, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await TransientChangeLock.WaitAsync();

        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.CreateSubKey(VirtualizationKey, writable: true))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        Properties.Resources.Error_Host_AzureFeatureSetRegistryUnavailable);

                if (enabled)
                    key.SetValue(AzureFeatureSetValue, 1, RegistryValueKind.DWord);
                else
                    key.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);

                key.Flush();
            }

            return await action();
        }
        finally
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.CreateSubKey(VirtualizationKey, writable: true);
                if (key == null)
                    throw new InvalidOperationException(
                        Properties.Resources.Error_Host_AzureFeatureSetRegistryUnavailable);

                // 这是由 ExHyperV 管理的临时租约，而不是持久设置。
                // 操作结束后不得恢复旧版本或外部遗留的启用值。
                key.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);
                key.Flush();
            }
            finally
            {
                TransientChangeLock.Release();
            }
        }
    }
}
