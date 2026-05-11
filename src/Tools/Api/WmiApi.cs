using System.Diagnostics;
using System.Management;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace ExHyperV.Tools.Api;

// ══════════════════════════════════════════════════════════════════
//  WMI 命名空间常量
//  所有 scope 字符串集中在这里，服务层不允许硬编码路径
// ══════════════════════════════════════════════════════════════════
public static class WmiScope
{
    public const string HyperV = @"root\virtualization\v2";
    public const string CimV2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string StdCimV2 = @"root\StandardCimv2";
    public const string Wmi = @"root\wmi";
    public const string DeviceGuard = @"root\Microsoft\Windows\DeviceGuard";

    // Storage 和 StdCimV2 只支持 CIM API，其他用 System.Management
    internal static bool RequiresCim(string scope) =>
        scope.Equals(Storage, StringComparison.OrdinalIgnoreCase) ||
        scope.Equals(StdCimV2, StringComparison.OrdinalIgnoreCase);
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
//  System.Management 路径：按 (scope, host) 缓存 ManagementScope
//  CIM 路径：按 (scope, host) 缓存 CimSession
// ══════════════════════════════════════════════════════════════════
internal static class WmiConnectionCache
{
    private static readonly Dictionary<string, ManagementScope> _mgmtCache = new();
    private static readonly Dictionary<string, CimSession> _cimCache = new();
    private static readonly Dictionary<string, DateTime> _mgmtLastChecked = new();
    private static readonly object _mgmtLock = new();
    private static readonly object _cimLock = new();

    // 30 秒内同一连接不重复做健康检查，避免高频轮询时产生额外开销
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    public static ManagementScope GetManagementScope(string scope, WmiContext ctx)
    {
        string key = $"{ctx.Host}|{scope}";
        lock (_mgmtLock)
        {
            if (_mgmtCache.TryGetValue(key, out var cached) && cached.IsConnected)
            {
                // 30 秒内直接复用，不做健康检查
                if (_mgmtLastChecked.TryGetValue(key, out var lastChecked) &&
                    DateTime.Now - lastChecked < HealthCheckInterval)
                {
                    return cached;
                }

                // 超过 30 秒，做一次轻量验证
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
                    // 连接已断，移除缓存重新建立
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

    private static readonly Dictionary<string, DateTime> _cimLastChecked = new();

    public static CimSession GetCimSession(string scope, WmiContext ctx)
    {
        string key = $"{ctx.Host}|{scope}";
        lock (_cimLock)
        {
            if (_cimCache.TryGetValue(key, out var cached))
            {
                // 30 秒内直接复用，不做健康检查
                if (_cimLastChecked.TryGetValue(key, out var lastChecked) &&
                    DateTime.Now - lastChecked < HealthCheckInterval)
                {
                    return cached;
                }

                // 超过 30 秒，做一次轻量验证
                try
                {
                    cached.QueryInstances(scope, "WQL",
                        "SELECT * FROM __Namespace WHERE Name='_test_'");
                    _cimLastChecked[key] = DateTime.Now;
                    return cached;
                }
                catch
                {
                    cached.Dispose();
                    _cimCache.Remove(key);
                    _cimLastChecked.Remove(key);
                }
            }

            CimSession session;
            if (ctx.IsLocal)
            {
                session = CimSession.Create(null);
            }
            else
            {
                var options = new WSManSessionOptions
                {
                    DestinationPort = 5985,
                };
                if (!string.IsNullOrEmpty(ctx.Username))
                {
                    options.AddDestinationCredentials(new CimCredential(
                        PasswordAuthenticationMechanism.Default,
                        null,
                        ctx.Username,
                        MakePsCredPassword(ctx.Password)));
                }
                session = CimSession.Create(ctx.Host, options);
            }

            _cimCache[key] = session;
            _cimLastChecked[key] = DateTime.Now;
            return session;
        }
    }

    public static void Clear()
    {
        lock (_mgmtLock)
        {
            _mgmtCache.Clear();
            _mgmtLastChecked.Clear();
        }
        lock (_cimLock)
        {
            foreach (var s in _cimCache.Values) s.Dispose();
            _cimCache.Clear();
            _cimLastChecked.Clear();
        }
    }

    // CIM 远程连接需要 SecureString 密码
    private static System.Security.SecureString MakePsCredPassword(string? password)
    {
        var ss = new System.Security.SecureString();
        foreach (var c in password ?? string.Empty) ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }
}

// ══════════════════════════════════════════════════════════════════
//  WmiApi — 核心静态类
//  所有 WMI/CIM 调用的唯一入口
//  服务层不允许直接使用 ManagementObjectSearcher / CimSession
// ══════════════════════════════════════════════════════════════════
public static class WmiApi
{
    // ── A. 查询多行 ───────────────────────────────────────────────

    /// <summary>
    /// 执行 WQL 查询，将每行映射为 <typeparamref name="T"/>。
    /// 自动根据 scope 选择 CIM 或 ManagementObject 路径。
    /// </summary>
    public static Task<ApiResponse<List<T>>> QueryAsync<T>(
        string wql,
        Func<ManagementObject, T> mapper,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;

        // Storage / StdCimV2 命名空间强制走 CIM
        if (WmiScope.RequiresCim(scope))
            throw new InvalidOperationException(
                $"Scope '{scope}' 只支持 CIM API，请使用接受 Func<CimInstance, T> 的重载。");

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
    /// CIM 路径重载，用于 Storage / StdCimV2 命名空间。
    /// </summary>
    public static Task<ApiResponse<List<T>>> QueryAsync<T>(
        string wql,
        Func<CimInstance, T> mapper,
        string scope = WmiScope.Storage,
        WmiContext? ctx = null)
    {
        ctx ??= WmiContext.Local;

        return Task.Run(() =>
        {
            try
            {
                var session = WmiConnectionCache.GetCimSession(scope, ctx);
                var list = new List<T>();

                foreach (var instance in session.QueryInstances(scope, "WQL", wql))
                {
                    try { list.Add(mapper(instance)); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WmiApi.CimQuery] mapper error: {ex.Message}");
                    }
                }

                return ApiResponse<List<T>>.Ok(list);
            }
            catch (CimException ex)
            {
                return ApiResponse<List<T>>.Fail(
                    ex.Message, (int)ex.NativeErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<T>>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

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
        WmiContext? ctx = null) where T : class
    {
        var response = await QueryAsync(wql, mapper, scope, ctx);

        if (!response.Success)
            return ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource);

        var first = response.Data?.FirstOrDefault();
        return first is null
            ? ApiResponse<T>.Empty()
            : ApiResponse<T>.Ok(first);
    }

    /// <summary>CIM 路径单行重载。</summary>
    public static async Task<ApiResponse<T>> QueryFirstAsync<T>(
        string wql,
        Func<CimInstance, T> mapper,
        string scope = WmiScope.Storage,
        WmiContext? ctx = null) where T : class
    {
        var response = await QueryAsync(wql, mapper, scope, ctx);

        if (!response.Success)
            return ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource);

        var first = response.Data?.FirstOrDefault();
        return first is null
            ? ApiResponse<T>.Empty()
            : ApiResponse<T>.Ok(first);
    }

    // ── E. 关联查询 ───────────────────────────────────────────────

    /// <summary>
    /// 关联查询：从已有对象出发，找到与其关联的目标类对象。
    /// 等效于 WMI 的 ASSOCIATORS OF 语句，或 obj.GetRelated()。
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
    /// CIM 关联查询重载。
    /// </summary>
    public static Task<ApiResponse<List<T>>> QueryRelatedAsync<T>(
        CimSession session,
        CimInstance source,
        string scope,
        string associationClass,
        string resultClass,
        string sourceRole,
        string resultRole,
        Func<CimInstance, T> mapper)
    {
        return Task.Run(() =>
        {
            try
            {
                var list = new List<T>();

                var related = session.EnumerateAssociatedInstances(
                    scope, source, associationClass, resultClass, sourceRole, resultRole);

                foreach (var instance in related)
                {
                    try { list.Add(mapper(instance)); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WmiApi.CimQueryRelated] mapper error: {ex.Message}");
                    }
                }

                return ApiResponse<List<T>>.Ok(list);
            }
            catch (CimException ex)
            {
                return ApiResponse<List<T>>.Fail(
                    ex.Message, (int)ex.NativeErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<T>>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    // ── F. 路径实例化 ─────────────────────────────────────────────

    /// <summary>
    /// 通过 WMI 对象路径字符串直接获取对象。
    /// 典型场景：BootSourceOrder 里存的是路径数组，逐个实例化。
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

    // ── C. 调用方法 ───────────────────────────────────────────────

    /// <summary>
    /// 在 WQL 查到的对象上调用 WMI 方法。
    /// 自动处理 ReturnValue=4096 的异步 Job，等待完成后返回。
    /// setParams：用来设置入参，例如 p => p["SystemSettings"] = xml
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
    /// 在已有的 ManagementObject 上直接调用方法。
    /// 场景：已经拿到对象，不想再查一次。
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

    // ── D. 改属性提交 ─────────────────────────────────────────────

    /// <summary>
    /// 拿到 WMI 对象，在回调里修改属性，由 WmiApi 自动序列化并调用
    /// ModifySystemSettings 或 ModifyResourceSettings 提交。
    ///
    /// submitMethod：提交用的方法名，默认 ModifySystemSettings
    /// submitParamName：XML 放进哪个参数，默认 SystemSettings
    ///
    /// 典型场景：改虚拟机名称、改内存设置、改处理器设置。
    /// </summary>
    public static async Task<ApiResponse> WithObjectAsync(
        string wql,
        Action<ManagementObject> modifier,
        string submitMethod = "ModifySystemSettings",
        string submitParamName = "SystemSettings",
        bool wrapInArray = false,
        string scope = WmiScope.HyperV,
        WmiContext? ctx = null,
        // 默认指向 Hyper-V 管理服务，非 Hyper-V 场景可自行传入其他服务的 WQL
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

                // 让服务层修改属性
                modifier(obj);

                // 序列化成 XML
                string xml = obj.GetText(TextFormat.CimDtd20);

                // 找到提交用的服务对象
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

    // ── 辅助：Hyper-V 管理服务快捷获取 ───────────────────────────

    /// <summary>
    /// 获取 Msvm_VirtualSystemManagementService。
    /// 这是 Hyper-V 几乎所有操作的入口，频繁使用。
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

    // ── 辅助工具 ──────────────────────────────────────────────────

    /// <summary>
    /// 转义 WQL 字符串中的单引号，防止注入。
    /// 使用方式：$"WHERE ElementName = '{WmiApi.Escape(name)}'"
    /// </summary>
    public static string Escape(string value) => value.Replace("'", "\\'");

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

    // ── 内部：异步 Job 等待 ───────────────────────────────────────

    private static async Task<ApiResponse> WaitForJobAsync(
        string jobPath,
        string scope,
        WmiContext ctx,
        CancellationToken cancellationToken)
    {
        // 内置默认超时 2 分钟，防止 Hyper-V Job 卡死导致线程永久挂起
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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

                // 7 = Completed
                if (jobState == 7) return ApiResponse.Ok();

                // > 7 = 终态但不是成功（Exception / Terminated / Killed）
                if (jobState > 7)
                {
                    string error = job["ErrorDescription"]?.ToString()
                                ?? job["Description"]?.ToString()
                                ?? $"Job failed with state {jobState}";
                    return ApiResponse.Fail(error, jobState, ApiErrorSource.Wmi);
                }

                // 2=New 3=Starting 4=Running 5=Suspended 6=ShuttingDown → 继续等
                await Task.Delay(300, linkedCts.Token);
            }

            // 区分是用户取消还是超时
            return timeoutCts.Token.IsCancellationRequested
                ? ApiResponse.Fail("Operation timed out (2 min)", -1, ApiErrorSource.Wmi)
                : ApiResponse.Fail("Operation cancelled", -1, ApiErrorSource.None);
        }
        catch (OperationCanceledException)
        {
            return timeoutCts.Token.IsCancellationRequested
                ? ApiResponse.Fail("Operation timed out (2 min)", -1, ApiErrorSource.Wmi)
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