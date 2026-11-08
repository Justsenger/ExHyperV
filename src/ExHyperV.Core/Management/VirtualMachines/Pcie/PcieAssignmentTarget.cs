namespace ExHyperV.Services;

/// <summary>PCIe 设备当前归属或分配目标的种类。</summary>
public enum PcieAssignmentKind
{
    /// <summary>设备由宿主使用。</summary>
    Host,
    /// <summary>设备已从宿主卸除，尚未分配给虚拟机；只能表示当前状态。</summary>
    Unassigned,
    /// <summary>设备属于指定 GUID 的虚拟机。</summary>
    VirtualMachine
}

/// <summary>PCIe 设备的归属身份，不使用本地化文字或内部字符串标记表示宿主与未分配状态。</summary>
/// <remarks>当前分配操作支持归还宿主或转移至虚拟机。Unassigned 用于查询结果，不能作为分配操作的目标。</remarks>
public sealed record PcieAssignmentTarget
{
    private PcieAssignmentTarget(PcieAssignmentKind kind, Guid? virtualMachineId = null, string? virtualMachineName = null)
    {
        Kind = kind;
        VirtualMachineId = virtualMachineId;
        VirtualMachineName = virtualMachineName;
    }

    /// <summary>宿主、未分配或虚拟机。</summary>
    public PcieAssignmentKind Kind { get; }

    /// <summary>虚拟机稳定 GUID；只有 Kind 为 VirtualMachine 时有值，操作不按显示名称重新定位。</summary>
    public Guid? VirtualMachineId { get; }

    /// <summary>虚拟机显示名称快照，仅用于呈现；重命名不改变目标身份。</summary>
    public string? VirtualMachineName { get; }

    /// <summary>表示设备由宿主使用，也可用于请求将设备归还宿主。</summary>
    public static PcieAssignmentTarget Host { get; } = new(PcieAssignmentKind.Host);

    /// <summary>表示设备已从宿主卸除且尚未分配；不能作为分配操作的目标。</summary>
    public static PcieAssignmentTarget Unassigned { get; } = new(PcieAssignmentKind.Unassigned);

    /// <summary>按稳定 GUID 创建虚拟机归属或目标，显示名称不参与定位。</summary>
    /// <param name="virtualMachineId">目标虚拟机的非空 GUID。</param>
    /// <param name="displayName">可选显示名称快照；未提供时以 GUID 展示。</param>
    /// <returns>表示该虚拟机的不可变目标。</returns>
    /// <exception cref="ArgumentException">GUID 为空。</exception>
    public static PcieAssignmentTarget ForVirtualMachine(Guid virtualMachineId, string? displayName = null)
    {
        if (virtualMachineId == Guid.Empty) throw new ArgumentException("虚拟机 GUID 不能为空。", nameof(virtualMachineId));
        return new(PcieAssignmentKind.VirtualMachine, virtualMachineId,
            string.IsNullOrWhiteSpace(displayName) ? virtualMachineId.ToString("D") : displayName);
    }

    /// <summary>比较归属身份；虚拟机显示名称变化不改变相等性。</summary>
    /// <param name="other">另一归属对象，可为 null。</param>
    /// <returns>种类与虚拟机 GUID 均相同才为 true。</returns>
    public bool Equals(PcieAssignmentTarget? other) => other is not null
        && Kind == other.Kind && VirtualMachineId == other.VirtualMachineId;

    /// <summary>按归属种类和虚拟机 GUID 生成哈希值，不包含显示名称。</summary>
    /// <returns>稳定身份的哈希值。</returns>
    public override int GetHashCode() => HashCode.Combine(Kind, VirtualMachineId);

    internal static PcieAssignmentTarget FromInternalStatus(string status)
    {
        string normalized = HostPcieService.NormalizeAssignmentStatus(status);
        if (normalized == HostPcieService.HostKey) return Host;
        if (normalized == HostPcieService.DismountedKey || string.IsNullOrWhiteSpace(normalized)) return Unassigned;
        if (!Guid.TryParse(normalized, out var id) || id == Guid.Empty)
            throw new ArgumentException("PCIe 虚拟机归属必须是 GUID，不能使用旧显示名称。", nameof(status));
        return ForVirtualMachine(id);
    }

    internal string ToInternalStatus() => Kind switch
    {
        PcieAssignmentKind.Host => HostPcieService.HostKey,
        PcieAssignmentKind.Unassigned => HostPcieService.DismountedKey,
        PcieAssignmentKind.VirtualMachine => VirtualMachineId!.Value.ToString("D"),
        _ => throw new InvalidOperationException("未知的 PCIe 分配归属。")
    };
}
