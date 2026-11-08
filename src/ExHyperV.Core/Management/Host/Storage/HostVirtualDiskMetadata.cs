using System.ComponentModel;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>只读解析虚拟磁盘的父盘关系；无法确认时返回失败，不能推断为没有父盘。</summary>
internal static class HostVirtualDiskMetadata
{
    internal static Task<ApiResponse<string?>> ReadParentPathAsync(string imagePath) =>
        Task.Run(() => ReadParentPath(imagePath));

    internal static ApiResponse<string?> ReadParentPath(string imagePath)
    {
        try
        {
            using var reader = new VirtualDiskMetadataReader();
            return ApiResponse<string?>.Ok(reader.ReadParentPath(imagePath));
        }
        catch (Win32Exception error)
        {
            return ApiResponse<string?>.Fail(error.Message, error.NativeErrorCode, ApiErrorSource.Win32, error,
                error.NativeErrorCode switch
                {
                    2 or 3 => ApiErrorKind.NotFound,
                    5 => ApiErrorKind.AccessDenied,
                    32 or 33 => ApiErrorKind.Conflict,
                    50 => ApiErrorKind.Unsupported,
                    _ => ApiErrorKind.OperationFailed
                });
        }
        catch (Exception error)
        {
            return ApiResponse<string?>.Fail(error.Message, exception: error, kind: error switch
            {
                ArgumentException => ApiErrorKind.InvalidArgument,
                NotSupportedException => ApiErrorKind.Unsupported,
                _ => ApiErrorKind.OperationFailed
            });
        }
    }
}
