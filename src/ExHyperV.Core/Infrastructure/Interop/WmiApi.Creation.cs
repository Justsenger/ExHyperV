using System.Management;

namespace ExHyperV.Tools;

// 只供创建事务使用。路径来自本次调用的回执，不是前后清单差集。
internal sealed record WmiCreationResult(ApiResponse Response, string[] CreatedPaths, bool OutcomeUnknown);
internal sealed record WmiCreatedElement(string Path, ushort[] Effects);

internal sealed class WmiCreationTrace(string? expectedClass)
{
    internal Guid Id { get; } = Guid.NewGuid();
    internal DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    internal string? ExpectedClass { get; } = expectedClass;
    internal string? JobPath { get; set; }
    internal string[] DirectPaths { get; set; } = [];
    internal WmiCreationResult? Result { get; set; }
}

internal static partial class WmiApi
{
    internal static Task<WmiCreationResult> InvokeCreationAsync(
        string wql, string methodName, string? expectedResultClass,
        Action<ManagementBaseObject>? setParams = null,
        string? resultField = "ResultingResourceSettings",
        CancellationToken cancellationToken = default, WmiCreationTrace? trace = null)
    {
        var operation = WmiOperationCapture.Start(methodName, wql, WmiScope.HyperV, WmiContext.Local);
        return RunTrackedAsync();
        async Task<WmiCreationResult> RunTrackedAsync()
        {
            var result = await InvokeCoreAsync().ConfigureAwait(false);
            if (trace is not null) trace.Result = result;
            operation?.Complete(result.Response);
            return result;
        }
        Task<WmiCreationResult> InvokeCoreAsync() => Task.Run(async () =>
        {
            bool submitted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var target = collection.Cast<ManagementObject>().FirstOrDefault();
                if (target is null)
                    return new WmiCreationResult(ApiResponse.Fail($"WMI object not found: {wql}"), [], false);

                using var input = target.GetMethodParameters(methodName);
                setParams?.Invoke(input);
                cancellationToken.ThrowIfCancellationRequested();
                submitted = true;
                using var output = target.InvokeMethod(methodName, input, null);
                if (output is null)
                    return UnknownCreation($"{methodName} 未返回创建结果，不能确认资源归属。");

                var code = ReadReturnCode(output);
                object? raw = resultField is null ? null : output[resultField];
                string[] direct = raw is string[] paths ? paths : raw is string path ? [path] : [];
                if (trace is not null) trace.DirectPaths = direct;
                ApiResponse response;
                if (code == 4096)
                {
                    string? jobPath = output["Job"] as string;
                    if (trace is not null) trace.JobPath = jobPath;
                    if (operation is not null) operation.Job = jobPath;
                    if (string.IsNullOrWhiteSpace(jobPath))
                        return UnknownCreation($"{methodName} 已提交，但没有返回作业身份。");

                    var job = await WaitForCreationJobAsync(ms, jobPath, cancellationToken);
                    if (job.OutcomeUnknown) return job;
                    response = job.Response;
                    operation?.Complete(response);

                    // ResultingResourceSettings 表示新增对象。若异步输出为空，只接受本次作业
                    // ElementEffects=5 (Create) 的关联；Affected 本身不表示由本次作业创建。
                    if (expectedResultClass is not null && direct.Length == 0)
                        direct = QueryJobCreatedPaths(ms, jobPath, expectedResultClass);
                }
                else
                {
                    response = code == 0 ? ApiResponse.Ok() : ApiResponse.Fail(
                        $"Method '{methodName}' returned code {code}", code, ApiErrorSource.Wmi,
                        kind: ApiErrorKind.OperationFailed);
                    operation?.Complete(response);
                    // 不支持或参数无效属于拒绝执行。其它创建失败可能已经产生部分资源。
                    if (code is 1 or 4 && direct.Length == 0)
                        return new WmiCreationResult(response, [], false);
                    if (code != 0 && direct.Length == 0)
                        return UnknownCreation(response.Error, response.Code, response.ErrorSource);
                }

                operation?.Complete(response);
                return CompleteCreationReceipt(response, direct, expectedResultClass);
            }
            catch (Exception ex)
            {
                return new WmiCreationResult(WmiJobWaiter.FromException(ex, submitted), [], submitted);
            }
        });
    }

    internal static WmiCreationResult CompleteCreationReceipt(
        ApiResponse response, IEnumerable<string> paths, string? expectedResultClass)
    {
        // VHD 等文件创建没有 WMI 结果对象；只允许在确认成功完成后认领指定文件。
        if (expectedResultClass is null)
            return response.Success ? new(response, [], false) :
                UnknownCreation(response.Error, response.Code, response.ErrorSource);

        var result = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0 || result.Any(p => !IsCreationPathOfClass(p, expectedResultClass)))
            return UnknownCreation(response.Success
                ? "创建已完成，但未获得可确认归属的资源身份。"
                : response.Error + " 创建资源的归属尚未确认。", response.Code, response.ErrorSource);
        return new(response, result, false);
    }

    private static bool IsCreationPathOfClass(string path, string expectedClass)
    {
        try
        {
            var parsed = new ManagementPath(path);
            return parsed.IsInstance && string.Equals(parsed.ClassName, expectedClass, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (ManagementException) { return false; }
    }

    internal static string[] SelectCreatedJobPaths(IEnumerable<WmiCreatedElement> elements, string expectedClass)
        => elements.Where(e => e.Effects.Contains((ushort)5) && IsCreationPathOfClass(e.Path, expectedClass))
            .Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] QueryJobCreatedPaths(ManagementScope scope, string jobPath, string expectedClass)
    {
        var relative = new ManagementPath(jobPath).RelativePath;
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
            $"REFERENCES OF {{{relative}}} WHERE ResultClass=Msvm_AffectedJobElement Role=AffectingElement"));
        using var references = searcher.Get();
        var elements = new List<WmiCreatedElement>();
        foreach (ManagementBaseObject reference in references)
        {
            using (reference)
                elements.Add(new(reference["AffectedElement"]?.ToString() ?? "",
                    reference["ElementEffects"] as ushort[] ?? []));
        }
        return SelectCreatedJobPaths(elements, expectedClass);
    }

    private static async Task<WmiCreationResult> WaitForCreationJobAsync(
        ManagementScope scope, string jobPath, CancellationToken cancellationToken)
    {
        var response = await WaitForJobOnScopeAsync(scope, jobPath, cancellationToken, hyperV: true);
        return new(response, [], response.ErrorKind == ApiErrorKind.OutcomeUnknown);
    }

    internal static WmiCreationResult? EvaluateCreationJob(ushort state, ushort? error, string? description)
        => EvaluateCreationJob(new(state, error, description, 0));

    internal static WmiCreationResult? EvaluateCreationJob(WmiJobObservation observation)
    {
        var response = WmiJobWaiter.Evaluate(observation, hyperV: true);
        return response is null ? null : new(response, [], response.ErrorKind == ApiErrorKind.OutcomeUnknown);
    }

    // Only an internally captured receipt can be observed. This is not an arbitrary
    // public WMI path entry point and never invokes, restarts or cancels a native job.
    internal static Task<WmiCreationResult> ObserveCreationAsync(WmiCreationTrace trace) => Task.Run(() =>
    {
        if (trace.Result is { OutcomeUnknown: false } known) return known;
        if (string.IsNullOrWhiteSpace(trace.JobPath))
            return UnknownCreation("本次调用未留下可查询的作业引用；请按恢复材料核对资源，不能重放创建。");
        try
        {
            var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var job = new ManagementObject(scope, new ManagementPath(trace.JobPath), null);
            job.Get();
            var observed = EvaluateCreationJob(WmiJobWaiter.Read(job));
            if (observed is null) return UnknownCreation("本次创建作业仍在运行，未执行清理。");
            if (observed.OutcomeUnknown) return observed;
            string[] paths = trace.DirectPaths;
            if (paths.Length == 0 && trace.ExpectedClass is not null)
                paths = QueryJobCreatedPaths(scope, trace.JobPath, trace.ExpectedClass);
            var result = CompleteCreationReceipt(observed.Response, paths, trace.ExpectedClass);
            if (!result.OutcomeUnknown) trace.Result = result;
            return result;
        }
        catch (Exception ex) { return new(WmiJobWaiter.FromException(ex, submitted: true), [], true); }
    });

    private static WmiCreationResult UnknownCreation(string error, int code = -1, ApiErrorSource source = ApiErrorSource.Wmi)
        => new(WmiJobWaiter.Unknown(error, code, source), [], true);
}
