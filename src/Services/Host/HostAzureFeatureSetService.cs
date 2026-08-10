using CommunityToolkit.Mvvm.Messaging;
using ExHyperV.Messages;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>
/// 管理 Hyper-V 的宿主机全局 Azure 功能集开关。
/// </summary>
public static class HostAzureFeatureSetService
{
    private const string VirtualizationKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
    private const string AzureFeatureSetValue = "AzureFeatureSet";
    private static readonly SemaphoreSlim TransientChangeLock = new(1, 1);

    public static bool IsEnabled()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(VirtualizationKey);
            return key?.GetValue(AzureFeatureSetValue) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public static (bool Success, string Error) SetEnabled(bool enabled)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            if (enabled)
            {
                using var key = baseKey.CreateSubKey(VirtualizationKey, writable: true);
                if (key == null)
                    return (false, Properties.Resources.Error_Host_AzureFeatureSetRegistryUnavailable);

                key.SetValue(AzureFeatureSetValue, 1, RegistryValueKind.DWord);
            }
            else
            {
                using var key = baseKey.OpenSubKey(VirtualizationKey, writable: true);
                key?.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);
            }

            WeakReferenceMessenger.Default.Send(new AzureFeatureSetChangedMessage(enabled));
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<T> RunTemporarilyDisabledAsync<T>(Func<Task<T>> action)
    {
        await TransientChangeLock.WaitAsync();
        object? originalValue = null;
        RegistryValueKind originalKind = RegistryValueKind.Unknown;
        bool valueRemoved = false;
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(VirtualizationKey, writable: true))
            {
                originalValue = key?.GetValue(AzureFeatureSetValue, null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (originalValue != null && key != null)
                {
                    originalKind = key.GetValueKind(AzureFeatureSetValue);
                    key.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);
                    key.Flush();
                    valueRemoved = true;
                }
            }
            return await action();
        }
        finally
        {
            try
            {
                if (valueRemoved && originalValue != null)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.CreateSubKey(VirtualizationKey, writable: true);
                    key?.SetValue(AzureFeatureSetValue, originalValue, originalKind);
                    key?.Flush();
                }
            }
            finally { TransientChangeLock.Release(); }
        }
    }
}
