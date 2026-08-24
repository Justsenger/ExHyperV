using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public enum HostPlatform
{
    Unknown,
    Amd,
    Intel,
    Arm64
}

/// <summary>
/// 异步检测主机原生平台，不把 WMI 查询带入应用静态初始化。
/// 成功结果在进程内缓存；查询失败不缓存，后续调用仍可重试。
/// </summary>
public static class HostPlatformService
{
    private static readonly object SyncRoot = new();
    private static readonly SemaphoreSlim DetectionGate = new(1, 1);
    private static HostPlatform? _cachedPlatform;

    public static async Task<HostPlatform> GetNativeHostPlatformAsync()
    {
        lock (SyncRoot)
        {
            if (_cachedPlatform is HostPlatform cached)
                return cached;

            if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                _cachedPlatform = HostPlatform.Arm64;
                return HostPlatform.Arm64;
            }
        }

        await DetectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (SyncRoot)
            {
                if (_cachedPlatform is HostPlatform cached)
                    return cached;
            }

            return await DetectNativeHostPlatformAsync().ConfigureAwait(false);
        }
        finally
        {
            DetectionGate.Release();
        }
    }

    private static async Task<HostPlatform> DetectNativeHostPlatformAsync()
    {
        HostPlatform platform = HostPlatform.Unknown;

        var response = await WmiApi.QueryFirstAsync(
                "SELECT Manufacturer FROM Win32_Processor",
                obj => obj["Manufacturer"]?.ToString() ?? string.Empty,
                WmiScope.CimV2)
            .ConfigureAwait(false);

        if (!response.HasData || string.IsNullOrWhiteSpace(response.Data))
            return platform;

        platform = response.Data switch
        {
            string manufacturer when string.Equals(
                manufacturer, "AuthenticAMD", StringComparison.OrdinalIgnoreCase) => HostPlatform.Amd,
            string manufacturer when string.Equals(
                manufacturer, "GenuineIntel", StringComparison.OrdinalIgnoreCase) => HostPlatform.Intel,
            _ => HostPlatform.Unknown
        };

        lock (SyncRoot)
            _cachedPlatform = platform;

        return platform;
    }
}
