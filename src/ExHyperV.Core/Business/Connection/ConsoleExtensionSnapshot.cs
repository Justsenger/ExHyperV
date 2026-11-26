namespace ExHyperV.Services;

/// <summary>一个目标虚拟机在当前观众上下文中的四段探测结果。Versioned per-viewer extension status.</summary>
/// <param name="version">权威快照版本，动作请求须回传此值。</param>
/// <param name="host">宿主组件状态。</param>
/// <param name="authorization">当前主体授权状态。</param>
/// <param name="guest">目标虚拟机组件状态。</param>
/// <param name="transport">传输路径状态。</param>
/// <param name="transportKind">当前传输种类，未知值走通用显示。</param>
/// <param name="surfaceEpoch">传输表面纪元，变化后使用完整帧。</param>
/// <param name="supported">后端声明支持的能力。</param>
/// <param name="granted">当前主体获准的能力范围。</param>
/// <param name="control">当前观众的有效控制状态。</param>
/// <param name="availableActions">经调用方识别的允许动作，未知动作默认禁用。</param>
/// <param name="reason">提供方已脱敏的原因说明。</param>
/// <param name="diagnosticId">用于关联诊断的非敏感编号。</param>
public sealed class ConsoleExtensionSnapshot(ulong version,
    ConsoleExtensionStage host,
    ConsoleExtensionStage authorization,
    ConsoleExtensionStage guest,
    ConsoleExtensionStage transport,
    ConsoleTransportKind transportKind,
    ulong surfaceEpoch,
    ConsoleCapabilities supported,
    ConsoleCapabilities granted,
    ConsoleControlState control,
    IReadOnlyList<ConsoleExtensionActionId> availableActions,
    string? reason,
    string? diagnosticId)
{
    /// <summary>权威快照版本，动作请求须回传此值。</summary>
    /// <fielddoc><meaning>权威快照版本，动作请求须回传此值。</meaning></fielddoc>
    public ulong Version { get; } = version;

    /// <summary>宿主组件状态。</summary>
    /// <fielddoc><meaning>宿主组件状态。</meaning></fielddoc>
    public ConsoleExtensionStage Host { get; } = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>当前主体授权状态。</summary>
    /// <fielddoc><meaning>当前主体授权状态。</meaning></fielddoc>
    public ConsoleExtensionStage Authorization { get; } = authorization ?? throw new ArgumentNullException(nameof(authorization));

    /// <summary>目标虚拟机组件状态。</summary>
    /// <fielddoc><meaning>目标虚拟机组件状态。</meaning></fielddoc>
    public ConsoleExtensionStage Guest { get; } = guest ?? throw new ArgumentNullException(nameof(guest));

    /// <summary>传输路径状态。</summary>
    /// <fielddoc><meaning>传输路径状态。</meaning></fielddoc>
    public ConsoleExtensionStage Transport { get; } = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>当前传输种类，未知值走通用显示。</summary>
    /// <fielddoc><meaning>当前传输种类，未知值走通用显示。</meaning></fielddoc>
    public ConsoleTransportKind TransportKind { get; } = transportKind;

    /// <summary>传输表面纪元，变化后使用完整帧。</summary>
    /// <fielddoc><meaning>传输表面纪元，变化后使用完整帧。</meaning></fielddoc>
    public ulong SurfaceEpoch { get; } = surfaceEpoch;

    /// <summary>后端声明支持的能力。</summary>
    /// <fielddoc><meaning>后端声明支持的能力。</meaning></fielddoc>
    public ConsoleCapabilities Supported { get; } = supported;

    /// <summary>当前主体获准的能力范围。</summary>
    /// <fielddoc><meaning>当前主体获准的能力范围。</meaning></fielddoc>
    public ConsoleCapabilities Granted { get; } = granted;

    /// <summary>当前观众的有效控制状态。</summary>
    /// <fielddoc><meaning>当前观众的有效控制状态。</meaning></fielddoc>
    public ConsoleControlState Control { get; } = control ?? throw new ArgumentNullException(nameof(control));

    /// <summary>经调用方识别的允许动作，未知动作默认禁用。</summary>
    /// <fielddoc><meaning>经调用方识别的允许动作，未知动作默认禁用。</meaning></fielddoc>
    public IReadOnlyList<ConsoleExtensionActionId> AvailableActions { get; } = Array.AsReadOnly(availableActions.Where(id => Enum.IsDefined(id) && id != ConsoleExtensionActionId.Unknown).Distinct().ToArray());

    /// <summary>提供方已脱敏的原因说明。</summary>
    /// <fielddoc><meaning>提供方已脱敏的原因说明。</meaning></fielddoc>
    public string? Reason { get; } = reason;

    /// <summary>用于关联诊断的非敏感编号。</summary>
    /// <fielddoc><meaning>用于关联诊断的非敏感编号。</meaning></fielddoc>
    public string? DiagnosticId { get; } = diagnosticId;
}
