using System.Runtime.InteropServices;

namespace ExHyperV.Api;

// ══════════════════════════════════════════════════════════════════
//  DismApi — DISM 原生 API 封装
//  直接调用 dismapi.dll，不走 PowerShell
//  用于开启/关闭 Windows 可选功能（如 Hyper-V 组件）
// ══════════════════════════════════════════════════════════════════
public static class DismApi
{
    // ── 公开接口 ──────────────────────────────────────────────────

    /// <summary>
    /// 启用一个或多个 Windows 可选功能。
    /// featureNames 可以用分号分隔多个功能名。
    /// </summary>
    public static async Task<ApiResponse> EnableFeatureAsync(string featureNames, bool enableAll = true)
        => await Task.Run(() => EnableFeature(featureNames, enableAll));

    /// <summary>
    /// 禁用一个或多个 Windows 可选功能。
    /// featureNames 可以用分号分隔多个功能名。
    /// removePayload=false 保留组件文件，可以重新启用。
    /// </summary>
    public static async Task<ApiResponse> DisableFeatureAsync(string featureNames, bool removePayload = false)
        => await Task.Run(() => DisableFeature(featureNames, removePayload));

    // ── 内部实现 ──────────────────────────────────────────────────

    private static ApiResponse EnableFeature(string featureNames, bool enableAll)
    {
        uint session = 0;
        try
        {
            int hr = DismNativeMethods.DismInitialize(
                DismNativeMethods.DismLogLevel.DismLogErrorsWarningsInfo,
                null, null);
            if (hr != 0) return ApiResponse.Fail($"DismInitialize failed: 0x{hr:X8}", hr, ApiErrorSource.Win32);

            hr = DismNativeMethods.DismOpenSession(
                DismNativeMethods.DISM_ONLINE_IMAGE, null, null, out session);
            if (hr != 0) return ApiResponse.Fail($"DismOpenSession failed: 0x{hr:X8}", hr, ApiErrorSource.Win32);

            hr = DismNativeMethods.DismEnableFeature(
                session, featureNames,
                null, DismNativeMethods.DismPackageIdentifier.DismPackageNone,
                false, null, 0, enableAll,
                nint.Zero, nint.Zero, nint.Zero);

            return hr == 0 || hr == 3010
                ? ApiResponse.Ok()
                : ApiResponse.Fail($"DismEnableFeature failed: 0x{hr:X8}", hr, ApiErrorSource.Win32);
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.Win32, ex);
        }
        finally
        {
            if (session != 0) DismNativeMethods.DismCloseSession(session);
            DismNativeMethods.DismShutdown();
        }
    }

    private static ApiResponse DisableFeature(string featureNames, bool removePayload)
    {
        uint session = 0;
        try
        {
            int hr = DismNativeMethods.DismInitialize(
                DismNativeMethods.DismLogLevel.DismLogErrorsWarningsInfo,
                null, null);
            if (hr != 0) return ApiResponse.Fail($"DismInitialize failed: 0x{hr:X8}", hr, ApiErrorSource.Win32);

            hr = DismNativeMethods.DismOpenSession(
                DismNativeMethods.DISM_ONLINE_IMAGE, null, null, out session);
            if (hr != 0) return ApiResponse.Fail($"DismOpenSession failed: 0x{hr:X8}", hr, ApiErrorSource.Win32);

            hr = DismNativeMethods.DismDisableFeature(
                session, featureNames,
                null, removePayload,
                nint.Zero, nint.Zero, nint.Zero);

            return hr == 0 || hr == 3010
                ? ApiResponse.Ok()
                : ApiResponse.Fail($"DismDisableFeature failed: 0x{hr:X8}", hr, ApiErrorSource.Win32);
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.Win32, ex);
        }
        finally
        {
            if (session != 0) DismNativeMethods.DismCloseSession(session);
            DismNativeMethods.DismShutdown();
        }
    }
}

// ══════════════════════════════════════════════════════════════════
//  NativeMethods — dismapi.dll P/Invoke 声明
// ══════════════════════════════════════════════════════════════════
internal static class DismNativeMethods
{
    public const string DISM_ONLINE_IMAGE = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";

    public enum DismLogLevel { DismLogErrorsWarningsInfo = 2 }
    public enum DismPackageIdentifier { DismPackageNone = 0, DismPackageName = 1, DismPackagePath = 2 }

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismInitialize(
        DismLogLevel logLevel,
        [MarshalAs(UnmanagedType.LPWStr)] string? logFilePath,
        [MarshalAs(UnmanagedType.LPWStr)] string? scratchDirectory);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismOpenSession(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string? windowsDirectory,
        [MarshalAs(UnmanagedType.LPWStr)] string? systemDrive,
        out uint session);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismEnableFeature(
        uint session,
        [MarshalAs(UnmanagedType.LPWStr)] string featureName,
        [MarshalAs(UnmanagedType.LPWStr)] string? identifier,
        DismPackageIdentifier packageIdentifier,
        [MarshalAs(UnmanagedType.Bool)] bool limitAccess,
        string[]? sourcePaths,
        uint sourcePathCount,
        [MarshalAs(UnmanagedType.Bool)] bool enableAll,
        nint cancelEvent,
        nint progress,
        nint userData);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismDisableFeature(
        uint session,
        [MarshalAs(UnmanagedType.LPWStr)] string featureName,
        [MarshalAs(UnmanagedType.LPWStr)] string? packageName,
        [MarshalAs(UnmanagedType.Bool)] bool removePayload,
        nint cancelEvent,
        nint progress,
        nint userData);

    [DllImport("dismapi.dll")]
    public static extern int DismCloseSession(uint session);

    [DllImport("dismapi.dll")]
    public static extern int DismShutdown();
}