using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExHyperV.Api;

// ══════════════════════════════════════════════════════════════════
//  HCS 数据模型
//  与 vmcompute.dll 通信的 JSON 结构
// ══════════════════════════════════════════════════════════════════

public sealed class HcsCpuGroupDetail
{
    [JsonPropertyName("GroupId")]
    public Guid GroupId { get; init; }

    [JsonPropertyName("Affinity")]
    public HcsCpuGroupAffinity? Affinity { get; init; }
}

public sealed class HcsCpuGroupAffinity
{
    [JsonPropertyName("LogicalProcessors")]
    public uint[]? LogicalProcessors { get; init; }
}

public sealed class HcsCpuGroupQueryResult
{
    [JsonPropertyName("Properties")]
    public HcsCpuGroupProperties[]? Properties { get; init; }
}

public sealed class HcsCpuGroupProperties
{
    [JsonPropertyName("CpuGroups")]
    public HcsCpuGroupDetail[]? CpuGroups { get; init; }
}

// ══════════════════════════════════════════════════════════════════
//  HcsApi — 公开封装层
//  所有 vmcompute.dll 调用的唯一入口
//  COM 初始化在底层统一处理，调用方不感知
// ══════════════════════════════════════════════════════════════════
public static class HcsApi
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── CPU Group 操作 ────────────────────────────────────────────

    /// <summary>
    /// 查询所有 CPU Group。
    /// </summary>
    public static Task<ApiResponse<List<HcsCpuGroupDetail>>> GetAllCpuGroupsAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                string json = HcsCore.Query(@"{""PropertyTypes"":[""CpuGroup""]}");
                if (string.IsNullOrEmpty(json))
                    return ApiResponse<List<HcsCpuGroupDetail>>.Ok(new List<HcsCpuGroupDetail>());

                var result = JsonSerializer.Deserialize<HcsCpuGroupQueryResult>(json, _jsonOptions);
                var groups = result?.Properties?.FirstOrDefault()?.CpuGroups?.ToList()
                          ?? new List<HcsCpuGroupDetail>();

                return ApiResponse<List<HcsCpuGroupDetail>>.Ok(groups);
            }
            catch (HcsException ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(
                    ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(
                    ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>
    /// 创建新的 CPU Group，绑定指定的逻辑处理器集合。
    /// </summary>
    public static Task<ApiResponse> CreateCpuGroupAsync(Guid groupId, uint[] processorIndexes)
    {
        return Task.Run(() =>
        {
            try
            {
                string processors = string.Join(",", processorIndexes);
                string json = $@"{{
                    ""PropertyType"": ""CpuGroup"",
                    ""Settings"": {{
                        ""Operation"": ""CreateGroup"",
                        ""OperationDetails"": {{
                            ""GroupId"": ""{groupId}"",
                            ""LogicalProcessorCount"": {processorIndexes.Length},
                            ""LogicalProcessors"": [{processors}]
                        }}
                    }}
                }}";

                HcsCore.Invoke(json);
                return ApiResponse.Ok();
            }
            catch (HcsException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>
    /// 删除指定的 CPU Group。
    /// </summary>
    public static Task<ApiResponse> DeleteCpuGroupAsync(Guid groupId)
    {
        return Task.Run(() =>
        {
            try
            {
                string json = $@"{{
                    ""PropertyType"": ""CpuGroup"",
                    ""Settings"": {{
                        ""Operation"": ""DeleteGroup"",
                        ""OperationDetails"": {{
                            ""GroupId"": ""{groupId}""
                        }}
                    }}
                }}";

                HcsCore.Invoke(json);
                return ApiResponse.Ok();
            }
            catch (HcsException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>
    /// 设置 CPU Group 的 CPU 占用上限（百分比 * 100，例如 5000 = 50%）。
    /// </summary>
    public static Task<ApiResponse> SetCpuGroupCapAsync(Guid groupId, ushort cpuCap)
    {
        return Task.Run(() =>
        {
            try
            {
                string json = $@"{{
                    ""PropertyType"": ""CpuGroup"",
                    ""Settings"": {{
                        ""Operation"": ""SetProperty"",
                        ""OperationDetails"": {{
                            ""GroupId"": ""{groupId}"",
                            ""PropertyCode"": 65536,
                            ""PropertyValue"": {cpuCap}
                        }}
                    }}
                }}";

                HcsCore.Invoke(json);
                return ApiResponse.Ok();
            }
            catch (HcsException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }
}

// ══════════════════════════════════════════════════════════════════
//  HcsCore — 内部底层通信层
//  统一管理 COM 初始化和 vmcompute.dll P/Invoke
//  HcsApi 以外的代码不应直接调用
// ══════════════════════════════════════════════════════════════════
internal static class HcsCore
{
    // COM 初始化常量
    // 使用 COINIT_MULTITHREADED(2) 而不是 COINIT_APARTMENTTHREADED(0)
    // 因为调用方是线程池 MTA 线程，两者必须一致
    private const uint COINIT_MULTITHREADED = 2;

    // ── vmcompute.dll P/Invoke 声明 ───────────────────────────────

    [DllImport("vmcompute.dll", SetLastError = true,
        CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HcsModifyServiceSettings(
        string settings, out nint result);

    [DllImport("vmcompute.dll", SetLastError = true,
        CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HcsGetServiceProperties(
        string propertyQuery, out nint properties, out nint result);

    // ── COM 初始化 P/Invoke ───────────────────────────────────────

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint ptr);

    // ── 核心通信方法 ──────────────────────────────────────────────

    /// <summary>
    /// 向 HCS 发送修改指令（CreateGroup、DeleteGroup、SetProperty 等）。
    /// 内部统一处理 COM 初始化，调用方不感知。
    /// </summary>
    public static void Invoke(string jsonPayload)
    {
        int comResult = CoInitializeEx(nint.Zero, COINIT_MULTITHREADED);
        // S_OK(0) 或 S_FALSE(1) 都表示 COM 已就绪
        bool comInitialized = comResult == 0 || comResult == 1;

        nint resultPtr = nint.Zero;
        try
        {
            int hResult = HcsModifyServiceSettings(jsonPayload, out resultPtr);
            if (hResult != 0)
            {
                string detail = resultPtr != nint.Zero
                    ? Marshal.PtrToStringUni(resultPtr) ?? string.Empty
                    : string.Empty;
                throw new HcsException(
                    $"HcsModifyServiceSettings failed. HRESULT: 0x{hResult:X}. Detail: {detail}",
                    hResult);
            }
        }
        finally
        {
            if (resultPtr != nint.Zero) CoTaskMemFree(resultPtr);
            if (comInitialized) CoUninitialize();
        }
    }

    /// <summary>
    /// 向 HCS 发送查询指令，返回 JSON 字符串。
    /// </summary>
    public static string Query(string jsonPayload)
    {
        int comResult = CoInitializeEx(nint.Zero, COINIT_MULTITHREADED);
        bool comInitialized = comResult == 0 || comResult == 1;

        nint propertiesPtr = nint.Zero;
        nint resultPtr = nint.Zero;
        try
        {
            int hResult = HcsGetServiceProperties(jsonPayload, out propertiesPtr, out resultPtr);
            if (hResult != 0)
            {
                string detail = resultPtr != nint.Zero
                    ? Marshal.PtrToStringUni(resultPtr) ?? string.Empty
                    : string.Empty;
                throw new HcsException(
                    $"HcsGetServiceProperties failed. HRESULT: 0x{hResult:X}. Detail: {detail}",
                    hResult);
            }

            return propertiesPtr != nint.Zero
                ? Marshal.PtrToStringUni(propertiesPtr) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            if (propertiesPtr != nint.Zero) CoTaskMemFree(propertiesPtr);
            if (resultPtr != nint.Zero) CoTaskMemFree(resultPtr);
            if (comInitialized) CoUninitialize();
        }
    }
}

// ══════════════════════════════════════════════════════════════════
//  HcsException — HCS 专用异常类型
//  携带 HRESULT，方便 ApiResponse 填充错误码
// ══════════════════════════════════════════════════════════════════
public sealed class HcsException : Exception
{
    public new int HResult { get; }

    public HcsException(string message, int hResult) : base(message)
    {
        HResult = hResult;
    }
}