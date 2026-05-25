using System.Diagnostics;

namespace ExHyperV.Api;

// ══════════════════════════════════════════════════════════════════
//  错误来源枚举
// ══════════════════════════════════════════════════════════════════
public enum ApiErrorSource
{
    None,
    Wmi,
    Win32,
    Hcs,
    Com,
}

// ══════════════════════════════════════════════════════════════════
//  泛型结果类型 ApiResponse<T>
//  用于有数据返回的场景：查询、读取
// ══════════════════════════════════════════════════════════════════
public class ApiResponse<T>
{
    public bool Success { get; init; }
    public bool IsEmpty { get; init; }
    public T? Data { get; init; }
    public string Error { get; init; } = string.Empty;
    public int Code { get; init; }
    public ApiErrorSource ErrorSource { get; init; }

#if DEBUG
    public Exception? Exception { get; init; }
#endif

    public static ApiResponse<T> Ok(T data) => new()
    {
        Success = true,
        IsEmpty = false,
        Data = data,
    };

    public static ApiResponse<T> Empty() => new()
    {
        Success = true,
        IsEmpty = true,
        Data = default,
    };

    public static ApiResponse<T> Fail(
        string error,
        int code = -1,
        ApiErrorSource source = ApiErrorSource.None,
        Exception? exception = null)
    {
        Debug.WriteLine($"[ApiResponse.Fail] source={source} code={code} error={error}");
        return new()
        {
            Success = false,
            IsEmpty = false,
            Error = error,
            Code = code,
            ErrorSource = source,
#if DEBUG
            Exception = exception,
#endif
        };
    }

    public bool HasData => Success && !IsEmpty && Data is not null;

    public static implicit operator bool(ApiResponse<T> response) => response.Success;

    public override string ToString() =>
        Success
            ? IsEmpty ? "ApiResponse: Empty" : $"ApiResponse: Ok({typeof(T).Name})"
            : $"ApiResponse: Fail [{ErrorSource}:{Code}] {Error}";
}

// ══════════════════════════════════════════════════════════════════
//  非泛型结果类型 ApiResponse
//  用于无数据返回的场景：操作类方法
// ══════════════════════════════════════════════════════════════════
public sealed class ApiResponse
{
    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public int Code { get; init; }
    public ApiErrorSource ErrorSource { get; init; }

#if DEBUG
    public Exception? Exception { get; init; }
#endif

    public static ApiResponse Ok() => new() { Success = true };

    public static ApiResponse Fail(
        string error,
        int code = -1,
        ApiErrorSource source = ApiErrorSource.None,
        Exception? exception = null)
    {
        Debug.WriteLine($"[ApiResponse.Fail] source={source} code={code} error={error}");
        return new()
        {
            Success = false,
            Error = error,
            Code = code,
            ErrorSource = source,
#if DEBUG
            Exception = exception,
#endif
        };
    }

    public static implicit operator bool(ApiResponse response) => response.Success;

    public override string ToString() =>
        Success
            ? "ApiResponse: Ok"
            : $"ApiResponse: Fail [{ErrorSource}:{Code}] {Error}";
}