using System.Diagnostics;
using System.Management;

namespace ExHyperV.Tools;

internal sealed record WmiJobObservation(ushort State, ushort? ErrorCode, string? Description, int PercentComplete,
    ushort[]? OperationalStatus = null);

// 等待结束和原生作业结束是两回事。这里只接受可确认的终态，不请求 Kill/Terminate。
internal static class WmiJobWaiter
{
    internal static WmiJobObservation Read(ManagementBaseObject job)
    {
        var state = job["JobState"] ?? throw new InvalidOperationException("WMI 作业缺少 JobState。");
        var error = job["ErrorCode"];
        var operationalStatus = job.Properties.Cast<PropertyData>()
            .FirstOrDefault(p => p.Name.Equals("OperationalStatus", StringComparison.OrdinalIgnoreCase))?.Value as ushort[];
        return new(Convert.ToUInt16(state), error is null ? null : Convert.ToUInt16(error),
            job["ErrorDescription"]?.ToString(), Convert.ToInt32(job["PercentComplete"] ?? 0), operationalStatus);
    }

    internal static ApiResponse? Evaluate(WmiJobObservation job, bool hyperV)
    {
        if (job.State is >= 2 and <= 6) return null;

        // 32768 是微软 Hyper-V V2 样例中的 CompletedWithWarnings；其它提供程序不套用该扩展。
        bool completed = job.State == 7 || (hyperV && job.State == 32768);
        // Hyper-V 的失败作业可停在 Exception；Completed + Error 是额外的结束证据。
        bool completedException = hyperV && job.State == 10
            && job.OperationalStatus?.Contains((ushort)17) == true
            && job.OperationalStatus.Contains((ushort)6);
        if (completed && job.ErrorCode == 0) return ApiResponse.Ok();
        if ((completed || completedException || job.State == 8) && job.ErrorCode is ushort error)
            return ApiResponse.Fail(string.IsNullOrWhiteSpace(job.Description)
                ? $"WMI 作业已结束但未成功 (state={job.State}, error={error})。" : job.Description,
                error, ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);

        // Killed 可能仍留有子进程；Exception/Service/未知值也不能推断为已停止。
        return Unknown(string.IsNullOrWhiteSpace(job.Description)
            ? $"无法确认 WMI 作业最终结果 (state={job.State})；请核对目标状态后再处理。"
            : job.Description + " 无法确认 WMI 作业最终结果，请核对目标状态后再处理。");
    }

    internal static async Task<ApiResponse> WaitAsync(Func<WmiJobObservation> read,
        CancellationToken cancellationToken, bool hyperV, IProgress<int>? progress = null,
        TimeSpan? timeout = null)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var snapshot = read();
                    var result = Evaluate(snapshot, hyperV);
                    Report(progress, result?.Success == true ? 100 : snapshot.PercentComplete);
                    if (result is not null) return result;
                    await Task.Delay(300, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex)
            {
                return Unknown(timeoutCts.IsCancellationRequested
                    ? "等待 WMI 作业超时，底层操作可能仍在运行；请核对目标状态后再处理。"
                    : "已停止等待 WMI 作业，底层操作可能仍在运行；请核对目标状态后再处理。", exception: ex);
            }
        }
        catch (Exception ex)
        {
            return FromException(ex, submitted: true);
        }
    }

    internal static ApiResponse Unknown(string message, int code = -1,
        ApiErrorSource source = ApiErrorSource.Wmi, Exception? exception = null) =>
        ApiResponse.Fail(message, code, source, exception, ApiErrorKind.OutcomeUnknown);

    internal static ApiResponse FromException(Exception ex, bool submitted)
    {
        var source = ex is ManagementException ? ApiErrorSource.Wmi :
            ex is UnauthorizedAccessException ? ApiErrorSource.Win32 : ApiErrorSource.None;
        int code = ex is ManagementException management ? (int)management.ErrorCode :
            ex is UnauthorizedAccessException ? 5 : -1;
        var kind = submitted ? ApiErrorKind.OutcomeUnknown :
            ex is OperationCanceledException ? ApiErrorKind.Cancelled :
            ex is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed;
        return ApiResponse.Fail(submitted
            ? ex.Message + " 请求可能已提交，最终结果尚未确认；请核对目标状态后再处理。"
            : ex.Message, code, source, ex, kind);
    }

    private static void Report(IProgress<int>? progress, int value)
    {
        try { progress?.Report(Math.Clamp(value, 0, 100)); }
        catch (Exception ex) { Debug.WriteLine($"WMI progress callback failed: {ex.Message}"); }
    }
}
