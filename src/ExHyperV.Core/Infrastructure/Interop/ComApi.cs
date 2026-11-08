using System.Diagnostics;
using System.Runtime.InteropServices;


namespace ExHyperV.Tools;

// ══════════════════════════════════════════════════════════════════
//  ComApi — 公开封装层
// ══════════════════════════════════════════════════════════════════
/// <summary>通过共享 STA 执行器访问宿主 ICS 和热点 COM/WinRT 接口。</summary>
/// <remarks>ICS 变更作用于宿主全局，需要网络管理权限。执行器为进程共享资源；单次调用创建的 COM 对象仍由创建者负责释放。</remarks>
internal static class ComApi
{
    // Scoped fault source for contract tests. Ordinary callers always use the native boundary.
    private static readonly AsyncLocal<Func<Guid, Guid, ApiResponse?>?> SharingFault = new();
    internal static IDisposable UseSharingFault(Func<Guid, Guid, ApiResponse?> source)
    {
        var old = SharingFault.Value; SharingFault.Value = source; return new SharingFaultScope(old);
    }
    private sealed class SharingFaultScope(Func<Guid, Guid, ApiResponse?>? previous) : IDisposable
    { public void Dispose() => SharingFault.Value = previous; }
    /// <summary>按连接 GUID 为指定公共侧与私有侧启用 ICS，共享变更前先确认目标存在。</summary>
    /// <remarks>替换不匹配的现有主机共享配置。查找目标时短暂重试；后续失败可能保留已经修改的共享配置。</remarks>
    /// <returns>操作响应；无法找到目标或 COM 调用失败时返回失败。</returns>
    /// <param name="publicConnectionId">提供外网连接的上游网络连接 GUID。</param>
    /// <param name="privateConnectionId">向 VM 提供共享的下游网络连接 GUID。</param>
    public static Task<ApiResponse> EnableIcsSharingForConnectionsAsync(
        Guid publicConnectionId, Guid privateConnectionId)
        => Task.Run(() =>
        {
            if (SharingFault.Value?.Invoke(publicConnectionId, privateConnectionId) is { } substitute) return substitute;
            if (publicConnectionId == Guid.Empty || privateConnectionId == Guid.Empty)
                return ApiResponse.Fail("ICS target connection GUID is missing.");
            try
            {
                RunOnSta(() =>
                {
                    var timer = Stopwatch.StartNew();
                    while (true)
                    {
                        try
                        {
                            IcsCore.Enable(publicConnectionId.ToString(), privateConnectionId.ToString());
                            return;
                        }
                        catch (IcsTargetNotFoundException) when (timer.ElapsedMilliseconds < 5000)
                        {
                            Thread.Sleep(250);
                        }
                    }
                });
                return ApiResponse.Ok();
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Com, ex);
            }
        });

    /// <summary>枚举宿主可见网络连接及当前 ICS 角色。</summary>
    /// <returns>连接快照响应；失败与空列表有明确区别。</returns>
    public static Task<ApiResponse<List<IcsConnection>>> GetConnectionsAsync() => Task.Run(() =>
    {
        try { return ApiResponse<List<IcsConnection>>.Ok(RunOnSta(IcsCore.GetConnections)); }
        catch (Exception ex) { return ApiResponse<List<IcsConnection>>.Fail(ex.Message, ex.HResult, ApiErrorSource.Com, ex); }
    });

    /// <summary>按快照重新配置宿主全部 ICS 共享，并读取实际状态复核。</summary>
    /// <remarks>缺少恢复目标或复核不符会抛出 IcsException；中途失败可能已改变部分共享。</remarks>
    /// <param name="snapshot">先前读取的网络连接共享快照，按连接 GUID 匹配。</param>
    public static Task RestoreConnectionsAsync(IReadOnlyList<IcsConnection> snapshot) => Task.Run(() => RunOnSta(() =>
    {
        dynamic manager = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.HNetShare")!)!;
        try
        {
            var configs = new Dictionary<Guid, object>();
            foreach (var connection in manager.EnumEveryConnection)
                configs[Guid.Parse((string)manager.NetConnectionProps[connection].Guid)] = manager.INetSharingConfigurationForINetConnection[connection];
            foreach (var saved in snapshot.Where(c => c.Enabled))
                if (!configs.ContainsKey(saved.Id)) throw new IcsException($"ICS recovery target is missing: {saved.Id}");
            foreach (var current in configs)
            {
                dynamic config = current.Value;
                var desired = snapshot.SingleOrDefault(c => c.Id == current.Key && c.Enabled);
                if ((bool)config.SharingEnabled && (desired == null || (int)config.SharingConnectionType != desired.Type))
                    config.DisableSharing();
            }
            foreach (var saved in snapshot.Where(c => c.Enabled).OrderBy(c => c.Type))
            {
                dynamic config = configs[saved.Id];
                if (!(bool)config.SharingEnabled || (int)config.SharingConnectionType != saved.Type)
                    config.EnableSharing(saved.Type);
            }
        }
        finally { Marshal.FinalReleaseComObject(manager); }
        var actual = IcsCore.GetConnections().Where(c => c.Enabled).Select(c => (c.Id, c.Type)).ToHashSet();
        if (!actual.SetEquals(snapshot.Where(c => c.Enabled).Select(c => (c.Id, c.Type))))
            throw new IcsException("ICS recovery readback mismatch.");
    }));

    /// <summary>通过 WinRT 检查移动热点是否已启用或处于切换中。</summary>
    /// <returns>成功响应的数据为热点状态；无法判断返回失败，不把未知当作关闭。</returns>
    public static Task<ApiResponse<bool>> IsMobileHotspotActiveAsync() => Task.Run(() =>
    {
        try { return ApiResponse<bool>.Ok(RunOnSta(MobileHotspotInterop.ReadActive)); }
        catch (Exception ex) { return ApiResponse<bool>.Fail(ex.Message, ex.HResult, ApiErrorSource.Com, ex); }
    });

    private static class MobileHotspotInterop
    {
        // Windows SDK ABI: INetworkInformationStatics / IVectorView<ConnectionProfile> /
        // INetworkOperatorTetheringManagerStatics2 / INetworkOperatorTetheringManager.
        // 使用原生 WinRT ABI，保持现有目标框架；本类不包含 Start/StopTethering 调用。
        public static unsafe bool ReadActive()
        {
            Marshal.ThrowExceptionForHR(RoInitialize(0));
            nint information = 0, factory = 0, profiles = 0;
            try
            {
                information = Activate("Windows.Networking.Connectivity.NetworkInformation", new("5074f851-950d-4165-9c15-365619481eea"));
                factory = Activate("Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager", new("5b235412-35f0-49e7-9b08-16d278fbaa42"));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(information, 6))(information, &profiles));
                uint count = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(profiles, 7))(profiles, &count));
                Exception? error = null;
                for (uint i = 0; i < count; i++)
                {
                    nint profile = 0, manager = 0;
                    try
                    {
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(profiles, 6))(profiles, i, &profile));
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(factory, 7))(factory, profile, &manager));
                        int state = 0;
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(manager, 8))(manager, &state));
                        // 热点是全机状态；任选一个可查询的 profile 即可读取。
                        if (state == 2) return false; // Off
                        if (state is 1 or 3) return true; // On / InTransition
                        error = new InvalidOperationException("Mobile hotspot state is unknown.");
                    }
                    catch (Exception ex) { error = ex; }
                    finally { Release(manager); Release(profile); }
                }
                // 读取失败不能等同于热点关闭。
                throw error ?? new InvalidOperationException("No connection profile is available to check mobile hotspot state.");
            }
            finally { Release(profiles); Release(factory); Release(information); RoUninitialize(); }
        }

        private static unsafe nint Slot(nint instance, int index) => (*(nint**)instance)[index];
        private static void Release(nint instance) { if (instance != 0) Marshal.Release(instance); }

        private static nint Activate(string name, Guid iid)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var text));
            try
            {
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out var factory));
                return factory;
            }
            finally { WindowsDeleteString(text); }
        }

        [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
        [DllImport("combase.dll")] private static extern void RoUninitialize();
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint value);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    }

    // Serialize ICS calls on a native STA message pump.
    // Shared, lazy process-lifetime executor; callers own only their operation's COM objects.
    private static readonly Lazy<ComStaExecutor> Sta = new(() => new ComStaExecutor());
    /// <summary>在本进程共享的 STA 消息线程同步执行回调，供需要 STA 的 COM 操作使用。</summary>
    /// <remarks>调用线程会等待完成；回调异常传播。不要返回仍依赖 STA 线程访问的 COM 对象作为普通跨线程对象使用。</remarks>
    /// <param name="action">要在 STA 上完成的同步操作。</param>
    public static void RunOnSta(Action action) => RunOnSta(() => { action(); return true; });
    /// <summary>在本进程共享的 STA 消息线程同步执行回调，供需要 STA 的 COM 操作使用。</summary>
    /// <remarks>调用线程会等待完成；回调异常传播。不要返回仍依赖 STA 线程访问的 COM 对象作为普通跨线程对象使用。</remarks>
    /// <returns>泛型重载返回回调结果。</returns>
    /// <param name="func">要在 STA 上执行并返回结果的同步函数。</param>
    /// <typeparam name="T">同步回调的返回类型。</typeparam>
    public static T RunOnSta<T>(Func<T> func) => Sta.Value.Invoke(func);

}

// ══════════════════════════════════════════════════════════════════
//  IcsCore — ICS 内部实现
//  直接操作 HNetCfg.HNetShare COM 对象
//  必须在 STA 线程调用，由 ComApi.RunOnSta 保证
// ══════════════════════════════════════════════════════════════════
internal static class IcsCore
{
    private const string ProgId = "HNetCfg.HNetShare";

    // ICS 共享类型常量
    // 0 = ICSSHARINGTYPE_PUBLIC  （上游，连外网的那侧）
    // 1 = ICSSHARINGTYPE_PRIVATE （下游，虚拟机那侧）
    private const int Public = 0;
    private const int Private = 1;

    /// <summary>
    /// 为指定的公共和私有适配器启用 ICS。
    /// 先验证两个目标都存在再清场启用：目标缺失时立即失败且不动系统里任何现有共享
    /// ICS 全局仅一份，因此仅在新配置验证成功后清理旧共享。
    /// </summary>
    public static void Enable(string publicAdapterName, string privateAdapterName)
    {
        dynamic netShare = CreateNetShare();
        try
        {
            EnableOnManager(netShare, publicAdapterName, privateAdapterName);
        }
        finally
        {
            Marshal.FinalReleaseComObject(netShare);
        }
    }

    // 使用同一次 ICS 枚举定位并操作连接，避免先在另一套接口列表猜名称，再查找失败。
    internal static void EnableOnManager(
        dynamic netShare, string publicAdapterName, string privateAdapterName)
    {
        dynamic? publicConfig = null;
        dynamic? privateConfig = null;
        var enabledOthers = new List<object>();
        var previous = new List<(object Config, int Type)>();
        int publicMatches = 0;
        int privateMatches = 0;
        bool sameConnection = false;

        // 先完成目标定位，再修改全局 ICS 配置。
        foreach (var conn in netShare.EnumEveryConnection)
        {
            try
            {
                dynamic props = netShare.NetConnectionProps[conn];
                dynamic cfg = netShare.INetSharingConfigurationForINetConnection[conn];

                string connectionId = Guid.Parse((string)props.Guid).ToString();
                bool isPublic = string.Equals(connectionId, publicAdapterName, StringComparison.OrdinalIgnoreCase);
                bool isPrivate = string.Equals(connectionId, privateAdapterName, StringComparison.OrdinalIgnoreCase);
                if (isPublic) { publicConfig = cfg; publicMatches++; }
                if (isPrivate) { privateConfig = cfg; privateMatches++; }
                if (isPublic && isPrivate) sameConnection = true;
                bool isTarget = isPublic || isPrivate;
                if ((bool)cfg.SharingEnabled)
                {
                    previous.Add((cfg, (int)cfg.SharingConnectionType));
                    if (!isTarget) enabledOthers.Add(cfg);
                }
            }
            catch (Exception ex)
            {
                throw new IcsException("ICS enumeration failed before any configuration change.", ex);
            }
        }

        if (publicMatches > 1 || privateMatches > 1)
            throw new IcsException("Multiple ICS connections match the selected adapter.");
        if (sameConnection)
            throw new IcsException("Public and private ICS adapters must be different connections.");
        if (publicConfig == null)
            throw new IcsTargetNotFoundException(
                $"Public adapter not found: '{publicAdapterName}'");
        if (privateConfig == null)
            throw new IcsTargetNotFoundException($"Private adapter not found: '{privateAdapterName}'");

        string stage = "Disable previous sharing";
        try
        {
            foreach (dynamic cfg in enabledOthers) cfg.DisableSharing();
            if ((bool)publicConfig.SharingEnabled) publicConfig.DisableSharing();
            if ((bool)privateConfig.SharingEnabled) privateConfig.DisableSharing();
            stage = $"Enable public ICS connection {publicAdapterName}";
            publicConfig.EnableSharing(Public);
            stage = $"Enable private ICS connection {privateAdapterName}";
            privateConfig.EnableSharing(Private);
            stage = "Verify ICS configuration";
            if (!(bool)publicConfig.SharingEnabled || (int)publicConfig.SharingConnectionType != Public ||
                !(bool)privateConfig.SharingEnabled || (int)privateConfig.SharingConnectionType != Private)
                throw new IcsException("ICS readback did not match the requested connection pair.");
        }
        catch (Exception ex)
        {
            var recoveryErrors = new List<string>();
            foreach (dynamic cfg in new object[] { privateConfig, publicConfig })
            {
                try { cfg.DisableSharing(); } catch (Exception recovery) { recoveryErrors.Add(recovery.Message); }
            }
            foreach (var old in previous.OrderBy(p => p.Type))
            {
                try { ((dynamic)old.Config).EnableSharing(old.Type); }
                catch (Exception recovery) { recoveryErrors.Add(recovery.Message); }
            }
            throw new IcsException($"{stage}: {ex.Message} (0x{ex.HResult:X8}). " +
                (recoveryErrors.Count == 0 ? "Previous ICS configuration restored." :
                    "ICS recovery failed: " + string.Join("; ", recoveryErrors)), ex);
        }
    }

    public static List<IcsConnection> GetConnections()
    {
        dynamic manager = CreateNetShare();
        try
        {
            var result = new List<IcsConnection>();
            foreach (var connection in manager.EnumEveryConnection)
            {
                dynamic props = manager.NetConnectionProps[connection];
                dynamic config = manager.INetSharingConfigurationForINetConnection[connection];
                bool enabled = (bool)config.SharingEnabled;
                result.Add(new IcsConnection(Guid.Parse((string)props.Guid), (string)props.Name,
                    enabled, enabled ? (int)config.SharingConnectionType : -1));
            }
            return result;
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    private static dynamic CreateNetShare()
    {
        Type? comType = Type.GetTypeFromProgID(ProgId)
            ?? throw new IcsException($"COM ProgID not found: {ProgId}");
        return Activator.CreateInstance(comType)
            ?? throw new IcsException($"Failed to create COM object: {ProgId}");
    }
}

// ══════════════════════════════════════════════════════════════════
//  IcsException — ICS 专用异常
// ══════════════════════════════════════════════════════════════════
/// <summary>表示 ICS 配置或恢复步骤无法完成。</summary>
internal class IcsException : Exception
{
    /// <summary>创建 ICS 失败异常并保留可选原始异常。</summary>
    /// <param name="message">失败原因。</param>
    /// <param name="inner">造成失败的原始异常，可为 null。</param>
    public IcsException(string message, Exception? inner = null) : base(message, inner) { if (inner != null) HResult = inner.HResult; }
}

// 仅表示目标连接尚未找到。共享修改阶段的异常不能进入重试，否则可能重复修改系统共享。
internal sealed class IcsTargetNotFoundException : IcsException
{
    public IcsTargetNotFoundException(string message) : base(message) { }
}

/// <summary>宿主网络连接及其 ICS 共享角色快照。</summary>
/// <param name="Id">稳定网络连接 GUID。</param>
/// <param name="Name">连接显示名称，不作为匹配身份。</param>
/// <param name="Enabled">是否已启用 ICS。</param>
/// <param name="Type">ICS 角色：0 为公共上游，1 为私有下游；在 Enabled=true 时生效。</param>
internal sealed record IcsConnection(Guid Id, string Name, bool Enabled, int Type);
