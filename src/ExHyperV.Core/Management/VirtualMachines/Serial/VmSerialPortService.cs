using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>查询和修改虚拟机串口（COM 1 / COM 2）的命名管道连接与内核调试模式。</summary>
/// <example>
/// <para>启用虚拟机的 COM 1 命名管道连接并开启内核调试模式：</para>
/// <code>
/// using System;
/// using System.Threading.Tasks;
/// using ExHyperV.Services;
/// using ExHyperV.Tools;
///
/// public static class SerialPortExample
/// {
///     public static async Task&lt;ApiResponse&gt; EnableKernelDebugPipeAsync(Guid vmId)
///     {
///         var pipeResult = await VmSerialPortService.SetPipePathAsync(vmId, 1, "win11-debug");
///         if (!pipeResult.Success) return pipeResult;
///         return await VmSerialPortService.SetDebuggerModeAsync(vmId, 1, true);
///     }
/// }
/// </code>
/// </example>
public static class VmSerialPortService
{
    private const string VsmsWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

    /// <summary>评估当前虚拟机电源状态下是否允许修改串口配置。</summary>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>虚拟机完全关机时允许修改；否则返回需要关机的原因。</returns>
    public static VmModificationCondition GetModificationCondition(VmState state)
        => VmModificationCondition.PoweredOff(state);

    /// <summary>规范化命名管道路径；仅输入短名称时自动补全为 \\.\pipe\名称，留空返回空字符串。</summary>
    /// <param name="input">用户输入的管道短名称或完整 UNC 管道路径。</param>
    /// <returns>规范化后的命名管道路径，空输入返回空字符串。</returns>
    public static string NormalizePipePath(string? input)
    {
        string trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return string.Empty;
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)) return trimmed;
        return $@"\\.\pipe\{trimmed.TrimStart('\\')}";
    }

    /// <summary>校验命名管道路径格式；为空表示断开连接，视为有效。</summary>
    /// <param name="pipePath">待校验的短管道名、完整管道路径或空字符串。</param>
    /// <returns>路径无效时返回错误说明；有效或留空断开时返回 null。</returns>
    public static string? GetPipePathError(string? pipePath)
    {
        string normalized = NormalizePipePath(pipePath);
        if (string.IsNullOrEmpty(normalized)) return null;
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return @"命名管道路径必须以 \\.\pipe\ 或 \\服务器\pipe\ 开头。";

        int pipeMarker = normalized.IndexOf(@"\pipe\", 2, StringComparison.OrdinalIgnoreCase);
        if (pipeMarker <= 2 || pipeMarker + 6 >= normalized.Length)
            return @"命名管道路径格式无效，示例：\\.\pipe\my-vm-com1。";

        string server = normalized[2..pipeMarker];
        if (string.IsNullOrWhiteSpace(server) || server.Contains('\\') || server.Contains('/') || server.Any(char.IsWhiteSpace))
            return @"命名管道服务器名格式无效，本地请使用 \\.\pipe\名称。";

        string pipeName = normalized[(pipeMarker + 6)..];
        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName is "." or ".."
            || pipeName.StartsWith('\\')
            || pipeName.EndsWith('\\')
            || pipeName.Contains(@"\\", StringComparison.Ordinal)
            || pipeName.Any(static c => c < '\x20' || c is ':' or '/' or '*' or '?' or '"' or '<' or '>' or '|'))
            return @"命名管道名称包含无效字符或格式有误，示例：\\.\pipe\my-vm-com1。";

        return null;
    }

    /// <summary>读取指定虚拟机的全部虚拟串口配置（COM 1 与 COM 2）。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>按端口号升序排列的串口配置列表。</returns>
    public static async Task<ApiResponse<List<VmSerialPort>>> GetSerialPortsAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<List<VmSerialPort>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);

        var vssdResponse = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
            obj => WmiApi.PropStr(obj, "InstanceID"));

        if (!vssdResponse.Success)
            return ApiResponse<List<VmSerialPort>>.Fail(
                vssdResponse.Error, vssdResponse.Code, vssdResponse.ErrorSource, vssdResponse.Exception, vssdResponse.ErrorKind);
        if (!vssdResponse.HasData)
            return ApiResponse<List<VmSerialPort>>.Fail("找不到虚拟机配置。", kind: ApiErrorKind.NotFound);

        string escapedSettingId = WmiApi.Escape(vssdResponse.Data!);
        var portsResponse = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_SerialPortSettingData WHERE InstanceID LIKE '{escapedSettingId}\\\\%'",
            MapSerialPort);

        if (!portsResponse.Success)
            return ApiResponse<List<VmSerialPort>>.Fail(
                portsResponse.Error, portsResponse.Code, portsResponse.ErrorSource, portsResponse.Exception, portsResponse.ErrorKind);

        var ordered = (portsResponse.Data ?? [])
            .OrderBy(p => p.PortNumber)
            .ThenBy(p => p.WmiInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ApiResponse<List<VmSerialPort>>.Ok(ordered);
    }

    /// <summary>开启、更新或断开指定串口的宿主机命名管道连接；不改变内核调试模式。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="portNumber">串口编号：1 表示 COM 1，2 表示 COM 2。</param>
    /// <param name="pipePath">短管道名或完整管道路径；传入空字符串或 null 表示断开命名管道连接。</param>
    /// <returns>结构化修改结果。</returns>
    public static Task<ApiResponse> SetPipePathAsync(Guid vmId, int portNumber, string? pipePath)
    {
        if (vmId == Guid.Empty)
            return Task.FromResult(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
        if (portNumber is not (1 or 2))
            return Task.FromResult(ApiResponse.Fail("串口编号必须为 1（COM 1）或 2（COM 2）。", kind: ApiErrorKind.InvalidArgument));

        string normalizedPipe = NormalizePipePath(pipePath);
        if (GetPipePathError(normalizedPipe) is { } pipeError)
            return Task.FromResult(ApiResponse.Fail(pipeError, kind: ApiErrorKind.InvalidArgument));

        return ModifyPortAsync(vmId, portNumber, normalizedPipe, updatePipe: true, debuggerMode: null);
    }

    /// <summary>启用或关闭指定串口的 Windows 内核调试模式；不改变当前命名管道连接。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="portNumber">串口编号：1 表示 COM 1，2 表示 COM 2。</param>
    /// <param name="enabled">true 启用内核调试模式优化；false 关闭。</param>
    /// <returns>结构化修改结果。</returns>
    public static Task<ApiResponse> SetDebuggerModeAsync(Guid vmId, int portNumber, bool enabled)
    {
        if (vmId == Guid.Empty)
            return Task.FromResult(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
        if (portNumber is not (1 or 2))
            return Task.FromResult(ApiResponse.Fail("串口编号必须为 1（COM 1）或 2（COM 2）。", kind: ApiErrorKind.InvalidArgument));

        return ModifyPortAsync(vmId, portNumber, normalizedPipe: null, updatePipe: false, debuggerMode: enabled);
    }

    /// <summary>在同一次提交中应用串口路径与调试模式。调试模式为空时保留原值。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="portNumber">串口号，取 1 或 2。</param>
    /// <param name="pipePath">命名管道路径或短名称，空值或空白表示断开。</param>
    /// <param name="debuggerMode">请求的内核调试开关，null 表示保留当前值。</param>
    /// <returns>按当前串口配置提交两个字段的结果。</returns>
    public static Task<ApiResponse> SetPortSettingsAsync(Guid vmId, int portNumber, string? pipePath, bool? debuggerMode)
    {
        if (vmId == Guid.Empty || portNumber is not (1 or 2))
            return Task.FromResult(ApiResponse.Fail("请指定虚拟机和有效串口。", kind: ApiErrorKind.InvalidArgument));
        if (GetPipePathError(pipePath) is { } error)
            return Task.FromResult(ApiResponse.Fail(error, kind: ApiErrorKind.InvalidArgument));
        if (debuggerMode == true && string.IsNullOrWhiteSpace(pipePath))
            return Task.FromResult(ApiResponse.Fail("开启内核调试模式需要先指定命名管道。", kind: ApiErrorKind.InvalidArgument));
        return ModifyPortAsync(vmId, portNumber, NormalizePipePath(pipePath), true, debuggerMode);
    }

    private static Task<ApiResponse> ModifyPortAsync(
        Guid vmId, int portNumber, string? normalizedPipe, bool updatePipe, bool? debuggerMode) =>
        VmSettingsWriteLock.RunAsync(vmId, async () =>
        {
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData)
                return state.Success
                    ? ApiResponse.Fail("未找到虚拟机。", kind: ApiErrorKind.NotFound)
                    : ApiResponse.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);

            var condition = GetModificationCondition(state.Data!.State);
            if (!condition.CanModify) return condition.ToResponse();

            var portsResponse = await GetSerialPortsAsync(vmId);
            if (!portsResponse.HasData)
                return ApiResponse.Fail(portsResponse.Error, portsResponse.Code, portsResponse.ErrorSource,
                    portsResponse.Exception, portsResponse.ErrorKind);

            var target = portsResponse.Data!.FirstOrDefault(p => p.PortNumber == portNumber);
            if (target == null)
                return ApiResponse.Fail($"未找到串口 COM {portNumber}。", kind: ApiErrorKind.NotFound);

            if (debuggerMode.HasValue && !target.DebuggerModeAvailable)
                return ApiResponse.Fail("当前宿主不支持修改串口 DebuggerMode。", kind: ApiErrorKind.Unsupported);

            string escapedId = WmiApi.Escape(target.WmiInstanceId);
            return await WmiApi.WithObjectAsync(
                $"SELECT * FROM Msvm_SerialPortSettingData WHERE InstanceID = '{escapedId}'",
                obj =>
                {
                    if (updatePipe)
                    {
                        obj["Connection"] = BuildConnectionValues(normalizedPipe);
                    }
                    if (debuggerMode.HasValue)
                        obj["DebuggerMode"] = debuggerMode.Value;
                },
                submitMethod: "ModifyResourceSettings",
                submitParamName: "ResourceSettings",
                wrapInArray: true,
                serviceWql: VsmsWql);
        });

    private static VmSerialPort MapSerialPort(ManagementObject obj)
    {
        string instanceId = WmiApi.PropStr(obj, "InstanceID");
        string elementName = WmiApi.PropStr(obj, "ElementName");
        int portNumber = ParsePortNumber(instanceId, elementName);
        return new VmSerialPort
        {
            PortNumber = portNumber,
            Name = ResolvePortName(elementName, portNumber),
            PipePath = ExtractPipePath(obj["Connection"]),
            DebuggerModeAvailable = obj.HasProperty("DebuggerMode"),
            DebuggerMode = obj.TryGet<bool>("DebuggerMode") ?? false,
            WmiInstanceId = instanceId,
        };
    }

    /// <summary>Hyper-V Msvm_SerialPortSettingData.Connection 需要单元素数组；断开时写入 [""] 而非空数组。</summary>
    internal static string[] BuildConnectionValues(string? normalizedPipe)
        => [string.IsNullOrEmpty(normalizedPipe) ? string.Empty : normalizedPipe];

    internal static int ParsePortNumber(string instanceId, string elementName)
    {
        if (instanceId.EndsWith(@"\0", StringComparison.Ordinal)) return 1;
        if (instanceId.EndsWith(@"\1", StringComparison.Ordinal)) return 2;
        if (elementName.Contains('2')) return 2;
        return 1;
    }

    internal static string ResolvePortName(string elementName, int portNumber)
        => string.IsNullOrWhiteSpace(elementName) ? $"COM {portNumber}" : elementName;

    internal static string ExtractPipePath(object? connectionValue)
        => connectionValue switch
        {
            string[] array => array.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty,
            string text => text.Trim(),
            _ => string.Empty
        };
}
