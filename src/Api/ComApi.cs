using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ExHyperV.Api;

// ══════════════════════════════════════════════════════════════════
//  ComApi — 公开封装层
//  所有 COM 对象调用的唯一入口
//  ICS 操作必须在 STA 线程执行，ComApi 内部统一处理，调用方不感知
// ══════════════════════════════════════════════════════════════════
public static class ComApi
{
    // ── ICS 网络共享 ──────────────────────────────────────────────
    // 替换 NetworkService / Utils.UpdateSwitchConfigurationAsync 里
    // 通过 PowerShell 调用 HNetCfg.HNetShare 的所有操作

    /// <summary>
    /// 禁用所有网络适配器上的 ICS 共享。
    /// 等效于 PS 脚本里遍历所有连接并调用 DisableSharing()。
    /// </summary>
    public static ApiResponse DisableAllIcsSharing()
    {
        try
        {
            RunOnSta(() => IcsCore.DisableAll());
            return ApiResponse.Ok();
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.Com, ex);
        }
    }

    public static Task<ApiResponse> DisableAllIcsSharingAsync()
        => Task.Run(DisableAllIcsSharing);

    /// <summary>
    /// 为两个适配器启用 ICS（公共上游 + 私有下游）。
    /// publicAdapterName：共享上游的适配器显示名称（如物理网卡名）
    /// privateAdapterName：受保护网络一侧的适配器显示名称（如 vEthernet 名）
    /// 调用前应先调用 DisableAllIcsSharing() 清除旧配置。
    /// </summary>
    public static ApiResponse EnableIcsSharing(string publicAdapterName, string privateAdapterName)
    {
        if (string.IsNullOrWhiteSpace(publicAdapterName))
            return ApiResponse.Fail("publicAdapterName cannot be empty");
        if (string.IsNullOrWhiteSpace(privateAdapterName))
            return ApiResponse.Fail("privateAdapterName cannot be empty");

        try
        {
            RunOnSta(() => IcsCore.Enable(publicAdapterName, privateAdapterName));
            return ApiResponse.Ok();
        }
        catch (IcsException ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.Com, ex);
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.Com, ex);
        }
    }

    public static Task<ApiResponse> EnableIcsSharingAsync(
        string publicAdapterName, string privateAdapterName)
        => Task.Run(() => EnableIcsSharing(publicAdapterName, privateAdapterName));

    /// <summary>
    /// 查询指定交换机是否配置了 ICS，并返回上游物理适配器的名称。
    /// 返回 null 表示该交换机未配置 ICS。
    /// </summary>
    public static ApiResponse<string?> GetIcsSourceAdapter(string switchName)
    {
        if (string.IsNullOrWhiteSpace(switchName))
            return ApiResponse<string?>.Fail("switchName cannot be empty");

        try
        {
            string? result = null;
            RunOnSta(() => result = IcsCore.GetSourceAdapter(switchName));
            return ApiResponse<string?>.Ok(result);
        }
        catch (Exception ex)
        {
            return ApiResponse<string?>.Fail(ex.Message, -1, ApiErrorSource.Com, ex);
        }
    }

    public static Task<ApiResponse<string?>> GetIcsSourceAdapterAsync(string switchName)
        => Task.Run(() => GetIcsSourceAdapter(switchName));

    // ── STA 线程执行 ──────────────────────────────────────────────
    // COM 对象要求在 STA（单线程单元）线程中调用
    // 所有 ICS 操作必须经由此方法执行

    /// <summary>在 STA 线程中同步执行操作，等待完成后返回。</summary>
    public static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (exception != null) ExceptionDispatchInfo.Capture(exception).Throw();
    }

    /// <summary>在 STA 线程中执行并返回结果。</summary>
    public static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { exception = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (exception != null) ExceptionDispatchInfo.Capture(exception).Throw();
        return result;
    }
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

    /// <summary>禁用所有连接上的 ICS 共享。</summary>
    public static void DisableAll()
    {
        dynamic netShare = CreateNetShare();
        try
        {
            foreach (var conn in netShare.EnumEveryConnection)
            {
                try
                {
                    dynamic cfg = netShare.INetSharingConfigurationForINetConnection[conn];
                    if ((bool)cfg.SharingEnabled)
                        cfg.DisableSharing();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[IcsCore.DisableAll] connection error: {ex.Message}");
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(netShare);
        }
    }

    /// <summary>
    /// 为指定的公共和私有适配器启用 ICS。
    /// 调用前应已通过 DisableAll() 清除旧配置。
    /// </summary>
    public static void Enable(string publicAdapterName, string privateAdapterName)
    {
        dynamic netShare = CreateNetShare();
        try
        {
            dynamic? publicConfig = null;
            dynamic? privateConfig = null;

            foreach (var conn in netShare.EnumEveryConnection)
            {
                try
                {
                    dynamic props = netShare.NetConnectionProps[conn];
                    dynamic cfg = netShare.INetSharingConfigurationForINetConnection[conn];

                    // 先清除所有已有 ICS（防止重复配置）
                    if ((bool)cfg.SharingEnabled)
                        cfg.DisableSharing();

                    string name = (string)props.Name;
                    if (name == publicAdapterName) publicConfig = cfg;
                    if (name == privateAdapterName) privateConfig = cfg;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[IcsCore.Enable] enum error: {ex.Message}");
                }
            }

            if (publicConfig == null)
                throw new IcsException($"Public adapter not found: '{publicAdapterName}'");
            if (privateConfig == null)
                throw new IcsException($"Private adapter not found: '{privateAdapterName}'");

            publicConfig.EnableSharing(Public);
            privateConfig.EnableSharing(Private);
        }
        finally
        {
            Marshal.FinalReleaseComObject(netShare);
        }
    }

    /// <summary>
    /// 查询指定交换机的 ICS 上游物理适配器名称。
    /// 返回 null 表示未配置 ICS。
    /// </summary>
    public static string? GetSourceAdapter(string switchName)
    {
        // vEthernet 适配器的显示名格式固定为 "vEthernet (交换机名)"
        string vEthernetName = $"vEthernet ({switchName})";

        dynamic netShare = CreateNetShare();
        try
        {
            string? sourceAdapterName = null;
            bool gatewayIsCorrect = false;

            foreach (var conn in netShare.EnumEveryConnection)
            {
                try
                {
                    dynamic props = netShare.NetConnectionProps[conn];
                    dynamic cfg = netShare.INetSharingConfigurationForINetConnection[conn];

                    if (!(bool)cfg.SharingEnabled) continue;

                    string name = (string)props.Name;
                    int sharingType = (int)cfg.SharingConnectionType;

                    // SharingConnectionType 1 = PUBLIC（网关侧）
                    if (sharingType == Public + 1 && name == vEthernetName)
                        gatewayIsCorrect = true;

                    // SharingConnectionType 0 = PRIVATE → 但这里用 ICS 的约定反过来
                    // 实际上 EnableSharing(0)=PUBLIC，EnableSharing(1)=PRIVATE
                    // NetConnectionProps 查到的是上游（物理网卡）的名字
                    if (sharingType == Public && name != vEthernetName)
                        sourceAdapterName = name;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[IcsCore.GetSourceAdapter] enum error: {ex.Message}");
                }
            }

            return gatewayIsCorrect && sourceAdapterName != null
                ? sourceAdapterName
                : null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(netShare);
        }
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
public sealed class IcsException : Exception
{
    public IcsException(string message) : base(message) { }
}