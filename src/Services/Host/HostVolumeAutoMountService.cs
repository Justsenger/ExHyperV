using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ExHyperV.Services;

public readonly record struct VolumeAutoMountState(bool Success, bool Enabled, string Error);

/// <summary>
/// 管理 Windows Mount Manager 的主机全局自动挂载行为。
/// </summary>
public static class HostVolumeAutoMountService
{
    private const string MountManagerKey = @"SYSTEM\CurrentControlSet\Services\mountmgr";
    private const string NoAutoMountValue = "NoAutoMount";

    public static VolumeAutoMountState GetState()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(MountManagerKey);
            object? value = key?.GetValue(NoAutoMountValue);

            // NoAutoMount 不存在或为 0 表示启用；非零表示禁用。
            bool enabled = value == null || Convert.ToUInt64(value) == 0;
            return new VolumeAutoMountState(true, enabled, string.Empty);
        }
        catch (Exception ex)
        {
            return new VolumeAutoMountState(false, false, ex.Message);
        }
    }

    public static async Task<(bool Success, string Error)> SetEnabledAsync(bool enabled)
    {
        try
        {
            string mountvolPath = Path.Combine(Environment.SystemDirectory, "mountvol.exe");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = mountvolPath,
                    Arguments = enabled ? "/E" : "/N",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
                return (false, Properties.Resources.Error_Host_AutoMountProcessStartFailed);

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error)
                    ? new Win32Exception(process.ExitCode).Message
                    : error.Trim();
                return (false, detail);
            }

            VolumeAutoMountState actual = GetState();
            if (!actual.Success)
                return (false, actual.Error);
            if (actual.Enabled != enabled)
                return (false, Properties.Resources.Error_Host_AutoMountStateMismatch);

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
