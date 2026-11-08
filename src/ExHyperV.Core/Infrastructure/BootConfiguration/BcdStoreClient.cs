using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

internal enum BcdSetting
{
    HypervisorLaunchType,
    HypervisorSchedulerType,
}

// 只管理本项目使用的当前启动项整数元素；不公开通用 BCD 编辑或原生对象。
internal static class BcdStoreClient
{
    private const string CurrentBootObject = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";

    internal static ApiResponse<ulong> Read(BcdSetting setting) => Read(setting, OpenCurrent);

    internal static Task<ApiResponse> SetAsync(BcdSetting setting, ulong value)
        => Task.Run(() => Set(setting, value, OpenCurrent));

    // 接缝只覆盖一次打开、读取、写入及释放，正式实现不持有进程级 BCD 会话。
    internal interface ISession : IDisposable
    {
        ApiResponse<ulong> Read(uint element);
        ApiResponse Write(uint element, ulong value);
    }

    internal static ApiResponse<ulong> Read(BcdSetting setting, Func<ISession> open)
    {
        if (!TryGetElement(setting, out uint element, out _))
            return ApiResponse<ulong>.Fail("未指定受支持的 BCD 配置项。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            using ISession session = open();
            return session.Read(element);
        }
        catch (Exception ex)
        {
            return ReadFailure("读取当前启动项失败", ex);
        }
    }

    internal static ApiResponse Set(BcdSetting setting, ulong value, Func<ISession> open)
    {
        if (!TryGetElement(setting, out uint element, out ulong maxValue) || value > maxValue)
            return ApiResponse.Fail("BCD 配置项或取值无效。", kind: ApiErrorKind.InvalidArgument);
        bool writeStarted = false;
        try
        {
            using ISession session = open();
            ApiResponse written;
            try { writeStarted = true; written = session.Write(element, value); }
            catch (Exception ex) { return Failure("BCD 写入调用未能返回确定结果", ex, writeSubmitted: true); }
            if (!written.Success) return written;

            ApiResponse<ulong> actual;
            try { actual = session.Read(element); }
            catch (Exception ex) { actual = ReadFailure("读回当前启动项失败", ex); }
            if (!actual.Success)
                return ApiResponse.Fail($"BCD 写入已返回成功，但读回复核失败：{actual.Error}", actual.Code,
                    actual.ErrorSource, actual.Exception, ApiErrorKind.OutcomeUnknown);
            if (!actual.HasData)
                return ApiResponse.Fail("BCD 写入已返回成功，但读回时该配置项不存在。", kind: ApiErrorKind.Conflict);
            if (actual.Data != value)
                return ApiResponse.Fail($"BCD 写入已返回成功，但读回值为 {actual.Data}，预期为 {value}。", kind: ApiErrorKind.Conflict);
            return ApiResponse.Ok();
        }
        catch (Exception ex)
        {
            return Failure("处理当前启动项失败", ex, writeStarted);
        }
    }

    private static bool TryGetElement(BcdSetting setting, out uint element, out ulong maxValue)
    {
        (element, maxValue) = setting switch
        {
            BcdSetting.HypervisorLaunchType => (0x250000F0u, 1ul), // Off=0、Auto=1。
            // Classic=0、Core=1、Root=2；由独立 BCD 文件上的 BCDEdit/WMI 双向验证确认。
            // 这是 BCD 配置枚举，与 Hyper-V 启动事件的调度器编号不同。
            BcdSetting.HypervisorSchedulerType => (0x2500011Au, 2ul),
            _ => (0u, 0ul),
        };
        return element != 0;
    }

    private static ISession OpenCurrent()
    {
        var scope = new ManagementScope(@"\\.\root\WMI", new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
        });
        scope.Connect();
        using var storeClass = new ManagementClass(scope, new ManagementPath("BcdStore"), null);
        using (var opened = Invoke(storeClass, "OpenStore", ("File", "")))
        using (var storeInfo = RequiredObject(opened, "Store")) { }
        using var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=\"\""), null);
        using var result = Invoke(store, "OpenObject", ("Id", CurrentBootObject));
        using var info = RequiredObject(result, "Object");
        if (!Guid.TryParse(info["Id"] as string, out Guid id) || id == Guid.Empty)
            throw new ProviderFailureException("BCD Provider 未返回有效的当前启动项身份。");
        // 仅用已解析 GUID 构造系统存储对象，不接受 Provider 回传的任意存储路径。
        var current = new ManagementObject(scope,
            new ManagementPath($"BcdObject.Id=\"{id:B}\",StoreFilePath=\"\""), null);
        return new WmiSession(current);
    }

    private sealed class WmiSession(ManagementObject current) : ISession
    {
        public ApiResponse<ulong> Read(uint element)
        {
            ManagementBaseObject result;
            try
            {
                result = Invoke(current, "GetElement", ("Type", element));
            }
            catch (Exception ex) when (IsMissingElement(ex))
            {
                // 只在 GetElement 阶段解释为缺省；打开存储或启动对象失败不得成为 Empty。
                // 元素不存在与整个对象已消失可能使用相同原生错误，先重查当前对象。
                try { current.Get(); return ApiResponse<ulong>.Empty(); }
                catch (Exception identityError) { return ReadFailure("BCD 配置项缺失，且当前启动项身份无法重新核验", identityError); }
            }
            catch (Exception ex) { return ReadFailure("读取 BCD 配置项失败", ex); }
            using (result)
            {
                try
                {
                    using var item = RequiredObject(result, "Element");
                    if (item["Type"] is not uint returnedType || returnedType != element || item["Integer"] is not ulong value)
                        return ApiResponse<ulong>.Fail("BCD Provider 返回的元素类型或整数值无效。",
                            source: ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);
                    return ApiResponse<ulong>.Ok(value);
                }
                catch (Exception ex) { return ReadFailure("BCD Provider 返回的配置项无效", ex); }
            }
        }

        public ApiResponse Write(uint element, ulong value)
        {
            bool submitted = false;
            try
            {
                using var input = current.GetMethodParameters("SetIntegerElement");
                input["Type"] = element;
                input["Integer"] = value;
                submitted = true;
                using var output = current.InvokeMethod("SetIntegerElement", input, null);
                RequireSuccess(output, "SetIntegerElement");
                return ApiResponse.Ok();
            }
            catch (Exception ex) { return Failure("写入 BCD 配置项失败", ex, submitted); }
        }

        public void Dispose() => current.Dispose();
    }

    private static ManagementBaseObject Invoke(ManagementObject target, string method, params (string Name, object Value)[] values)
    {
        using var input = target.GetMethodParameters(method);
        foreach (var (name, value) in values) input[name] = value;
        ManagementBaseObject? output = target.InvokeMethod(method, input, null);
        try { RequireSuccess(output, method); return output!; }
        catch { output?.Dispose(); throw; }
    }

    private static void RequireSuccess(ManagementBaseObject? output, string method)
    {
        if (output?["ReturnValue"] is not bool success)
            throw new ProviderFailureException($"BCD Provider 的 {method} 未返回布尔结果。");
        if (!success)
            throw new ProviderFailureException($"BCD Provider 的 {method} 返回失败，未提供具体原生错误。请核对调用权限及 BCD 存储状态。", definitiveFailure: true);
    }

    private static ManagementBaseObject RequiredObject(ManagementBaseObject output, string property)
        => output[property] as ManagementBaseObject
            ?? throw new ProviderFailureException($"BCD Provider 未返回 {property} 对象。");

    private static bool IsMissingElement(Exception ex)
        => ex is ManagementException { ErrorCode: ManagementStatus.NotFound }
            || ex is COMException && unchecked((uint)ex.HResult) == 0xD0000225u;

    private static ApiResponse<ulong> ReadFailure(string message, Exception ex)
    {
        ApiResponse failure = Failure(message, ex);
        return ApiResponse<ulong>.Fail(failure.Error, failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
    }

    private static ApiResponse Failure(string message, Exception ex, bool writeSubmitted = false)
    {
        int code = ex is ManagementException me ? (int)me.ErrorCode
            : ex is Win32Exception we ? we.NativeErrorCode
            : ex is ProviderFailureException ? -1 : ex.HResult;
        ApiErrorSource source = ex switch
        {
            ManagementException or ProviderFailureException => ApiErrorSource.Wmi,
            COMException => ApiErrorSource.Com,
            Win32Exception => ApiErrorSource.Win32,
            _ => ApiErrorSource.None,
        };
        ApiErrorKind kind = IsAccessDenied(ex) ? ApiErrorKind.AccessDenied
            : ex is ManagementException { ErrorCode: ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotSupported }
                ? ApiErrorKind.Unsupported
            : writeSubmitted && ex is not ProviderFailureException { DefinitiveFailure: true } ? ApiErrorKind.OutcomeUnknown
            : ApiErrorKind.OperationFailed;
        return ApiResponse.Fail($"{message}：{(string.IsNullOrWhiteSpace(ex.Message) ? $"0x{ex.HResult:X8}" : ex.Message)}",
            code, source, ex, kind);
    }

    private static bool IsAccessDenied(Exception ex)
        => ex is UnauthorizedAccessException
            || ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied }
            || ex is Win32Exception { NativeErrorCode: 5 or 1314 }
            || unchecked((uint)ex.HResult) is 0x80070005u or 0x80070522u or 0x80041003u or 0xD0000022u or 0xD0000061u;

    private sealed class ProviderFailureException(string message, bool definitiveFailure = false) : Exception(message)
    {
        internal bool DefinitiveFailure { get; } = definitiveFailure;
    }
}
