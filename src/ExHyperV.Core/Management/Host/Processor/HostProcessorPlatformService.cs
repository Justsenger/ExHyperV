using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>分别查询宿主架构和处理器厂商；两项可以独立读取。</summary>
public static class HostProcessorPlatformService
{
    private static readonly object SyncRoot = new();
    private static readonly SemaphoreSlim DetectionGate = new(1, 1);
    private static IReadOnlyList<string>? _cachedManufacturers;

    /// <summary>读取宿主操作系统的原生架构。</summary>
    /// <returns>Architecture 枚举值，例如 X64、Arm64。ARM64 Windows 中的 x64 模拟进程仍返回 Arm64。</returns>
    public static Architecture GetArchitecture() => RuntimeInformation.OSArchitecture;

    /// <summary>读取宿主处理器的厂商标识，例如 GenuineIntel、AuthenticAMD 或系统报告的其他厂商。</summary>
    /// <param name="cancellationToken">在等待查询锁、开始查询前及查询返回后检查取消；不强行中断已开始的 WMI 调用。</param>
    /// <returns>成功时返回只读厂商清单；多个处理器的相同标识只保留一项。没有处理器记录时返回成功空结果，读取失败或记录缺少厂商时返回失败。</returns>
    /// <remarks>厂商标识去除首尾空白，忽略大小写去重并排序。完整的非空结果在进程内缓存；失败和空结果在下次调用时重新查询。</remarks>
    /// <exception cref="OperationCanceledException">调用方已请求取消。</exception>
    public static async Task<ApiResponse<IReadOnlyList<string>>> GetManufacturersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (SyncRoot)
        {
            if (_cachedManufacturers is { } cached)
                return ApiResponse<IReadOnlyList<string>>.Ok(cached);
        }

        await DetectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (SyncRoot)
            {
                if (_cachedManufacturers is { } cached)
                    return ApiResponse<IReadOnlyList<string>>.Ok(cached);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var response = await WmiApi.QueryAsync(
                "SELECT Manufacturer FROM Win32_Processor",
                obj => obj["Manufacturer"]?.ToString() ?? string.Empty,
                WmiScope.CimV2).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.Success)
                return ApiResponse<IReadOnlyList<string>>.Fail(response.Error, response.Code,
                    response.ErrorSource, response.Exception, response.ErrorKind);
            if (response.Data is not { Count: > 0 })
                return ApiResponse<IReadOnlyList<string>>.Empty();
            if (response.Data.Any(string.IsNullOrWhiteSpace))
                return ApiResponse<IReadOnlyList<string>>.Fail("系统未返回完整的处理器厂商信息。",
                    source: ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);

            IReadOnlyList<string> manufacturers = Array.AsReadOnly(response.Data
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
            lock (SyncRoot) _cachedManufacturers = manufacturers;
            return ApiResponse<IReadOnlyList<string>>.Ok(manufacturers);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return ApiResponse<IReadOnlyList<string>>.Fail(error.Message, exception: error,
                source: ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);
        }
        finally
        {
            DetectionGate.Release();
        }
    }

    internal static async Task<HostProcessorPlatform> GetNativeHostPlatformAsync()
    {
        if (GetArchitecture() == Architecture.Arm64) return HostProcessorPlatform.Arm64;
        var response = await GetManufacturersAsync().ConfigureAwait(false);
        if (!response.HasData) return HostProcessorPlatform.Unknown;
        return response.Data.Count == 1 ? response.Data[0] switch
        {
            string manufacturer when manufacturer.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase)
                => HostProcessorPlatform.Amd,
            string manufacturer when manufacturer.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase)
                => HostProcessorPlatform.Intel,
            _ => HostProcessorPlatform.Unknown
        } : HostProcessorPlatform.Unknown;
    }
}
