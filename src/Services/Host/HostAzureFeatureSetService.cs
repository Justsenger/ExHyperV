using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>
/// Provides short-lived access to Hyper-V's internal Azure feature staging flag.
/// The flag changes host-wide VMMS/VMWP behavior, so it must never be left enabled
/// as an application setting.
/// </summary>
public static class HostAzureFeatureSetService
{
    private const string VirtualizationKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
    private const string AzureFeatureSetValue = "AzureFeatureSet";

    // Registry state is host-wide. Serialize every temporary state change made by
    // this process so one operation cannot restore the state underneath another.
    private static readonly SemaphoreSlim TransientChangeLock = new(1, 1);

    /// <summary>
    /// Removes a value left behind by an older ExHyperV build or an interrupted
    /// temporary operation. Called at application startup and shutdown.
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
            // Best effort during process lifecycle. An actual dependent operation
            // reports registry failures through RunWithTemporaryStateAsync.
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

                // This is an ExHyperV-owned staging lease, not a setting. Never
                // restore a legacy/external enabled value after the operation.
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
