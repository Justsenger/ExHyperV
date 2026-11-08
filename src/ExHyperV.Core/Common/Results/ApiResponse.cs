using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace ExHyperV.Tools;

// ══════════════════════════════════════════════════════════════════
//  错误来源枚举
// ══════════════════════════════════════════════════════════════════
/// <summary>操作响应中数值错误码所属的来源，需结合具体方法解释。</summary>
public enum ApiErrorSource
{
    /// <summary>未指定原生来源，通常为参数、业务或托管失败。</summary>
    None,
    /// <summary>WMI/CIM 查询或方法调用失败。</summary>
    Wmi,
    /// <summary>Windows 原生接口错误；具体方法可能使用 Win32、配置管理器或 DISM 码。</summary>
    Win32,
    /// <summary>Host Compute Service 接口失败。</summary>
    Hcs,
    /// <summary>COM/WinRT 调用失败。</summary>
    Com,
}

/// <summary>与底层错误码来源无关的失败类别；调用方可据此处理业务分支。</summary>
public enum ApiErrorKind
{
    /// <summary>操作成功，没有错误。</summary>
    None,
    /// <summary>失败尚未归入更具体的类别，不应按错误文案猜测。</summary>
    Unspecified,
    /// <summary>请求缺少必需信息，或参数组合不合法。</summary>
    InvalidArgument,
    /// <summary>名称或选择条件匹配多个对象，无法确定唯一目标。</summary>
    AmbiguousTarget,
    /// <summary>请求的对象不存在或已不可查询。</summary>
    NotFound,
    /// <summary>对象身份或状态已变化，与本次请求冲突。</summary>
    Conflict,
    /// <summary>对象当前状态不允许执行请求的操作。</summary>
    InvalidState,
    /// <summary>当前调用方缺少完成操作的权限。</summary>
    AccessDenied,
    /// <summary>当前宿主或目标对象不支持请求的能力。</summary>
    Unsupported,
    /// <summary>操作产生了尚未清理的资源或状态，需要先处理清理结果。</summary>
    CleanupRequired,
    /// <summary>执行操作失败；不表示此前步骤没有产生副作用。</summary>
    OperationFailed,
    /// <summary>操作已取消或在提交前被取消；仅停止等待且无法确认底层结果时使用 OutcomeUnknown。</summary>
    Cancelled,
    /// <summary>请求已经提交或可能已提交，但最终结果或资源状态尚未确认；底层可能仍在执行，不得据此直接重试或清理。</summary>
    OutcomeUnknown,
}

// ══════════════════════════════════════════════════════════════════
//  泛型结果类型 ApiResponse<T>
//  用于有数据返回的场景：查询、读取
// ══════════════════════════════════════════════════════════════════
/// <summary>表示带数据操作的结果，区分失败、成功空结果和成功数据。</summary>
/// <remarks>使用 HasData 判断是否有数据，IsEmpty 表示成功但没有结果。失败信息由 ErrorKind、ErrorSource、Code 和 Error 提供。</remarks>
/// <typeparam name="T">成功响应携带的数据类型。</typeparam>
public sealed class ApiResponse<T>
{
    private ApiResponse() { }
    /// <summary>操作响应是否成功；成功的空结果也为 true，数据存在性由 HasData 判断。</summary>
    public bool Success { get; private init; }
    /// <summary>查询是否以成功但没有匹配结果结束，与失败区分。</summary>
    public bool IsEmpty { get; private init; }
    /// <summary>响应数据；使用前先检查 HasData，失败/空结果时可能为默认值。</summary>
    public T? Data { get; private init; }
    /// <summary>面向人的失败说明，可能本地化，不宜按文本判断错误类别。</summary>
    public string Error { get; private init; } = string.Empty;
    /// <summary>原生或业务错误码；含义由 ErrorSource 和具体方法决定，未指定时通常为 -1。</summary>
    public int Code { get; private init; }
    /// <summary>错误码所属接口来源。</summary>
    public ApiErrorSource ErrorSource { get; private init; }

    /// <summary>业务失败类别；成功时为 None，尚未分类的失败为 Unspecified。</summary>
    public ApiErrorKind ErrorKind { get; private init; }

    /// <summary>失败时保留的原始异常；未提供时为 null。</summary>
    public Exception? Exception { get; private init; }

    /// <summary>创建成功响应并保存给定数据。</summary>
    /// <returns>数据非 null 时为成功数据响应；null 时与 Empty() 等价。</returns>
    /// <param name="data">成功响应要携带的数据；不复制数据，null 统一表示成功空结果。</param>
    public static ApiResponse<T> Ok(T? data) => data is null ? Empty() : new()
    {
        Success = true,
        IsEmpty = false,
        Data = data,
    };

    /// <summary>创建成功但没有查询匹配项的响应。</summary>
    /// <returns>Success=true、IsEmpty=true、Data=default 的响应；HasData=false。</returns>
    public static ApiResponse<T> Empty() => new()
    {
        Success = true,
        IsEmpty = true,
        Data = default,
    };

    /// <summary>创建失败响应并写入调试输出，不抛出传入异常。</summary>
    /// <returns>保存错误信息的失败响应。</returns>
    /// <param name="error">可供人阅读的错误说明。</param>
    /// <param name="code">接口或业务错误码；默认 -1。</param>
    /// <param name="source">错误码来源；默认未分类。</param>
    /// <param name="exception">可选原始异常，保存在响应中供诊断使用。</param>
    /// <param name="kind">失败类别；None 或未定义值按 Unspecified 处理。</param>
    public static ApiResponse<T> Fail(
        string error,
        int code = -1,
        ApiErrorSource source = ApiErrorSource.None,
        Exception? exception = null,
        ApiErrorKind kind = ApiErrorKind.Unspecified)
    {
        Debug.WriteLine($"[ApiResponse.Fail] source={source} code={code} error={error}");
        return new()
        {
            Success = false,
            IsEmpty = false,
            Error = error ?? string.Empty,
            Code = code,
            ErrorSource = source,
            ErrorKind = kind != ApiErrorKind.None && Enum.IsDefined(kind) ? kind : ApiErrorKind.Unspecified,
            Exception = exception,
        };
    }

    /// <summary>仅在成功、非空结果且 Data 非 null 时为 true。</summary>
    [MemberNotNullWhen(true, nameof(Data))]
    public bool HasData => Success && !IsEmpty && Data is not null;

    /// <summary>将响应按是否成功转换为布尔值；空响应对象为 false，成功空结果为 true。</summary>
    /// <param name="response">待判断响应，可为 null。</param>
    /// <returns>响应非 null 且 Success=true 时为 true；不检查 Data。</returns>
    public static implicit operator bool(ApiResponse<T>? response) => response is not null && response.Success;

    /// <summary>生成用于诊断的响应摘要，不是稳定的数据交换格式。</summary>
    /// <returns>包含成功/空结果或错误来源、码和说明的文本。</returns>
    public override string ToString() =>
        Success
            ? IsEmpty ? "ApiResponse: Empty" : $"ApiResponse: Ok({typeof(T).Name})"
            : $"ApiResponse: Fail [{ErrorSource}:{Code}] {Error}";
}

// ══════════════════════════════════════════════════════════════════
//  非泛型结果类型 ApiResponse
//  用于无数据返回的场景：操作类方法
// ══════════════════════════════════════════════════════════════════
/// <summary>表示不携带数据的操作结果，提供成功标志及失败信息。</summary>
/// <remarks>使用 Success 判断操作结果；失败时读取 ErrorKind 和 Error。Steps 提供入口记录的执行步骤，RequiresRestart 与 RestartReasons 提供本次操作报告的宿主重启要求。</remarks>
public sealed class ApiResponse
{
    private ApiResponse() { }
    /// <summary>接口已记录的修改步骤，按执行顺序排列；空列表表示该入口不提供步骤记录，不表示没有副作用。</summary>
    /// <remarks>步骤标识及范围见具体方法；未开始的步骤可能不在列表中。此列表不代替重新查询实际资源。</remarks>
    public IReadOnlyList<OperationStepResult> Steps { get; private init; } = Array.Empty<OperationStepResult>();

    internal ApiResponse WithSteps(IEnumerable<OperationStepResult> steps)
    {
        var snapshot = steps.ToArray();
        var reasons = RestartReasons.Concat(snapshot.SelectMany(step => step.RestartReasons)).Distinct().ToArray();
        return new()
        {
            Success = Success, Error = Error, Code = Code, ErrorSource = ErrorSource,
            ErrorKind = ErrorKind, Exception = Exception, Steps = Array.AsReadOnly(snapshot),
            RequiresRestart = RequiresRestart || snapshot.Any(step => step.RequiresRestart) || reasons.Length != 0,
            RestartReasons = Array.AsReadOnly(reasons)
        };
    }
    /// <summary>本次操作已确认需要重启宿主；失败响应也可能保留此前成功步骤的重启要求。</summary>
    /// <remarks>仅提供重启状态的接口设置此标志；false 不代表对整个系统不存在待重启事项作出保证。本对象不会触发重启。</remarks>
    public bool RequiresRestart { get; private init; }

    /// <summary>本次操作及已记录步骤明确报告的重启原因；按首次出现顺序保存，只读且去重。</summary>
    /// <remarks>只有重启布尔标记的旧入口可能没有原因。本列表不会推断系统其他操作的重启需求。</remarks>
    public IReadOnlyList<RestartReason> RestartReasons { get; private init; } = Array.Empty<RestartReason>();

    internal ApiResponse WithRestartRequired(bool required) => new()
    {
        Success = Success, Error = Error, Code = Code, ErrorSource = ErrorSource,
        ErrorKind = ErrorKind, Exception = Exception, Steps = Steps,
        RequiresRestart = RequiresRestart || required, RestartReasons = RestartReasons
    };

    internal ApiResponse WithRestartReason(RestartReason reason) => WithRestartReasons([reason]);

    internal ApiResponse WithRestartReasons(IEnumerable<RestartReason> reasons)
    {
        var snapshot = RestartReasons.Concat(reasons).Distinct().ToArray();
        if (snapshot.Any(reason => !Enum.IsDefined(reason)))
            throw new ArgumentOutOfRangeException(nameof(reasons), "包含未定义的重启原因。");
        return new()
        {
            Success = Success, Error = Error, Code = Code, ErrorSource = ErrorSource,
            ErrorKind = ErrorKind, Exception = Exception, Steps = Steps,
            RequiresRestart = RequiresRestart || snapshot.Length != 0,
            RestartReasons = Array.AsReadOnly(snapshot)
        };
    }
    /// <summary>当前操作是否报告成功；具体成功条件见对应方法。</summary>
    public bool Success { get; private init; }
    /// <summary>面向人的失败说明，可能本地化，不宜按文本判断错误类别。</summary>
    public string Error { get; private init; } = string.Empty;
    /// <summary>原生或业务错误码；含义由 ErrorSource 和具体方法决定，未指定时通常为 -1。</summary>
    public int Code { get; private init; }
    /// <summary>错误码所属接口来源。</summary>
    public ApiErrorSource ErrorSource { get; private init; }

    /// <summary>业务失败类别；成功时为 None，尚未分类的失败为 Unspecified。</summary>
    public ApiErrorKind ErrorKind { get; private init; }

    /// <summary>失败时保留的原始异常；未提供时为 null。</summary>
    public Exception? Exception { get; private init; }

    /// <summary>创建不携带数据的成功响应。</summary>
    /// <returns>新的成功响应。</returns>
    public static ApiResponse Ok() => new() { Success = true };

    /// <summary>创建失败响应并写入调试输出，不抛出传入异常。</summary>
    /// <returns>保存错误信息的失败响应。</returns>
    /// <param name="error">可供人阅读的错误说明。</param>
    /// <param name="code">接口或业务错误码；默认 -1。</param>
    /// <param name="source">错误码来源；默认未分类。</param>
    /// <param name="exception">可选原始异常，保存在响应中供诊断使用。</param>
    /// <param name="kind">失败类别；None 或未定义值按 Unspecified 处理。</param>
    public static ApiResponse Fail(
        string error,
        int code = -1,
        ApiErrorSource source = ApiErrorSource.None,
        Exception? exception = null,
        ApiErrorKind kind = ApiErrorKind.Unspecified)
    {
        Debug.WriteLine($"[ApiResponse.Fail] source={source} code={code} error={error}");
        return new()
        {
            Success = false,
            Error = error ?? string.Empty,
            Code = code,
            ErrorSource = source,
            ErrorKind = kind != ApiErrorKind.None && Enum.IsDefined(kind) ? kind : ApiErrorKind.Unspecified,
            Exception = exception,
        };
    }

    /// <summary>将响应按是否成功转换为布尔值；响应对象为 null 时为 false。</summary>
    /// <param name="response">待判断响应，可为 null。</param>
    /// <returns>响应非 null 且 Success=true 时为 true。</returns>
    public static implicit operator bool(ApiResponse? response) => response is not null && response.Success;

    /// <summary>生成用于诊断的响应摘要，不是稳定的数据交换格式。</summary>
    /// <returns>包含成功状态或错误来源、码和说明的文本。</returns>
    public override string ToString() =>
        Success
            ? "ApiResponse: Ok"
            : $"ApiResponse: Fail [{ErrorSource}:{Code}] {Error}";
}
