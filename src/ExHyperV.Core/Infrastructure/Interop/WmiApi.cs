using System.Diagnostics;
using System.Management;
using System.Runtime.CompilerServices;

namespace ExHyperV.Tools;

// ══════════════════════════════════════════════════════════════════
//  WMI 命名空间常量
//  所有 scope 字符串集中在这里，服务层不允许硬编码路径
// ══════════════════════════════════════════════════════════════════
/// <summary>常用 Windows WMI 命名空间常量，不表示已连接或已获得权限。</summary>
internal static class WmiScope
{
    /// <summary>Hyper-V virtualization/v2 管理命名空间。</summary>
    public const string HyperV = @"root\virtualization\v2";
    /// <summary>通用 Windows CIMV2 系统信息命名空间。</summary>
    public const string CimV2 = @"root\cimv2";
    /// <summary>Windows Storage 存储管理命名空间。</summary>
    public const string Storage = @"Root\Microsoft\Windows\Storage";
    /// <summary>Windows 标准 CIM 管理命名空间。</summary>
    public const string StdCimV2 = @"root\StandardCimv2";
    /// <summary>底层 Windows WMI 命名空间。</summary>
    public const string Wmi = @"root\wmi";
    /// <summary>DeviceGuard 安全能力命名空间。</summary>
    public const string DeviceGuard = @"root\Microsoft\Windows\DeviceGuard";
    /// <summary>Host Guardian Service 客户端命名空间。</summary>
    public const string Hgs = @"root\microsoft\windows\hgs";
}

// ══════════════════════════════════════════════════════════════════
//  WmiContext — 连接上下文
//  本机用 WmiContext.Local（静态单例，零分配）
//  远程用 WmiContext.Remote(host, credential)
// ══════════════════════════════════════════════════════════════════
/// <summary>指定一次 WMI 连接使用的主机和凭据。</summary>
/// <remarks>大多数高级服务固定访问本机。远程访问依赖目标权限及 WMI/DCOM 配置；凭据由此对象保存，不应写入日志。</remarks>
internal sealed class WmiContext
{
    /// <summary>使用当前进程身份访问本机的共享上下文。</summary>
    public static readonly WmiContext Local = new();

    /// <summary>目标主机名称；本机上下文使用点号。</summary>
    public string Host { get; }
    /// <summary>远程连接用户名；本机上下文为 null。</summary>
    public string? Username { get; }
    /// <summary>远程连接密码；本机上下文为 null。此值是凭据，不应展示或记录。</summary>
    public string? Password { get; }
    /// <summary>是否标记为本机连接上下文。</summary>
    public bool IsLocal => Host == ".";

    private WmiContext()
    {
        Host = ".";
    }

    /// <summary>创建指定远程主机与显式凭据的 WMI 上下文，尚不连接目标。</summary>
    /// <returns>不可变远程连接上下文。</returns>
    /// <param name="host">目标主机名称或地址。</param>
    /// <param name="username">具有目标 WMI 权限的账户。</param>
    /// <param name="password">该账户密码。</param>
    public static WmiContext Remote(string host, string username, string password) =>
        new(host, username, password);

    private WmiContext(string host, string username, string password)
    {
        Host = host;
        Username = username;
        Password = password;
    }
}

// ══════════════════════════════════════════════════════════════════
//  连接缓存 — 内部使用
//  按连接上下文身份和 scope 缓存；不同凭据上下文不得复用连接。
// ══════════════════════════════════════════════════════════════════
internal static class WmiConnectionCache
{
    private static ConditionalWeakTable<WmiContext, Dictionary<string, ConnectionEntry>> _contexts = new();
    private static readonly object _mgmtLock = new();

    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    internal sealed class ConnectionEntry
    {
        internal readonly object SyncRoot = new();
        internal ManagementScope? Scope;
        internal DateTime LastChecked;
    }

    internal static ConnectionEntry GetScopeEntry(string scope, WmiContext ctx)
    {
        lock (_mgmtLock)
        {
            // The weak context key neither exposes credentials nor keeps temporary
            // remote contexts and their scopes alive after the caller drops them.
            var scopes = _contexts.GetValue(ctx, _ => new());
            if (!scopes.TryGetValue(scope, out var entry)) scopes[scope] = entry = new();
            return entry;
        }
    }

    public static ManagementScope GetManagementScope(string scope, WmiContext ctx)
    {
        // 全局锁仅用于查找条目；连接和健康检查只协调同一条目。
        // Clear 换表后，本次操作仍只更新已取得的旧条目，不会写回新表。
        var entry = GetScopeEntry(scope, ctx);
        lock (entry.SyncRoot)
        {
            if (entry.Scope is ManagementScope cached && cached.IsConnected)
            {
                if (DateTime.Now - entry.LastChecked < HealthCheckInterval)
                {
                    return cached;
                }

                try
                {
                    using var searcher = new ManagementObjectSearcher(cached,
                        new ObjectQuery("SELECT Name FROM __Namespace WHERE Name='_health_check_'"));
                    using var collection = searcher.Get();
                    entry.LastChecked = DateTime.Now;
                    return cached;
                }
                catch
                {
                    entry.Scope = null;
                    entry.LastChecked = DateTime.MinValue;
                }
            }

            string path = ctx.IsLocal
                ? $@"\\.\{scope}"
                : $@"\\{ctx.Host}\{scope}";

            var options = new ConnectionOptions();
            if (!ctx.IsLocal)
            {
                options.Username = ctx.Username;
                options.Password = ctx.Password;
            }

            var ms = new ManagementScope(path, options);
            ms.Connect();
            entry.Scope = ms;
            entry.LastChecked = DateTime.Now;
            return ms;
        }
    }

    public static void Clear()
    {
        lock (_mgmtLock)
        {
            // Existing operations keep their obtained scopes; future calls receive fresh entries.
            _contexts = new();
        }
    }
}

// ══════════════════════════════════════════════════════════════════
//  WmiApi — 静态类
//  所有 WMI 调用的唯一入口
//  服务层不允许直接使用 ManagementObjectSearcher
// ══════════════════════════════════════════════════════════════════
/// <summary>提供底层 WQL 查询、对象修改和 WMI 方法调用；默认访问本机 Hyper-V。</summary>
/// <remarks>必须具备对应命名空间和操作权限。除明确返回对象所有权的入口外，映射/配置回调收到的 ManagementObject 仅在回调期间借用。异步作业取消通常只停止等待，不撤销已提交的 WMI 操作。</remarks>
internal static partial class WmiApi
{
    // ── A. 查询多行 ───────────────────────────────────────────────

    /// <summary>执行 WQL 并逐行映射到托管数据；CIM 入口同样使用 WMI 对象。</summary>
    /// <remarks>映射回调异常会被记录并跳过对应行，所以成功响应也可能是不完整结果。每行对象映射后释放，不支持调用方在返回后再使用。</remarks>
    /// <returns>查询响应；没有有效映射结果时为成功空列表。</returns>
    /// <param name="wql">要执行的 WQL 查询；动态字符串值应先用 Escape 转义。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">与原生 WMI 对象生命周期无关的映射结果类型。</typeparam>
    /// <example><code>using System;
    /// using System.Collections.Generic;
    /// using System.Threading.Tasks;
    /// using ExHyperV.Tools;
    ///
    /// public static class WmiReadExample
    /// {
    ///     public static async Task&lt;List&lt;string&gt;&gt; ReadVmIdsAsync()
    ///     {
    ///         var result = await WmiApi.QueryAsync(
    ///             "SELECT Name FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'",
    ///             row =&gt; row["Name"]?.ToString() ?? string.Empty);
    ///         if (!result.Success)
    ///             throw new InvalidOperationException(result.Error);
    ///         return result.Data ?? new List&lt;string&gt;();
    ///     }
    /// }</code></example>
    public static Task<ApiResponse<List<T>>> QueryAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        var substituted = QuerySource.Value?.Invoke(wql, scope);
        if (substituted is not null)
        {
            var failure = substituted.Failure;
            return Task.FromResult(failure is null ? ApiResponse<List<T>>.Ok([])
                : ApiResponse<List<T>>.Fail(failure.Message, failure.Code, failure.Source, kind: failure.Kind));
        }
        ctx ??= WmiContext.Local;

        return Task.Run(() =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
                var list = new List<T>();

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();

                foreach (ManagementBaseObject baseObj in collection)
                {
                    if (baseObj is not ManagementObject obj) continue;
                    using (obj)
                    {
                        list.Add(mapper(obj));
                    }
                }

                return ApiResponse<List<T>>.Ok(list);
            }
            catch (ManagementException ex)
            {
                return ApiResponse<List<T>>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiResponse<List<T>>.Fail(
                    ex.Message, 5, ApiErrorSource.Win32, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<T>>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>执行 WQL 并逐行映射到托管数据；CIM 入口同样使用 WMI 对象。</summary>
    /// <remarks>映射回调异常会被记录并跳过对应行，所以成功响应也可能是不完整结果。每行对象映射后释放，不支持调用方在返回后再使用。</remarks>
    /// <returns>查询响应；没有有效映射结果时为成功空列表。</returns>
    /// <param name="wql">要执行的 WQL 查询；动态字符串值应先用 Escape 转义。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">与原生 WMI 对象生命周期无关的映射结果类型。</typeparam>
    public static Task<ApiResponse<List<T>>> QueryCimAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.Storage,
        WmiContext? ctx = null)
        => QueryAsync(wql, mapper, scope, ctx);

    // ── B. 查询单行 ───────────────────────────────────────────────

    /// <summary>执行查询并返回首个匹配结果。</summary>
    /// <remarks>查询及映射沿用 QueryAsync；映射失败返回失败响应，无匹配记录时返回 Empty。</remarks>
    /// <returns>有结果时返回数据；没有结果时返回 Empty，Success 仍为 true。</returns>
    /// <param name="wql">要执行的 WQL 查询；动态字符串值应先用 Escape 转义。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">独立于原生对象生命周期的映射结果类型。</typeparam>
    public static async Task<ApiResponse<T>> QueryFirstAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        var response = await QueryAsync(wql, mapper, scope, ctx);

        if (!response.Success)
            return ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

        if (response.Data == null || response.Data.Count == 0)
            return ApiResponse<T>.Empty();

        return ApiResponse<T>.Ok(response.Data[0]);
    }

    /// <summary>执行查询并返回首个匹配结果。</summary>
    /// <remarks>查询及映射沿用 QueryAsync；映射失败返回失败响应，无匹配记录时返回 Empty。</remarks>
    /// <returns>有结果时返回数据；没有结果时返回 Empty，Success 仍为 true。</returns>
    /// <param name="wql">要执行的 WQL 查询；动态字符串值应先用 Escape 转义。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">独立于原生对象生命周期的映射结果类型。</typeparam>
    public static Task<ApiResponse<T>> QueryFirstCimAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.Storage,
        WmiContext? ctx = null)
        => QueryFirstAsync(wql, mapper, scope, ctx);

    // ── C. 调用方法 ───────────────────────────────────────────────

    /// <summary>定位实例并调用 WMI 方法，遇到异步作业时等待其完成。</summary>
    /// <remarks>提交前取消返回 Cancelled；提交后停止等待或无法确认最终结果返回 OutcomeUnknown，不撤销原生操作，调用方不得直接重试或清理。</remarks>
    /// <returns>操作响应；无匹配实例、原生错误、作业取消等待或超时返回失败。</returns>
    /// <param name="wql">用于定位目标实例的 WQL 查询，使用首个匹配实例。</param>
    /// <param name="methodName">目标 WMI 方法名称。</param>
    /// <param name="setParams">同步填写借用的入参对象；null 表示无需填写，不得保存该对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <param name="cancellationToken">取消异步作业等待的令牌；底层作业和同步原生调用继续执行。</param>
    /// <param name="validateTarget">可选的提交前身份核对；失败时不调用目标方法，不得保存借用的对象。</param>
    public static Task<ApiResponse> InvokeAsync(
        string wql,
        string methodName,
        Action<ManagementBaseObject>? setParams = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        CancellationToken cancellationToken = default,
        Func<ManagementObject, ApiResponse>? validateTarget = null)
    {
        ctx ??= WmiContext.Local;
        var operation = WmiOperationCapture.Start(methodName, wql, scope, ctx);
        return RunTrackedAsync();
        async Task<ApiResponse> RunTrackedAsync()
        {
            var result = await InvokeCoreAsync().ConfigureAwait(false);
            operation?.Complete(result);
            return result;
        }
        Task<ApiResponse> InvokeCoreAsync() => Task.Run(async () =>
        {
            bool invocationStarted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var target = collection.Cast<ManagementObject>().FirstOrDefault();

                if (target is null)
                    return ApiResponse.Fail($"WMI object not found: {wql}");

                if (validateTarget is not null)
                {
                    var validation = validateTarget(target);
                    if (!validation.Success) return validation;
                }
                invocationStarted = true;
                return await InvokeOnObjectAsync(target, methodName, setParams, scope, ctx, cancellationToken, operation: operation);
            }
            catch (Exception ex)
            {
                return WmiJobWaiter.FromException(ex, invocationStarted);
            }
        });
    }

    /// <summary>调用实例方法并提取指定字符串结果字段；异步作业按实现读取关联结果路径。</summary>
    /// <remarks>提交前取消返回 Cancelled；提交后停止等待或无法确认最终结果返回 OutcomeUnknown，不撤销原生操作，调用方不得直接重试或清理。</remarks>
    /// <returns>指定结果字段或作业关联路径的字符串数组响应，不是完整 outParams 对象。</returns>
    /// <param name="wql">用于定位目标实例的 WQL 查询，使用首个匹配实例。</param>
    /// <param name="methodName">目标 WMI 方法名称。</param>
    /// <param name="setParams">同步填写借用的入参对象；null 表示无需填写，不得保存该对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <param name="resultField">要提取的输出字段名称，默认 ResultingResourceSettings。</param>
    /// <param name="cancellationToken">取消异步作业等待的令牌；底层作业和同步原生调用继续执行。</param>
    public static Task<ApiResponse<string[]>> InvokeWithResultAsync(
        string wql,
        string methodName,
        Action<ManagementBaseObject>? setParams = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        string resultField = "ResultingResourceSettings",
        CancellationToken cancellationToken = default)
    {
        ctx ??= WmiContext.Local;

        return Task.Run(async () =>
        {
            bool submitted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var target = collection.Cast<ManagementObject>().FirstOrDefault();

                if (target is null)
                    return ApiResponse<string[]>.Fail($"WMI object not found: {wql}");

                using var inParams = target.GetMethodParameters(methodName);
                setParams?.Invoke(inParams);

                cancellationToken.ThrowIfCancellationRequested();
                submitted = true;
                using var outParams = target.InvokeMethod(methodName, inParams, null);
                if (outParams is null)
                    return CopyFailure<string[]>(WmiJobWaiter.Unknown($"方法 {methodName} 未返回结果，无法确认最终状态。"));

                int returnValue = ReadReturnCode(outParams);

                if (returnValue == 4096)
                {
                    string? jobPath = outParams["Job"] as string;
                    var jobResult = await WaitForJobAsync(jobPath, scope, ctx, cancellationToken);
                    if (!jobResult.Success)
                        return CopyFailure<string[]>(jobResult);

                    // 异步 Job 路径下 outParams 的结果字段保持 null，
                    // 结果须从 Job 的 Msvm_AffectedJobElement 关联取——与微软管理库同款取法。
                    return ApiResponse<string[]>.Ok(QueryJobAffectedPaths(ms, jobPath!, resultField));
                }
                if (returnValue != 0)
                {
                    return ApiResponse<string[]>.Fail(
                        $"Method '{methodName}' returned code {returnValue}",
                        returnValue, ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);
                }

                var raw = outParams[resultField];
                var resulting = raw is string[] arr ? arr :
                                raw is string s ? new[] { s } :
                                Array.Empty<string>();
                return ApiResponse<string[]>.Ok(resulting);
            }
            catch (Exception ex)
            {
                return CopyFailure<string[]>(WmiJobWaiter.FromException(ex, submitted));
            }
        });
    }

    /// <summary>对调用方持有的 WMI 实例执行方法，并可观察异步作业进度。</summary>
    /// <remarks>提交前取消返回 Cancelled；提交后停止等待或无法确认最终结果返回 OutcomeUnknown，不撤销原生操作，调用方不得直接重试或清理。</remarks>
    /// <returns>操作响应；作业等待取消或超时返回失败，取消前提交的操作不会自动撤销。</returns>
    /// <param name="target">调用方持有的目标对象，须在返回任务完成后释放。</param>
    /// <param name="methodName">目标 WMI 方法名称。</param>
    /// <param name="setParams">同步填写借用的入参对象；null 表示无需填写，不得保存该对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <param name="cancellationToken">取消异步作业等待的令牌；底层作业和同步原生调用继续执行。</param>
    /// <param name="progress">可选的作业完成百分比接收器；界面更新需调度到界面线程。</param>
    /// <param name="timeout">异步作业等待上限；null 默认 30 分钟，不限制同步原生调用所耗时间。</param>
    public static async Task<ApiResponse> InvokeOnObjectAsync(
        ManagementObject target,
        string methodName,
        Action<ManagementBaseObject>? setParams = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        CancellationToken cancellationToken = default,
        IProgress<int>? progress = null,
        TimeSpan? timeout = null,
        WmiOperationReceipt? operation = null)
    {
        ctx ??= WmiContext.Local;

        var receipt = operation ?? WmiOperationCapture.Start(methodName, target.Path.Path, scope, ctx);
        var response = await Task.Run(async () =>
        {
            bool submitted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var inParams = target.GetMethodParameters(methodName);
                setParams?.Invoke(inParams);

                cancellationToken.ThrowIfCancellationRequested();
                submitted = true;
                using var outParams = target.InvokeMethod(methodName, inParams, null);
                if (outParams is null)
                    return WmiJobWaiter.Unknown($"方法 {methodName} 未返回结果，无法确认最终状态。");

                int returnValue = ReadReturnCode(outParams);
                if (receipt != null && returnValue == 4096) receipt.Job = outParams["Job"] as string;

                return returnValue switch
                {
                    0 => ApiResponse.Ok(),
                    4096 => await WaitForJobAsync(
                                outParams["Job"] as string, scope, ctx, cancellationToken,
                                progress, timeout),
                    _ => ApiResponse.Fail(
                                $"Method '{methodName}' returned code {returnValue}",
                                returnValue, ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed)
                };
            }
            catch (Exception ex)
            {
                return WmiJobWaiter.FromException(ex, submitted);
            }
        });
        receipt?.Complete(response);
        return response;
    }

    /// <summary>调用 WMI 类方法并返回原生输出参数对象。</summary>
    /// <remarks>输出对象不是普通 DTO，不能遗漏释放。异步作业使用内部等待策略，没有公开取消入口。</remarks>
    /// <returns>成功时 Data 为调用方拥有的 ManagementBaseObject，使用后必须 Dispose；失败时返回错误响应。</returns>
    /// <param name="className">目标 WMI 类名。</param>
    /// <param name="methodName">目标 WMI 方法名称。</param>
    /// <param name="setParams">同步填写借用的入参对象；null 表示无需填写，不得保存该对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    public static Task<ApiResponse<ManagementBaseObject>> InvokeClassMethodAsync(
        string className,
        string methodName,
        Action<ManagementBaseObject>? setParams = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;

        return Task.Run(async () =>
        {
            bool submitted = false;
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
                using var cls = new ManagementClass(ms, new ManagementPath(className), null);
                using var inParams = cls.GetMethodParameters(methodName);
                setParams?.Invoke(inParams);

                submitted = true;
                var outParams = cls.InvokeMethod(methodName, inParams, null);
                if (outParams is null)
                    return CopyFailure<ManagementBaseObject>(WmiJobWaiter.Unknown(
                        $"方法 {methodName} 未返回结果，无法确认最终状态。"));

                // 成功时 outParams 的所有权转给调用方；失败/异常路径在此释放
                try
                {
                    int returnValue = ReadReturnCode(outParams);
                    if (returnValue == 4096)   // 异步 Job：等完成（与 InvokeOnObjectAsync 一致）
                    {
                        var jobResult = await WaitForJobAsync(outParams["Job"] as string, scope, ctx, default);
                        if (!jobResult.Success)
                        {
                            outParams.Dispose();
                            return CopyFailure<ManagementBaseObject>(jobResult);
                        }
                    }
                    else if (returnValue != 0)
                    {
                        outParams.Dispose();
                        return ApiResponse<ManagementBaseObject>.Fail(
                            $"Method '{methodName}' returned code {returnValue}",
                            returnValue, ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);
                    }

                    return ApiResponse<ManagementBaseObject>.Ok(outParams);
                }
                catch
                {
                    outParams.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                return CopyFailure<ManagementBaseObject>(WmiJobWaiter.FromException(ex, submitted));
            }
        });
    }

    /// <summary>创建未提交的 WMI 类实例，经回调配置后序列化为实例文本。</summary>
    /// <returns>配置后的 WMI 实例文本响应；本方法不把实例直接写入服务。</returns>
    /// <param name="className">要创建的 WMI 类名。</param>
    /// <param name="configure">同步配置借用实例并返回校验响应；返回失败时不序列化。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    public static Task<ApiResponse<string>> CreateInstanceTextAsync(
        string className,
        Func<ManagementObject, ApiResponse> configure,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;

        return Task.Run(() =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
                using var cls = new ManagementClass(ms, new ManagementPath(className), null);
                using var instance = cls.CreateInstance();
                if (instance is null)
                    return ApiResponse<string>.Fail(
                        string.Format(
                            Properties.Resources.Error_Wmi_CreateInstanceFailed,
                            className));

                var configureResult = configure(instance);
                if (!configureResult.Success)
                    return CopyFailure<string>(configureResult);

                return ApiResponse<string>.Ok(instance.GetText(TextFormat.CimDtd20));
            }
            catch (ManagementException ex)
            {
                return ApiResponse<string>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiResponse<string>.Fail(
                    ex.Message, 5, ApiErrorSource.Win32, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<string>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>对调用方持有的 CIM/WMI 实例执行方法。</summary>
    /// <returns>原生调用操作响应。</returns>
    /// <param name="instance">调用方拥有且在任务完成前保持有效的对象；本方法不释放它。</param>
    /// <param name="methodName">目标 WMI 方法名称。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="setParams">同步填写借用的入参对象；null 表示无需填写，不得保存该对象。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    public static Task<ApiResponse> InvokeCimMethodAsync(
        ManagementObject instance,
        string methodName,
        string scope,
        Action<ManagementBaseObject>? setParams = null,
        WmiContext? ctx = null)
        => InvokeOnObjectAsync(instance, methodName, setParams, scope, ctx);

    // ── D. 改属性提交 ─────────────────────────────────────────────

    /// <summary>读取首个设置对象，在回调中修改后，通过指定服务方法提交序列化结果。</summary>
    /// <remarks>提交前取消返回 Cancelled；提交后停止等待或无法确认最终结果返回 OutcomeUnknown，不撤销原生操作，调用方不得直接重试或清理。</remarks>
    /// <returns>读取、修改和提交的操作响应。</returns>
    /// <param name="wql">要执行的 WQL 查询；动态字符串值应先用 Escape 转义。</param>
    /// <param name="modifier">同步修改借用设置对象的回调；不得保留对象引用。</param>
    /// <param name="submitMethod">管理服务上用于提交修改的方法名称。</param>
    /// <param name="submitParamName">接收设置文本的入参名称。</param>
    /// <param name="wrapInArray">是否将设置文本包装为字符串数组。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <param name="serviceWql">用于定位提交服务实例的 WQL。</param>
    /// <param name="cancellationToken">取消异步作业等待的令牌；底层作业和同步原生调用继续执行。</param>
    public static async Task<ApiResponse> WithObjectAsync(
        string wql,
        Action<ManagementObject> modifier,
        string submitMethod = "ModifySystemSettings",
        string submitParamName = "SystemSettings",
        bool wrapInArray = false,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        string serviceWql = "SELECT * FROM Msvm_VirtualSystemManagementService",
        CancellationToken cancellationToken = default)
    {
        ctx ??= WmiContext.Local;

        return await Task.Run(async () =>
        {
            bool invocationStarted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var obj = collection.Cast<ManagementObject>().FirstOrDefault();

                if (obj is null)
                    return ApiResponse.Fail($"WMI object not found: {wql}", kind: ApiErrorKind.NotFound);

                modifier(obj);
                string xml = obj.GetText(TextFormat.CimDtd20);

                using var svcSearcher = new ManagementObjectSearcher(ms, new ObjectQuery(serviceWql));
                using var svcCollection = svcSearcher.Get();
                using var service = svcCollection.Cast<ManagementObject>().FirstOrDefault();

                if (service is null)
                    return ApiResponse.Fail($"Service not found: {serviceWql}");

                invocationStarted = true;
                return await InvokeOnObjectAsync(
                    service,
                    submitMethod,
                    p =>
                    {
                        p[submitParamName] = wrapInArray
                            ? (object)new string[] { xml }
                            : xml;
                    },
                    scope, ctx, cancellationToken);
            }
            catch (Exception ex)
            {
                return WmiJobWaiter.FromException(ex, invocationStarted);
            }
        });
    }

    // ── E. 关联查询 ───────────────────────────────────────────────

    /// <summary>枚举源对象的关联对象并映射为独立托管结果。</summary>
    /// <remarks>源对象连接及身份由调用方提供；不要依赖 scope/ctx 重定向已经持有的源对象。结果原生对象在映射后释放。</remarks>
    /// <returns>关联结果列表响应；行映射失败会跳过，可能成功返回部分数据。</returns>
    /// <param name="source">调用方持有的源对象，在查询任务完成前必须有效；结果对象仅在 mapper 中借用。</param>
    /// <param name="relatedClass">要查找的关联目标类名。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="associationClass">限制关联类；可空重载中 null 表示不限制。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">独立于原生对象生命周期的关联结果类型。</typeparam>
    public static Task<ApiResponse<List<T>>> QueryRelatedAsync<T>(
        ManagementObject source,
        string relatedClass,
        Func<ManagementObject, T> mapper,
        string? associationClass = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        return Task.Run(() =>
        {
            try
            {
                var list = new List<T>();

                using var related = source.GetRelated(
                    relatedClass,
                    associationClass,
                    null, null, null, null, false, null);

                foreach (var baseObj in related)
                {
                    if (baseObj is not ManagementObject obj) continue;
                    using (obj)
                    {
                        list.Add(mapper(obj));
                    }
                }

                return ApiResponse<List<T>>.Ok(list);
            }
            catch (ManagementException ex)
            {
                return ApiResponse<List<T>>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<T>>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>枚举源对象的关联对象并映射为独立托管结果。</summary>
    /// <remarks>源对象连接及身份由调用方提供；不要依赖 scope/ctx 重定向已经持有的源对象。结果原生对象在映射后释放。</remarks>
    /// <returns>关联结果列表响应；行映射失败会跳过，可能成功返回部分数据。</returns>
    /// <param name="source">调用方持有的源对象，在查询任务完成前必须有效；结果对象仅在 mapper 中借用。</param>
    /// <param name="relatedClass">要查找的关联目标类名。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="associationClass">限制关联类；可空重载中 null 表示不限制。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">独立于原生对象生命周期的关联结果类型。</typeparam>
    public static Task<ApiResponse<List<T>>> QueryRelatedCimAsync<T>(
        ManagementObject source,
        string relatedClass,
        Func<ManagementObject, T> mapper,
        string? associationClass = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
        => QueryRelatedAsync(source, relatedClass, mapper, associationClass, scope, ctx);

    /// <summary>枚举源对象的关联对象并映射为独立托管结果。</summary>
    /// <remarks>源对象连接及身份由调用方提供；不要依赖 scope/ctx 重定向已经持有的源对象。结果原生对象在映射后释放。</remarks>
    /// <returns>关联结果列表响应；行映射失败会跳过，可能成功返回部分数据。</returns>
    /// <param name="source">调用方持有的源对象，在查询任务完成前必须有效；结果对象仅在 mapper 中借用。</param>
    /// <param name="associationClass">限制关联类；可空重载中 null 表示不限制。</param>
    /// <param name="resultClass">关联结果类名。</param>
    /// <param name="sourceRole">源对象在关联中的角色属性名。</param>
    /// <param name="resultRole">结果对象在关联中的角色属性名。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">独立于原生对象生命周期的关联结果类型。</typeparam>
    public static Task<ApiResponse<List<T>>> QueryRelatedCimAsync<T>(
        ManagementObject source,
        string associationClass,
        string resultClass,
        string sourceRole,
        string resultRole,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        return Task.Run(() =>
        {
            try
            {
                var list = new List<T>();

                using var related = source.GetRelated(
                    resultClass,
                    associationClass,
                    null, null,
                    resultRole,    // 第5个参数传 resultRole
                    sourceRole,    // 第6个参数传 sourceRole
                    false, null);

                foreach (var baseObj in related)
                {
                    if (baseObj is not ManagementObject obj) continue;
                    using (obj)
                    {
                        list.Add(mapper(obj));
                    }
                }

                return ApiResponse<List<T>>.Ok(list);
            }
            catch (ManagementException ex)
            {
                return ApiResponse<List<T>>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<T>>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>在首个查询对象仍有效的期间等待异步回调完成，然后释放该对象。</summary>
    /// <returns>回调结果响应；未找到对象时返回 Empty。</returns>
    /// <param name="wql">要执行的 WQL 查询；动态字符串值应先用 Escape 转义。</param>
    /// <param name="callback">异步使用借用对象的回调；可在回调内 await，但不能返回后继续使用对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">回调产生的独立托管结果类型。</typeparam>
    public static Task<ApiResponse<T>> WithFirstAsync<T>(
        string wql,
        Func<ManagementObject, Task<T>> callback,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;
        return Task.Run(async () =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var obj = collection.Cast<ManagementObject>().FirstOrDefault();
                if (obj is null) return ApiResponse<T>.Empty();
                var result = await callback(obj);
                return ApiResponse<T>.Ok(result);
            }
            catch (ManagementException ex)
            {
                return ApiResponse<T>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<T>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    // ── F. 路径实例化 ─────────────────────────────────────────────

    /// <summary>按 WMI 对象路径读取实例并映射为托管结果。</summary>
    /// <returns>映射结果响应；访问或映射失败时返回失败。</returns>
    /// <param name="objectPath">WMI 对象路径，不是文件路径。</param>
    /// <param name="mapper">把当前借用的 ManagementObject 转为独立托管结果；不得保存或返回该原生对象。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    /// <typeparam name="T">独立于原生对象生命周期的结果类型。</typeparam>
    public static Task<ApiResponse<T>> GetByPathAsync<T>(
        string objectPath,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null) where T : class
    {
        ctx ??= WmiContext.Local;

        return Task.Run(() =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
                using var obj = new ManagementObject(ms, new ManagementPath(objectPath), null);
                obj.Get();
                return ApiResponse<T>.Ok(mapper(obj));
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
            {
                return ApiResponse<T>.Empty();
            }
            catch (ManagementException ex)
            {
                return ApiResponse<T>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<T>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    // ── 辅助：Hyper-V 管理服务快捷获取 ───────────────────────────

    /// <summary>
    /// 构造本机按显示名称定位虚拟机的 WQL 条件，并排除同名宿主机。
    /// Msvm_ComputerSystem 同时包含宿主机和虚拟机：二者的 ElementName 可以相同，
    /// 但宿主机 Name 是计算机名，而虚拟机 Name 是 GUID。
    /// </summary>
    internal static string VmComputerSystemNamePredicate(string vmName) =>
        $"ElementName = '{Escape(vmName)}' AND Name <> '{Escape(Environment.MachineName)}'";

    /// <summary>同步取得 Hyper-V 虚拟系统管理服务对象。</summary>
    /// <returns>调用方拥有的 ManagementObject，必须 Dispose；查询错误可抛异常。</returns>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    public static ManagementObject GetVirtualSystemManagementService(
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;
        var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
        using var searcher = new ManagementObjectSearcher(
            ms, new ObjectQuery("SELECT * FROM Msvm_VirtualSystemManagementService"));
        using var col = searcher.Get();
        return col.Cast<ManagementObject>().First();
    }

    /// <summary>Gets the local image service directly. The caller owns and disposes the returned object.</summary>
    internal static ManagementObject? GetImageManagementService()
    {
        var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
        using var collection = searcher.Get();
        return collection.Cast<ManagementObject>().FirstOrDefault();
    }

    /// <summary>按 VM 名称或 GUID 同步定位虚拟机 WMI 对象。</summary>
    /// <returns>调用方拥有且必须 Dispose 的对象；没有匹配项时为 null，查询错误可抛异常。</returns>
    /// <param name="vmName">虚拟机显示名称。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    public static ManagementObject? GetVmComputerSystem(
        string vmName,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;
        var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
        string safe = Escape(vmName);
        using var searcher = new ManagementObjectSearcher(
            ms, new ObjectQuery(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{safe}'"));
        using var col = searcher.Get();
        // 不依赖本机名，远程 WMI 上下文也能正确排除同名宿主机。
        ManagementObject? match = null;
        foreach (ManagementObject item in col)
        {
            if (!Guid.TryParse(item["Name"]?.ToString(), out _)) { item.Dispose(); continue; }
            if (match != null)
            {
                match.Dispose();
                item.Dispose();
                throw new InvalidOperationException("存在多个同名虚拟机，请使用 GUID 指定目标。");
            }
            match = item;
        }
        return match;
    }

    /// <summary>按 VM 名称或 GUID 同步定位虚拟机 WMI 对象。</summary>
    /// <returns>调用方拥有且必须 Dispose 的对象；没有匹配项时为 null，查询错误可抛异常。</returns>
    /// <param name="vmId">虚拟机稳定 GUID。</param>
    /// <param name="scope">WMI 命名空间，默认取方法声明值。</param>
    /// <param name="ctx">连接上下文；null 使用本机当前身份。</param>
    public static ManagementObject? GetVmComputerSystem(
        Guid vmId,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        if (vmId == Guid.Empty) return null;

        ctx ??= WmiContext.Local;
        var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
        string safe = Escape(vmId.ToString("D"));
        using var searcher = new ManagementObjectSearcher(
            ms,
            new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{safe}'"));
        using var col = searcher.Get();
        return col.Cast<ManagementObject>().FirstOrDefault();
    }

    /// <summary>从 VM 对象取得其当前已实现配置，而非检查点配置。</summary>
    /// <returns>调用方拥有且必须 Dispose 的配置对象；未找到时为 null。</returns>
    /// <param name="vmComputerSystem">调用方持有的 VM WMI 对象，查询期间必须有效；本方法不释放该输入对象。</param>
    public static ManagementObject? GetVmSettings(ManagementObject vmComputerSystem)
    {
        using var related = vmComputerSystem.GetRelated(
            "Msvm_VirtualSystemSettingData",
            "Msvm_SettingsDefineState",
            null, null, null, null, false, null);
        return related.Cast<ManagementObject>().FirstOrDefault();
    }

    // ── 辅助工具 ──────────────────────────────────────────────────

    /// <summary>转义 WQL 字符串值中的反斜杠和单引号。</summary>
    /// <returns>转义后的文本，不额外添加外围单引号。</returns>
    /// <param name="value">非 null 的原始字符串值，不是整段 WQL。</param>
    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>尝试读取并转换 WMI 属性，读取或转换失败时采用默认值。</summary>
    /// <returns>转换后的属性值，或 defaultValue。</returns>
    /// <param name="obj">有效的借用 WMI 对象。</param>
    /// <param name="name">属性名称。</param>
    /// <param name="defaultValue">不存在、为空或转换失败时的回退值。</param>
    /// <typeparam name="T">目标属性类型。</typeparam>
    public static T? Prop<T>(ManagementObject obj, string name, T? defaultValue = default)
    {
        try
        {
            var val = obj[name];
            if (val is null) return defaultValue;
            return (T)Convert.ChangeType(val, typeof(T));
        }
        catch { return defaultValue; }
    }

    /// <summary>读取 WMI 属性并转为字符串，属性值为 null 时返回空字符串。</summary>
    /// <returns>属性文本；属性访问自身抛出的异常不在本方法捕获。</returns>
    /// <param name="obj">有效的 WMI 对象。</param>
    /// <param name="name">要读取的属性名称。</param>
    public static string PropStr(ManagementObject obj, string name)
        => obj[name]?.ToString() ?? string.Empty;

    /// <summary>清除进程内已缓存 WMI 连接上下文，供后续查询重新建立连接。</summary>
    /// <remarks>不撤销已经提交的作业，也不释放由调用方持有的对象。</remarks>
    public static void ClearConnectionCache() => WmiConnectionCache.Clear();

    // ── 内部：异步 Job 等待与结果获取 ─────────────────────────────

    /// <summary>
    /// 异步 Job 完成后按 resultField 语义从 Msvm_AffectedJobElement 关联取结果对象路径。
    /// 关联里混着 Msvm_ComputerSystem 与各类设置对象：ResultingSystem 取前者，其余(如
    /// ResultingResourceSettings)取后者。
    /// </summary>
    private static string[] QueryJobAffectedPaths(ManagementScope ms, string jobPath, string resultField)
    {
        int colon = jobPath.IndexOf(":Msvm_", StringComparison.OrdinalIgnoreCase);
        string rel = colon >= 0 ? jobPath.Substring(colon + 1) : jobPath;
        using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(
            $"ASSOCIATORS OF {{{rel}}} WHERE AssocClass=Msvm_AffectedJobElement"));
        using var col = searcher.Get();
        var systems = new List<string>();
        var others = new List<string>();
        foreach (ManagementBaseObject baseObj in col)
        {
            if (baseObj is not ManagementObject obj) continue;
            using (obj)
            {
                if (string.Equals(obj.ClassPath.ClassName, "Msvm_ComputerSystem", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(obj.ClassPath.ClassName, "Msvm_VirtualEthernetSwitch", StringComparison.OrdinalIgnoreCase))
                    systems.Add(obj.Path.Path);
                else
                    others.Add(obj.Path.Path);
            }
        }
        return resultField == "ResultingSystem" ? systems.ToArray() : others.ToArray();
    }

    private static async Task<ApiResponse> WaitForJobAsync(
        string? jobPath,
        string scope,
        WmiContext ctx,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(jobPath))
            return WmiJobWaiter.Unknown("WMI 方法已提交异步任务，但没有返回作业身份；无法确认最终结果。");
        try
        {
            var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
            return await WaitForJobOnScopeAsync(ms, jobPath, cancellationToken,
                string.Equals(scope, WmiScope.HyperV, StringComparison.OrdinalIgnoreCase), progress, timeout);
        }
        catch (Exception ex)
        {
            return WmiJobWaiter.FromException(ex, submitted: true);
        }
    }

    private static async Task<ApiResponse> WaitForJobOnScopeAsync(ManagementScope scope, string jobPath,
        CancellationToken cancellationToken, bool hyperV, IProgress<int>? progress = null, TimeSpan? timeout = null)
    {
        try
        {
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
            return await WmiJobWaiter.WaitAsync(() =>
            {
                job.Get();
                return WmiJobWaiter.Read(job);
            }, cancellationToken, hyperV, progress, timeout);
        }
        catch (Exception ex) { return WmiJobWaiter.FromException(ex, submitted: true); }
    }

    private static int ReadReturnCode(ManagementBaseObject output) =>
        Convert.ToInt32(output["ReturnValue"] ?? throw new InvalidOperationException("WMI 方法缺少 ReturnValue。"));

    private static ApiResponse<T> CopyFailure<T>(ApiResponse response) =>
        ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
}

// ══════════════════════════════════════════════════════════════════
//  ManagementObjectExtensions
// ══════════════════════════════════════════════════════════════════
/// <summary>为借用的 WMI 对象提供属性探测及尽力读写辅助，不提交对象变更。</summary>
internal static class ManagementObjectExtensions
{
    /// <summary>检查 WMI 对象的属性集合是否包含指定属性。</summary>
    /// <returns>找到属性时为 true；原生对象访问异常可传播。</returns>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">属性名称。</param>
    public static bool HasProperty(this ManagementObject obj, string propName)
        => obj.Properties.Cast<PropertyData>()
               .Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));

    /// <summary>仅在值可用且对象含指定属性时赋值，不自动提交到 WMI。</summary>
    /// <remarks>跳过空值和缺失属性；属性探测或赋值异常向调用方传播。</remarks>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">目标属性名称。</param>
    /// <param name="value">待写入值；null 会跳过赋值，字符串重载也跳过空字符串。</param>
    /// <typeparam name="T">要写入的可空值类型。</typeparam>
    public static void TrySet<T>(this ManagementObject obj, string propName, T? value)
        where T : struct
    {
        if (value.HasValue && obj.HasProperty(propName))
            obj[propName] = value.Value;
    }

    /// <summary>仅在值可用且对象含指定属性时赋值，不自动提交到 WMI。</summary>
    /// <remarks>跳过空值和缺失属性；属性探测或赋值异常向调用方传播。</remarks>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">目标属性名称。</param>
    /// <param name="value">待写入值；null 会跳过赋值，字符串重载也跳过空字符串。</param>
    public static void TrySet(this ManagementObject obj, string propName, string? value)
    {
        if (!string.IsNullOrEmpty(value) && obj.HasProperty(propName))
            obj[propName] = value;
    }

    /// <summary>目标有指定属性时尝试赋值，包括 null 值。</summary>
    /// <remarks>跳过不存在的属性，但属性访问或写入异常可传播；不自动提交到 WMI。</remarks>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">目标属性名称。</param>
    /// <param name="value">待写入值，可为 null。</param>
    public static void TrySetAlways(this ManagementObject obj, string propName, object? value)
    {
        if (obj.HasProperty(propName))
            obj[propName] = value;
    }

    /// <summary>尝试读取并转换可空值类型的 WMI 属性。</summary>
    /// <returns>转换后的值；缺失、空值或类型转换失败时为 null。属性访问异常仍可传播。</returns>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">要读取的属性名称。</param>
    /// <typeparam name="T">要读取的值类型。</typeparam>
    public static T? TryGet<T>(this ManagementObject obj, string propName)
        where T : struct
    {
        if (!obj.HasProperty(propName)) return null;
        var val = obj[propName];
        if (val == null) return null;
        try { return (T)Convert.ChangeType(val, typeof(T)); }
        catch { return null; }
    }

    /// <summary>尝试把 WMI 属性读取为字节数值。</summary>
    /// <returns>属性的字节值；缺失或空值时为 null，访问/数值转换异常可传播。</returns>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">要读取的属性名称。</param>
    public static byte? TryGetByte(this ManagementObject obj, string propName)
    {
        if (!obj.HasProperty(propName)) return null;
        var val = obj[propName];
        return val == null ? null : Convert.ToByte(val);
    }

    /// <summary>尝试把 WMI 属性读取为字符串。</summary>
    /// <returns>属性字符串；缺失或空值时为 null，属性访问异常可传播。</returns>
    /// <param name="obj">调用方持有的有效 WMI 对象。</param>
    /// <param name="propName">要读取的属性名称。</param>
    public static string? TryGetString(this ManagementObject obj, string propName)
    {
        if (!obj.HasProperty(propName)) return null;
        return obj[propName]?.ToString();
    }
}
