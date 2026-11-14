using ExHyperV.Tools;
using System.ComponentModel;

namespace ExHyperV.Services;

/// <summary>管理 Windows Mount Manager 的宿主全局卷自动挂载策略。</summary>
public static class HostVolumeAutoMountService
{
    private const string ResourceName = "host:volume-auto-mount";

    /// <summary>读取宿主是否自动挂载新卷。</summary>
    /// <returns>true 已启用，false 已禁用；读取失败返回 Win32 错误。</returns>
    public static ApiResponse<bool> GetState()
    {
        try
        {
            return ApiResponse<bool>.Ok(VolumeAutoMountApi.GetEnabled());
        }
        catch (Win32Exception ex)
        {
            return ApiResponse<bool>.Fail(ex.Message, ex.NativeErrorCode, ApiErrorSource.Win32, ex, ErrorKind(ex));
        }
        catch (Exception ex)
        {
            return ApiResponse<bool>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
        }
    }

    /// <summary>直接设置 Mount Manager 的宿主全局自动挂载策略，并重新查询确认。</summary>
    /// <remarks>这是宿主全局的持久设置，退出程序后仍生效。Core 的写入和复核按宿主排队。</remarks>
    /// <returns>成功表示回读值与请求一致；回读失败为 OutcomeUnknown，值不一致为 Conflict。原生失败保留 Win32 错误码。</returns>
    /// <param name="enabled">true 启用新卷自动挂载，false 禁用。</param>
    public static async Task<ApiResponse> SetEnabledAsync(bool enabled)
    {
        try
        {
            return await HostResourceLock.RunAsync(ResourceName, () =>
            {
                VolumeAutoMountApi.SetEnabled(enabled);
                var actual = GetState();
                if (!actual.HasData)
                    return Task.FromResult(ApiResponse.Fail(actual.Error, actual.Code, actual.ErrorSource,
                        actual.Exception, ApiErrorKind.OutcomeUnknown));
                if (actual.Data != enabled)
                    return Task.FromResult(ApiResponse.Fail(Properties.Resources.Error_Host_AutoMountStateMismatch,
                        kind: ApiErrorKind.Conflict));
                return Task.FromResult(ApiResponse.Ok());
            }).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return ApiResponse.Fail(ex.Message, ex.NativeErrorCode, ApiErrorSource.Win32, ex, ErrorKind(ex));
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, exception: ex, kind: ex is AbandonedMutexException
                ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed);
        }
    }

    private static ApiErrorKind ErrorKind(Win32Exception error) => error.NativeErrorCode switch
    {
        5 or 1314 => ApiErrorKind.AccessDenied,
        1 or 50 => ApiErrorKind.Unsupported,
        2 or 3 => ApiErrorKind.NotFound,
        _ => ApiErrorKind.OperationFailed
    };
}
