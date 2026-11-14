using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExHyperV.Tools;

// ══════════════════════════════════════════════════════════════════
//  HCS 数据模型
//  与 ComputeCore.dll 通信的 JSON 结构
// ══════════════════════════════════════════════════════════════════

/// <summary>HCS 返回的一组 CPU 分组身份与亲和性配置。</summary>
internal sealed class HcsCpuGroupDetail
{
    /// <summary>CPU 分组 GUID。</summary>
    [JsonPropertyName("GroupId")]
    [JsonRequired]
    public Guid GroupId { get; init; }

    /// <summary>分组处理器亲和性；原生结果未提供时可为 null。</summary>
    [JsonPropertyName("Affinity")]
    public HcsCpuGroupAffinity? Affinity { get; init; }

    /// <summary>HCS 返回的分组配额、优先级等原始策略；缺失时不推断默认策略。</summary>
    [JsonPropertyName("GroupProperties")]
    public HcsCpuGroupProperty[]? GroupProperties { get; init; }

    /// <summary>Hyper-V 内部 CPU 分组的唯一标识。</summary>
    [JsonPropertyName("HypervisorGroupId")]
    public ulong? HypervisorGroupId { get; init; }

    /// <summary>分组总 CPU 配额原始值，0 至 65536；未报告该属性时为 null。</summary>
    [JsonIgnore]
    public uint? CpuCap
    {
        get
        {
            ulong? value = GroupProperties?.FirstOrDefault(p => p.PropertyCode == 0x00010000)?.PropertyValue;
            return value is <= 65536 ? (uint)value.Value : null;
        }
    }
}

/// <summary>HCS CPU 分组的原始策略属性。</summary>
internal sealed class HcsCpuGroupProperty
{
    /// <summary>HCS 属性代码；0x00010000 表示总 CPU 配额。</summary>
    [JsonPropertyName("PropertyCode")]
    [JsonRequired]
    public uint PropertyCode { get; init; }

    /// <summary>属性原始值；解释取决于 PropertyCode。</summary>
    [JsonPropertyName("PropertyValue")]
    [JsonRequired]
    public ulong PropertyValue { get; init; }
}

/// <summary>HCS CPU 分组对应的宿主逻辑处理器索引集合。</summary>
internal sealed class HcsCpuGroupAffinity
{
    /// <summary>原生返回的逻辑处理器数量；提供时必须与列表长度相等。</summary>
    [JsonPropertyName("LogicalProcessorCount")]
    public uint? LogicalProcessorCount { get; init; }

    /// <summary>宿主逻辑处理器索引；缺失字段时为 null。</summary>
    [JsonPropertyName("LogicalProcessors")]
    public uint[]? LogicalProcessors { get; init; }
}

/// <summary>HCS 返回的宿主逻辑处理器身份与拓扑，不使用 Windows 处理器组内编号。</summary>
internal sealed class HcsLogicalProcessor
{
    /// <summary>Hyper-V 宿主逻辑处理器索引，CPU 分组使用此值。</summary>
    [JsonPropertyName("LpIndex")]
    [JsonRequired]
    public uint LpIndex { get; init; }

    /// <summary>HCS 返回的 NUMA 节点编号。</summary>
    [JsonPropertyName("NodeNumber")]
    [JsonRequired]
    public byte NodeNumber { get; init; }

    /// <summary>物理处理器封装标识。</summary>
    [JsonPropertyName("PackageId")]
    [JsonRequired]
    public uint PackageId { get; init; }

    /// <summary>HCS 返回的物理核心标识。</summary>
    [JsonPropertyName("CoreId")]
    [JsonRequired]
    public uint CoreId { get; init; }

    /// <summary>对应的根分区虚拟处理器索引；-1 表示不提供给根分区。</summary>
    [JsonPropertyName("RootVpIndex")]
    [JsonRequired]
    public int RootVpIndex { get; init; }

    /// <summary>较新 HCS 返回的子节点拓扑，保留原始结构。</summary>
    [JsonPropertyName("Subnodes")]
    public JsonElement[]? Subnodes { get; init; }
}

// ══════════════════════════════════════════════════════════════════
//  HcsApi — 内部封装层
//  CPU 分组与拓扑查询的唯一入口
//  COM 初始化在底层统一处理，调用方不感知
// ══════════════════════════════════════════════════════════════════
/// <summary>通过宿主计算服务查询和修改 CPU 分组，供 CPU 亲和性等高级管理使用。</summary>
/// <remarks>需要系统 HCS 支持及相应管理权限；返回响应包含原生失败，不提供取消入口。</remarks>
internal static class HcsApi
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── CPU Group 操作 ────────────────────────────────────────────

    /// <summary>读取宿主计算服务返回的 CPU 分组。</summary>
    /// <returns>CPU 分组列表响应；没有分组时成功返回空列表。</returns>
    public static Task<ApiResponse<List<HcsCpuGroupDetail>>> GetAllCpuGroupsAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                string json = HcsCore.Query(@"{""PropertyTypes"":[""CpuGroup""]}");
                return ApiResponse<List<HcsCpuGroupDetail>>.Ok(ParseCpuGroups(json));
            }
            catch (HcsResultCleanupException ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(ex.Message, ex.NativeHResult ?? -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OperationFailed);
            }
            catch (HcsException ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(
                    ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (COMException ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(
                    ex.Message, ex.HResult, ApiErrorSource.Com, ex);
            }
            catch (JsonException ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(
                    ex.Message, -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OperationFailed);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail(
                    ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>读取 HCS 宿主处理器拓扑，保留 Hyper-V LP 身份、插槽和 NUMA 信息。</summary>
    internal static Task<ApiResponse<List<HcsLogicalProcessor>>> GetProcessorTopologyAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                string json = HcsCore.Query(@"{""PropertyTypes"":[""ProcessorTopology""]}");
                return ApiResponse<List<HcsLogicalProcessor>>.Ok(ParseProcessorTopology(json));
            }
            catch (HcsResultCleanupException ex)
            {
                return ApiResponse<List<HcsLogicalProcessor>>.Fail(ex.Message, ex.NativeHResult ?? -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OperationFailed);
            }
            catch (HcsException ex)
            {
                return ApiResponse<List<HcsLogicalProcessor>>.Fail(
                    ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (COMException ex)
            {
                return ApiResponse<List<HcsLogicalProcessor>>.Fail(
                    ex.Message, ex.HResult, ApiErrorSource.Com, ex);
            }
            catch (JsonException ex)
            {
                return ApiResponse<List<HcsLogicalProcessor>>.Fail(
                    ex.Message, -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OperationFailed);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<HcsLogicalProcessor>>.Fail(
                    ex.Message, -1, ApiErrorSource.None, ex);
            }
        });
    }

    /// <summary>创建具有指定逻辑处理器集合的宿主 CPU 分组。</summary>
    /// <returns>HCS 创建响应。</returns>
    /// <param name="groupId">新分组的 GUID。</param>
    /// <param name="processorIndexes">宿主逻辑处理器索引。</param>
    public static Task<ApiResponse> CreateCpuGroupAsync(Guid groupId, uint[] processorIndexes)
    {
        if (groupId == Guid.Empty || processorIndexes is null || processorIndexes.Length == 0)
            return Task.FromResult(ApiResponse.Fail(
                "创建 CPU 分组需要非空的分组标识和逻辑处理器列表。", kind: ApiErrorKind.InvalidArgument));

        // 在排入线程池前固定调用方输入，避免调用期间数组被修改。
        uint[] indexes = processorIndexes.Distinct().Order().ToArray();
        return Task.Run(() =>
        {
            try
            {
                string processors = string.Join(",", indexes);
                string json = $@"{{
                    ""PropertyType"": ""CpuGroup"",
                    ""Settings"": {{
                        ""Operation"": ""CreateGroup"",
                        ""OperationDetails"": {{
                            ""GroupId"": ""{groupId}"",
                            ""LogicalProcessorCount"": {indexes.Length},
                            ""LogicalProcessors"": [{processors}]
                        }}
                    }}
                }}";

                HcsCore.Invoke(json);
                return ApiResponse.Ok();
            }
            catch (HcsResultCleanupException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.NativeHResult ?? -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OutcomeUnknown);
            }
            catch (HcsException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (COMException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Com, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex, ApiErrorKind.OutcomeUnknown);
            }
        });
    }

    /// <summary>删除指定的宿主 HCS CPU 分组。</summary>
    /// <returns>HCS 删除响应。</returns>
    /// <param name="groupId">待删除分组 GUID。</param>
    public static Task<ApiResponse> DeleteCpuGroupAsync(Guid groupId)
    {
        if (groupId == Guid.Empty)
            return Task.FromResult(ApiResponse.Fail("CPU 分组标识不能为空。", kind: ApiErrorKind.InvalidArgument));

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
            catch (HcsResultCleanupException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.NativeHResult ?? -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OutcomeUnknown);
            }
            catch (HcsException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (COMException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Com, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex, ApiErrorKind.OutcomeUnknown);
            }
        });
    }

    /// <summary>将 CPU 分组配额原始值提交给 HCS。</summary>
    /// <returns>HCS 配置响应。</returns>
    /// <param name="groupId">待修改分组 GUID。</param>
    /// <param name="cpuCap">HCS CpuCap 原始值，范围 0 至 65536；65536 表示满额。</param>
    public static Task<ApiResponse> SetCpuGroupCapAsync(Guid groupId, uint cpuCap)
        => SetCpuGroupPropertyAsync(groupId, 0x00010000, cpuCap, cpuCap <= 65536);

    /// <summary>提交 CPU 组调度优先级，原始值范围为 0 至 7。</summary>
    internal static Task<ApiResponse> SetCpuGroupPriorityAsync(Guid groupId, uint priority)
        => SetCpuGroupPropertyAsync(groupId, 0x00020000, priority, priority <= 7);

    /// <summary>提交 CPU 组保留的空闲逻辑处理器数量，拓扑约束由上层及 HCS 校验。</summary>
    internal static Task<ApiResponse> SetCpuGroupIdleProcessorReserveAsync(Guid groupId, uint count)
        => SetCpuGroupPropertyAsync(groupId, 0x00030000, count, count <= 255);

    /// <summary>设置 CPU 组的相对调度权重。</summary>
    internal static Task<ApiResponse> SetCpuGroupRelativeWeightAsync(Guid groupId, uint weight)
        => SetCpuGroupPropertyAsync(groupId, 0x00040000, weight, weight is >= 1 and <= 65535);
    private static Task<ApiResponse> SetCpuGroupPropertyAsync(Guid groupId, uint propertyCode, uint value, bool valid)
    {
        if (groupId == Guid.Empty || !valid)
            return Task.FromResult(ApiResponse.Fail("CPU 分组标识或属性值无效。", kind: ApiErrorKind.InvalidArgument));
        return Task.Run(() =>
        {
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    PropertyType = "CpuGroup",
                    Settings = new
                    {
                        Operation = "SetProperty",
                        OperationDetails = new { GroupId = groupId, PropertyCode = propertyCode, PropertyValue = value }
                    }
                });
                HcsCore.Invoke(json);
                return ApiResponse.Ok();
            }
            catch (HcsResultCleanupException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.NativeHResult ?? -1, ApiErrorSource.Hcs, ex, ApiErrorKind.OutcomeUnknown);
            }
            catch (HcsException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Hcs, ex);
            }
            catch (COMException ex)
            {
                return ApiResponse.Fail(ex.Message, ex.HResult, ApiErrorSource.Com, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, -1, ApiErrorSource.None, ex, ApiErrorKind.OutcomeUnknown);
            }
        });
    }
    private static List<HcsCpuGroupDetail> ParseCpuGroups(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = GetSingleProperty(document.RootElement);
        if (!property.TryGetProperty("CpuGroups", out var groupsElement))
        {
            // HCS 在没有 CPU 分组时实际返回 Properties:[{}]。
            if (!property.EnumerateObject().Any()) return new();
            throw new JsonException("HCS CPU 分组响应缺少 CpuGroups。");
        }
        if (groupsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("HCS CpuGroups 必须是数组。");

        var groups = groupsElement.Deserialize<List<HcsCpuGroupDetail>>(_jsonOptions)!;
        var ids = new HashSet<Guid>();
        foreach (var group in groups)
        {
            if (group is null || group.GroupId == Guid.Empty || !ids.Add(group.GroupId))
                throw new JsonException("HCS CPU 分组身份缺失或重复。");
            if (group.Affinity is { } affinity)
            {
                if (affinity.LogicalProcessors is null
                    || affinity.LogicalProcessors.Distinct().Count() != affinity.LogicalProcessors.Length
                    || affinity.LogicalProcessorCount is uint count && count != affinity.LogicalProcessors.Length)
                    throw new JsonException("HCS CPU 分组的处理器列表或数量无效。");
            }
            var codes = new HashSet<uint>();
            foreach (var policy in group.GroupProperties ?? [])
            {
                if (policy is null || !codes.Add(policy.PropertyCode)
                    || policy.PropertyCode == 0x00010000 && policy.PropertyValue > 65536)
                    throw new JsonException("HCS CPU 分组策略缺失、重复或超出有效范围。");
            }
        }
        return groups;
    }

    private static List<HcsLogicalProcessor> ParseProcessorTopology(string json)
    {
        using var document = JsonDocument.Parse(json);
        var property = GetSingleProperty(document.RootElement);
        if (!property.TryGetProperty("LogicalProcessorCount", out var countElement)
            || countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetUInt32(out uint count)
            || !property.TryGetProperty("LogicalProcessors", out var processorsElement)
            || processorsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("HCS 处理器拓扑缺少有效的数量或处理器列表。");

        var processors = processorsElement.Deserialize<List<HcsLogicalProcessor>>(_jsonOptions)!;
        var ids = new HashSet<uint>();
        if (count != processors.Count)
            throw new JsonException("HCS 处理器拓扑数量与列表不一致。");
        foreach (var processor in processors)
        {
            if (processor is null || !ids.Add(processor.LpIndex) || processor.RootVpIndex < -1)
                throw new JsonException("HCS 处理器身份重复、缺失或根分区索引无效。");
        }
        return processors;
    }

    private static JsonElement GetSingleProperty(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Properties", out var properties)
            || properties.ValueKind != JsonValueKind.Array || properties.GetArrayLength() != 1
            || properties[0].ValueKind != JsonValueKind.Object)
            throw new JsonException("HCS 单项属性查询必须返回一个有效的 Properties 对象。");
        return properties[0];
    }
}

// ══════════════════════════════════════════════════════════════════
//  HcsCore — 内部底层通信层
//  统一管理 COM 初始化和 ComputeCore.dll P/Invoke
//  HcsApi 以外的代码不应直接调用
// ══════════════════════════════════════════════════════════════════
internal static class HcsCore
{
    // HcsApi 的 Task.Run 在线程池 MTA 中调用；S_OK/S_FALSE 都须配对清理。
    private const uint COINIT_MULTITHREADED = 0;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("computecore.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int HcsModifyServiceSettings(string settings, out nint result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("computecore.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int HcsGetServiceProperties(string propertyQuery, out nint result);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint LocalFree(nint memory);

    /// <summary>提交 HCS 修改请求；原生操作失败和结果内存清理失败分别保留。</summary>
    public static void Invoke(string jsonPayload) => Call(jsonPayload, query: false);

    /// <summary>查询 HCS 属性；公开接口的单个结果缓冲区承载属性或错误文档。</summary>
    public static string Query(string jsonPayload) => Call(jsonPayload, query: true);

    private static string Call(string jsonPayload, bool query)
    {
        int initialized = CoInitializeEx(nint.Zero, COINIT_MULTITHREADED);
        if (initialized < 0)
            throw new COMException("初始化 HCS 所需的 COM 环境失败。", initialized);

        nint result = nint.Zero;
        int? nativeHResult = null;
        string text = string.Empty;
        Exception? operationError = null;
        Exception? cleanupError = null;
        try
        {
            nativeHResult = query
                ? HcsGetServiceProperties(jsonPayload, out result)
                : HcsModifyServiceSettings(jsonPayload, out result);
            text = result == nint.Zero ? string.Empty : Marshal.PtrToStringUni(result) ?? string.Empty;
        }
        catch (Exception error) { operationError = error; }
        finally
        {
            try
            {
                if (result != nint.Zero && LocalFree(result) != nint.Zero)
                    cleanupError = new Win32Exception(Marshal.GetLastPInvokeError(), "HCS 返回的结果内存未能释放。");
            }
            catch (Exception error) { cleanupError = error; }
            // 结果内存清理失败也必须配对本次成功的 COM 初始化。
            try { CoUninitialize(); }
            catch (Exception error) { cleanupError = Combine(cleanupError, error); }
        }

        string method = query ? "HcsGetServiceProperties" : "HcsModifyServiceSettings";
        if (nativeHResult is < 0)
        {
            string message = $"{method} failed. HRESULT: 0x{nativeHResult.Value:X8}. Detail: {text}";
            if (operationError is not null) message += $" 读取结果诊断失败：{operationError.Message}";
            if (cleanupError is not null) message += $" 清理失败：{cleanupError.Message}";
            throw new HcsException(message, nativeHResult.Value, Combine(operationError, cleanupError));
        }
        if (cleanupError is not null)
        {
            string outcome = nativeHResult is >= 0
                ? $"系统请求已返回成功（HRESULT 0x{nativeHResult.Value:X8}）"
                : "系统请求是否完成尚未确认";
            throw new HcsResultCleanupException($"{method}：{outcome}，但结果清理失败；不得直接重放或反向操作。{cleanupError.Message}",
                nativeHResult, Combine(operationError, cleanupError)!);
        }
        if (operationError is not null) ExceptionDispatchInfo.Capture(operationError).Throw();
        return text;
    }

    private static Exception? Combine(Exception? first, Exception? second)
        => first is null ? second : second is null ? first : new AggregateException(first, second);
}

/// <summary>保留原生提交结果的清理异常；调用方须停止后续写入和自动回滚。</summary>
internal sealed class HcsResultCleanupException : Exception
{
    internal int? NativeHResult { get; }
    internal HcsResultCleanupException(string message, int? nativeHResult, Exception inner) : base(message, inner)
    {
        NativeHResult = nativeHResult;
        if (nativeHResult.HasValue) HResult = nativeHResult.Value;
    }
}

// HCS 原生失败原样保留 HRESULT；诊断读取或清理失败不会覆盖此码。
internal sealed class HcsException : Exception
{
    public HcsException(string message, int hResult, Exception? inner = null) : base(message, inner)
    {
        HResult = hResult;
    }
}
