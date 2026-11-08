using System.Text.RegularExpressions;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class HyperVSwitchService
{
    internal const string ConfigurationResource = "host:network-configuration";

    internal static async Task RunConfigurationAsync(Func<Task> action)
    {
        try
        {
            await HostResourceLock.RunAsync(ConfigurationResource, async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (CoreOperationException) { throw; }
        catch (AbandonedMutexException error)
        {
            throw new CoreOperationException(ApiResponse.Fail("前一次网络配置操作异常结束，请核对交换机与 ICS 实际状态后再修改。",
                exception: error, kind: ApiErrorKind.Conflict));
        }
        catch (Exception error) { throw ConfigurationFailure(error); }
    }

    private static async Task<Guid> ResolveSwitchIdAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("交换机名称不能为空。", nameof(name));
        var response = await WmiApi.QueryAsync(
            $"SELECT Name FROM Msvm_VirtualEthernetSwitch WHERE ElementName = '{WmiApi.Escape(name)}'",
            p => Guid.Parse(p["Name"].ToString()!), WmiScope.HyperV);
        RequireSuccess(response);
        return SelectSwitchId(response.Data ?? []);
    }

    internal static Guid SelectSwitchId(IReadOnlyList<Guid> matches) => matches.Count == 1 ? matches[0]
        : throw new CoreOperationException(ApiResponse.Fail(matches.Count == 0 ? "没有找到指定交换机。" : "存在同名交换机，请使用 GUID 指定目标。",
            kind: matches.Count == 0 ? ApiErrorKind.NotFound : ApiErrorKind.AmbiguousTarget));

    internal static Guid SwitchIdFromPath(string path)
    {
        var match = Regex.Match(path, "(?:^|[.,])Name=\"(?<id>\\{?[0-9A-Fa-f-]{36}\\}?)\"", RegexOptions.IgnoreCase);
        return match.Success ? Guid.Parse(match.Groups["id"].Value)
            : throw new InvalidOperationException("新交换机路径没有提供可识别的 GUID。");
    }

    private static IReadOnlyList<OperationStepResult> CreatedSwitchSteps(string? path)
    {
        if (path == null) return [];
        // 只公开稳定 GUID，不把内部 WMI 对象路径带到公开契约。
        Guid id;
        try { id = SwitchIdFromPath(path); }
        catch { return []; }
        return [new("CreateSwitch", OperationStepState.Succeeded) { ResourceId = id.ToString("D") }];
    }

    internal static bool IsOutcomeUnknown(Exception error) =>
        error is CoreOperationException { Result.ErrorKind: ApiErrorKind.OutcomeUnknown };

    internal static async Task FailConfigurationAsync(Exception failure, Func<Task>? restore,
        IReadOnlyList<OperationStepResult>? steps = null)
    {
        bool restored = false;
        if (restore != null && !IsOutcomeUnknown(failure))
        {
            try { await restore().ConfigureAwait(false); restored = true; }
            catch (Exception recovery) { throw ConfigurationFailure(failure, recovery, steps); }
        }
        throw ConfigurationFailure(failure, steps: steps, restored: restored);
    }

    internal static CoreOperationException ConfigurationFailure(Exception error, Exception? recovery = null,
        IReadOnlyList<OperationStepResult>? steps = null, bool restored = false)
    {
        var failure = ApiFailure.From(error);
        var kind = failure.Kind;
        var recorded = new List<OperationStepResult>(steps ?? []);
        recorded.Add(new("ConfigureSwitch", kind == ApiErrorKind.OutcomeUnknown
            ? OperationStepState.OutcomeUnknown : OperationStepState.Failed, failure));
        if (restored) recorded.Add(new("RestoreSwitch", OperationStepState.Succeeded));
        if (recovery != null)
        {
            var cleanup = ApiFailure.From(recovery);
            recorded.Add(new("RestoreSwitch", cleanup.Kind == ApiErrorKind.OutcomeUnknown
                ? OperationStepState.OutcomeUnknown : OperationStepState.Failed, cleanup));
            kind = kind == ApiErrorKind.OutcomeUnknown || cleanup.Kind == ApiErrorKind.OutcomeUnknown
                ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired;
        }
        return new CoreOperationException(ApiResponse.Fail(recovery == null ? failure.Message
            : failure.Message + "\n恢复失败：" + recovery.Message, failure.Code, failure.Source,
            recovery == null ? error : new AggregateException(error, recovery), kind).WithSteps(recorded));
    }

    internal static void RequireSuccess(ApiResponse result)
    {
        if (!result.Success) throw new CoreOperationException(result);
    }

    private static void RequireSuccess<T>(ApiResponse<T> result)
    {
        if (!result.Success) throw new CoreOperationException(ApiResponse.Fail(result.Error, result.Code,
            result.ErrorSource, result.Exception, result.ErrorKind));
    }
}
