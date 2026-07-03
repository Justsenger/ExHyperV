using System.Diagnostics;
using System.Management;

namespace ExHyperV.Tools;

// ══════════════════════════════════════════════════════════════════
//  WMI 命名空间常量
//  所有 scope 字符串集中在这里，服务层不允许硬编码路径
// ══════════════════════════════════════════════════════════════════
public static class WmiScope
{
    public const string HyperV = @"root\virtualization\v2";
    public const string CimV2 = @"root\cimv2";
    public const string Storage = @"Root\Microsoft\Windows\Storage";
    public const string StdCimV2 = @"root\StandardCimv2";
    public const string Wmi = @"root\wmi";
    public const string DeviceGuard = @"root\Microsoft\Windows\DeviceGuard";
    public const string Hgs = @"root\microsoft\windows\hgs";
}

// ══════════════════════════════════════════════════════════════════
//  WmiContext — 连接上下文
//  本机用 WmiContext.Local（静态单例，零分配）
//  远程用 WmiContext.Remote(host, credential)
// ══════════════════════════════════════════════════════════════════
public sealed class WmiContext
{
    public static readonly WmiContext Local = new();

    public string Host { get; }
    public string? Username { get; }
    public string? Password { get; }
    public bool IsLocal => Host == ".";

    private WmiContext()
    {
        Host = ".";
    }

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
//  按 (scope, host) 缓存 ManagementScope
// ══════════════════════════════════════════════════════════════════
internal static class WmiConnectionCache
{
    private static readonly Dictionary<string, ManagementScope> _mgmtCache = new();
    private static readonly Dictionary<string, DateTime> _mgmtLastChecked = new();
    private static readonly object _mgmtLock = new();

    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    public static ManagementScope GetManagementScope(string scope, WmiContext ctx)
    {
        string key = $"{ctx.Host}|{scope}";
        lock (_mgmtLock)
        {
            if (_mgmtCache.TryGetValue(key, out var cached) && cached.IsConnected)
            {
                if (_mgmtLastChecked.TryGetValue(key, out var lastChecked) &&
                    DateTime.Now - lastChecked < HealthCheckInterval)
                {
                    return cached;
                }

                try
                {
                    using var searcher = new ManagementObjectSearcher(cached,
                        new ObjectQuery("SELECT Name FROM __Namespace WHERE Name='_health_check_'"));
                    searcher.Get();
                    _mgmtLastChecked[key] = DateTime.Now;
                    return cached;
                }
                catch
                {
                    _mgmtCache.Remove(key);
                    _mgmtLastChecked.Remove(key);
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
            _mgmtCache[key] = ms;
            _mgmtLastChecked[key] = DateTime.Now;
            return ms;
        }
    }

    public static void Clear()
    {
        lock (_mgmtLock)
        {
            _mgmtCache.Clear();
            _mgmtLastChecked.Clear();
        }
    }
}

// ══════════════════════════════════════════════════════════════════
//  WmiApi — 静态类
//  所有 WMI 调用的唯一入口
//  服务层不允许直接使用 ManagementObjectSearcher
// ══════════════════════════════════════════════════════════════════
public static class WmiApi
{
    // ── A. 查询多行 ───────────────────────────────────────────────

    /// <summary>
    /// 执行 WQL 查询，将每行映射为 <typeparamref name="T"/>。
    /// </summary>
    public static Task<ApiResponse<List<T>>> QueryAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
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
                        try { list.Add(mapper(obj)); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WmiApi.Query] mapper error: {ex.Message}");
                        }
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

    /// <summary>
    /// 同 QueryAsync，原 CIM 路径查询的替代。
    /// Storage / StdCimV2 命名空间经过验证可直接使用旧版 API。
    /// </summary>
    public static Task<ApiResponse<List<T>>> QueryCimAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.Storage,
        WmiContext? ctx = null)
        => QueryAsync(wql, mapper, scope, ctx);

    // ── B. 查询单行 ───────────────────────────────────────────────

    /// <summary>
    /// 查询第一个匹配对象。
    /// 没有匹配 → ApiResponse.Empty()
    /// 查询失败 → ApiResponse.Fail(...)
    /// </summary>
    public static async Task<ApiResponse<T>> QueryFirstAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        var response = await QueryAsync(wql, mapper, scope, ctx);

        if (!response.Success)
            return ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource);

        if (response.Data == null || response.Data.Count == 0)
            return ApiResponse<T>.Empty();

        return ApiResponse<T>.Ok(response.Data[0]);
    }

    /// <summary>
    /// 同 QueryFirstAsync，原 CIM 路径单行查询的替代。
    /// </summary>
    public static Task<ApiResponse<T>> QueryFirstCimAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.Storage,
        WmiContext? ctx = null)
        => QueryFirstAsync(wql, mapper, scope, ctx);

    // ── C. 调用方法 ───────────────────────────────────────────────

    /// <summary>
    /// 在 WQL 查到的对象上调用 WMI 方法。
    /// 自动处理 ReturnValue=4096 的异步 Job，等待完成后返回。
    /// </summary>
    public static Task<ApiResponse> InvokeAsync(
        string wql,
        string methodName,
        Action<ManagementBaseObject>? setParams = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        CancellationToken cancellationToken = default)
    {
        ctx ??= WmiContext.Local;

        return Task.Run(async () =>
        {
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var target = collection.Cast<ManagementObject>().FirstOrDefault();

                if (target is null)
                    return ApiResponse.Fail($"WMI object not found: {wql}");

                return await InvokeOnObjectAsync(target, methodName, setParams, scope, ctx, cancellationToken);
            }
            catch (ManagementException ex)
            {
                return ApiResponse.Fail(ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 同 InvokeAsync，但额外返回完整的 outParams，
    /// 供调用方读取 ResultingResourceSettings 等 out 参数。
    /// </summary>
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
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var target = collection.Cast<ManagementObject>().FirstOrDefault();

                if (target is null)
                    return ApiResponse<string[]>.Fail($"WMI object not found: {wql}");

                using var inParams = target.GetMethodParameters(methodName);
                setParams?.Invoke(inParams);

                using var outParams = target.InvokeMethod(methodName, inParams, null);
                if (outParams is null)
                    return ApiResponse<string[]>.Fail($"Method '{methodName}' returned null");

                int returnValue = Convert.ToInt32(outParams["ReturnValue"]);

                if (returnValue == 4096)
                {
                    string jobPath = (string)outParams["Job"];
                    var jobResult = await WaitForJobAsync(jobPath, scope, ctx, cancellationToken);
                    if (!jobResult.Success)
                        return ApiResponse<string[]>.Fail(
                            jobResult.Error, jobResult.Code, jobResult.ErrorSource);

                    // 异步 Job 路径下 outParams 的结果字段不填充(实测立即读/Job 完成后读均为 null)，
                    // 结果须从 Job 的 Msvm_AffectedJobElement 关联取——与微软管理库同款取法。
                    return ApiResponse<string[]>.Ok(QueryJobAffectedPaths(ms, jobPath, resultField));
                }
                if (returnValue != 0)
                {
                    return ApiResponse<string[]>.Fail(
                        $"Method '{methodName}' returned code {returnValue}",
                        returnValue, ApiErrorSource.Wmi);
                }

                var raw = outParams[resultField];
                var resulting = raw is string[] arr ? arr :
                                raw is string s ? new[] { s } :
                                Array.Empty<string>();
                return ApiResponse<string[]>.Ok(resulting);
            }
            catch (ManagementException ex)
            {
                return ApiResponse<string[]>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<string[]>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 在已有的 ManagementObject 上直接调用方法。
    /// </summary>
    public static async Task<ApiResponse> InvokeOnObjectAsync(
        ManagementObject target,
        string methodName,
        Action<ManagementBaseObject>? setParams = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        CancellationToken cancellationToken = default)
    {
        ctx ??= WmiContext.Local;

        return await Task.Run(async () =>
        {
            try
            {
                using var inParams = target.GetMethodParameters(methodName);
                setParams?.Invoke(inParams);

                using var outParams = target.InvokeMethod(methodName, inParams, null);
                if (outParams is null)
                    return ApiResponse.Fail($"Method '{methodName}' returned null");

                int returnValue = Convert.ToInt32(outParams["ReturnValue"]);

                return returnValue switch
                {
                    0 => ApiResponse.Ok(),
                    4096 => await WaitForJobAsync(
                                (string)outParams["Job"], scope, ctx, cancellationToken),
                    _ => ApiResponse.Fail(
                                $"Method '{methodName}' returned code {returnValue}",
                                returnValue, ApiErrorSource.Wmi)
                };
            }
            catch (ManagementException ex)
            {
                return ApiResponse.Fail(ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 在 WMI 类上调用静态/类方法，支持嵌入对象参数（通过 Properties[].Value 赋值）。
    /// 用于 HGS 等需要传入嵌入实例的场景。
    /// </summary>
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
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
                using var cls = new ManagementClass(ms, new ManagementPath(className), null);
                using var inParams = cls.GetMethodParameters(methodName);
                setParams?.Invoke(inParams);

                var outParams = cls.InvokeMethod(methodName, inParams, null);
                if (outParams is null)
                    return ApiResponse<ManagementBaseObject>.Fail($"Method '{methodName}' returned null");

                // 成功时 outParams 的所有权转给调用方；失败/异常路径在此释放
                try
                {
                    int returnValue = Convert.ToInt32(outParams["ReturnValue"]);
                    if (returnValue == 4096)   // 异步 Job：等完成（与 InvokeOnObjectAsync 一致）
                    {
                        var jobResult = await WaitForJobAsync((string)outParams["Job"], scope, ctx, default);
                        if (!jobResult.Success)
                        {
                            outParams.Dispose();
                            return ApiResponse<ManagementBaseObject>.Fail(
                                jobResult.Error, jobResult.Code, jobResult.ErrorSource);
                        }
                    }
                    else if (returnValue != 0)
                    {
                        outParams.Dispose();
                        return ApiResponse<ManagementBaseObject>.Fail(
                            $"Method '{methodName}' returned code {returnValue}",
                            returnValue, ApiErrorSource.Wmi);
                    }

                    return ApiResponse<ManagementBaseObject>.Ok(outParams);
                }
                catch
                {
                    outParams.Dispose();
                    throw;
                }
            }
            catch (ManagementException ex)
            {
                return ApiResponse<ManagementBaseObject>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<ManagementBaseObject>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>
    /// 原 InvokeCimMethodAsync 的替代。
    /// 在已有 ManagementObject 上调用方法，直接委托 InvokeOnObjectAsync。
    /// </summary>
    public static Task<ApiResponse> InvokeCimMethodAsync(
        ManagementObject instance,
        string methodName,
        string scope,
        Action<ManagementBaseObject>? setParams = null,
        WmiContext? ctx = null)
        => InvokeOnObjectAsync(instance, methodName, setParams, scope, ctx);

    // ── D. 改属性提交 ─────────────────────────────────────────────

    /// <summary>
    /// 拿到 WMI 对象，在回调里修改属性，由 WmiApi 自动序列化并提交。
    /// </summary>
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
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(scope, ctx);

                using var searcher = new ManagementObjectSearcher(ms, new ObjectQuery(wql));
                using var collection = searcher.Get();
                using var obj = collection.Cast<ManagementObject>().FirstOrDefault();

                if (obj is null)
                    return ApiResponse.Fail($"WMI object not found: {wql}");

                modifier(obj);
                string xml = obj.GetText(TextFormat.CimDtd20);

                using var svcSearcher = new ManagementObjectSearcher(ms, new ObjectQuery(serviceWql));
                using var svcCollection = svcSearcher.Get();
                using var service = svcCollection.Cast<ManagementObject>().FirstOrDefault();

                if (service is null)
                    return ApiResponse.Fail($"Service not found: {serviceWql}");

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
            catch (ManagementException ex)
            {
                return ApiResponse.Fail(ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        }, cancellationToken);
    }

    // ── E. 关联查询 ───────────────────────────────────────────────

    /// <summary>
    /// 关联查询：从已有对象出发，找到与其关联的目标类对象。
    /// </summary>
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
                        try { list.Add(mapper(obj)); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WmiApi.QueryRelated] mapper error: {ex.Message}");
                        }
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

    /// <summary>
    /// QueryRelated 的旧版 API 实现。
    /// 无 sourceRole/resultRole 版本。
    /// </summary>
    public static Task<ApiResponse<List<T>>> QueryRelatedCimAsync<T>(
        ManagementObject source,
        string relatedClass,
        Func<ManagementObject, T> mapper,
        string? associationClass = null,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
        => QueryRelatedAsync(source, relatedClass, mapper, associationClass, scope, ctx);

    /// <summary>
    /// 原 QueryRelatedCimAsync 的替代，带 sourceRole/resultRole 参数。
    /// 注意：GetRelated 第5个参数对应 resultRole，第6个对应 sourceRole（与 CIM 对调）。
    /// 经过实测验证（test123 虚拟机，12条结果确认）。
    /// </summary>
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
                    resultRole,    // 第5个参数传 resultRole（实测确认）
                    sourceRole,    // 第6个参数传 sourceRole（实测确认）
                    false, null);

                foreach (var baseObj in related)
                {
                    if (baseObj is not ManagementObject obj) continue;
                    using (obj)
                    {
                        try { list.Add(mapper(obj)); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WmiApi.QueryRelatedCim] mapper error: {ex.Message}");
                        }
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

    /// <summary>
    /// 查询单个对象，在回调里使用，对象生命周期由 WmiApi 管理。
    /// 适用于需要对查到的对象做后续操作（如 GetRelated）的场景。
    /// 避免 obj => obj 导致 using 释放后对象失效的问题。
    /// </summary>
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

    /// <summary>
    /// 通过 WMI 对象路径字符串直接获取对象。
    /// </summary>
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
    /// 获取 Msvm_VirtualSystemManagementService。
    /// 调用方负责 Dispose。
    /// </summary>
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

    /// <summary>
    /// 获取虚拟机的 Msvm_ComputerSystem 对象。
    /// 调用方负责 Dispose。
    /// </summary>
    public static ManagementObject? GetVmComputerSystem(
        string vmName,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;
        var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
        string safe = vmName.Replace("'", "\\'");
        using var searcher = new ManagementObjectSearcher(
            ms, new ObjectQuery(
                $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{safe}'"));
        using var col = searcher.Get();
        return col.Cast<ManagementObject>().FirstOrDefault();
    }

    /// <summary>
    /// 获取虚拟机当前激活的 VirtualSystemSettingData。
    /// 调用方负责 Dispose。
    /// </summary>
    public static ManagementObject? GetVmSettings(ManagementObject vmComputerSystem)
    {
        using var related = vmComputerSystem.GetRelated(
            "Msvm_VirtualSystemSettingData",
            "Msvm_SettingsDefineState",
            null, null, null, null, false, null);
        return related.Cast<ManagementObject>().FirstOrDefault();
    }

    // ── 辅助工具 ──────────────────────────────────────────────────

    /// <summary>
    /// 转义 WQL 字符串字面量，防止注入/查询破坏。必须先转反斜杠（WQL 转义符）再转撇号——
    /// 否则尾随反斜杠会吃掉闭合撇号、截断后续 AND 子句、改写 WHERE 语义。
    /// </summary>
    public static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>安全读取 ManagementObject 属性，失败返回默认值。</summary>
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

    /// <summary>安全读取字符串属性。</summary>
    public static string PropStr(ManagementObject obj, string name)
        => obj[name]?.ToString() ?? string.Empty;

    /// <summary>清理所有连接缓存（进程退出或测试时使用）。</summary>
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
                if (string.Equals(obj.ClassPath.ClassName, "Msvm_ComputerSystem", StringComparison.OrdinalIgnoreCase))
                    systems.Add(obj.Path.Path);
                else
                    others.Add(obj.Path.Path);
            }
        }
        return resultField == "ResultingSystem" ? systems.ToArray() : others.ToArray();
    }

    private static async Task<ApiResponse> WaitForJobAsync(
        string jobPath,
        string scope,
        WmiContext ctx,
        CancellationToken cancellationToken)
    {
        // 上限 30 分钟：大固定 VHD 创建/快照合并等长任务远超旧的 2 分钟，过早超时会误报失败
        // 并诱发上层回滚，而引擎侧 Job 仍在继续。主动取消语义由 cancellationToken 承担。
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            var ms = WmiConnectionCache.GetManagementScope(scope, ctx);
            using var job = new ManagementObject(ms, new ManagementPath(jobPath), null);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                job.Get();
                ushort jobState = (ushort)job["JobState"];

                // 7=Completed、32768=CompletedWithWarnings：微软管理库两者同判成功(带警告的操作已完成)
                if (jobState == 7 || jobState == 32768) return ApiResponse.Ok();

                if (jobState > 7)
                {
                    string error = job["ErrorDescription"]?.ToString()
                                ?? job["Description"]?.ToString()
                                ?? $"Job failed with state {jobState}";
                    return ApiResponse.Fail(error, jobState, ApiErrorSource.Wmi);
                }

                await Task.Delay(300, linkedCts.Token);
            }

            return timeoutCts.Token.IsCancellationRequested
                ? ApiResponse.Fail("Timed out waiting for the job; it may still be running in the background", -1, ApiErrorSource.Wmi)
                : ApiResponse.Fail("Operation cancelled", -1, ApiErrorSource.None);
        }
        catch (OperationCanceledException)
        {
            return timeoutCts.Token.IsCancellationRequested
                ? ApiResponse.Fail("Timed out waiting for the job; it may still be running in the background", -1, ApiErrorSource.Wmi)
                : ApiResponse.Fail("Operation cancelled", -1, ApiErrorSource.None);
        }
        catch (ManagementException ex)
        {
            return ApiResponse.Fail(ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"WaitForJob error: {ex.Message}", -1, ApiErrorSource.None, ex);
        }
    }
}

// ══════════════════════════════════════════════════════════════════
//  ManagementObjectExtensions
// ══════════════════════════════════════════════════════════════════
public static class ManagementObjectExtensions
{
    public static bool HasProperty(this ManagementObject obj, string propName)
        => obj.Properties.Cast<PropertyData>()
               .Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));

    public static void TrySet<T>(this ManagementObject obj, string propName, T? value)
        where T : struct
    {
        if (value.HasValue && obj.HasProperty(propName))
            obj[propName] = value.Value;
    }

    public static void TrySet(this ManagementObject obj, string propName, string? value)
    {
        if (!string.IsNullOrEmpty(value) && obj.HasProperty(propName))
            obj[propName] = value;
    }

    public static void TrySetAlways(this ManagementObject obj, string propName, object? value)
    {
        if (obj.HasProperty(propName))
            obj[propName] = value;
    }

    public static T? TryGet<T>(this ManagementObject obj, string propName)
        where T : struct
    {
        if (!obj.HasProperty(propName)) return null;
        var val = obj[propName];
        if (val == null) return null;
        try { return (T)Convert.ChangeType(val, typeof(T)); }
        catch { return null; }
    }

    public static byte? TryGetByte(this ManagementObject obj, string propName)
    {
        if (!obj.HasProperty(propName)) return null;
        var val = obj[propName];
        return val == null ? null : Convert.ToByte(val);
    }

    public static string? TryGetString(this ManagementObject obj, string propName)
    {
        if (!obj.HasProperty(propName)) return null;
        return obj[propName]?.ToString();
    }
}