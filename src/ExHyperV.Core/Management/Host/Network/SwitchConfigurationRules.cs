using ExHyperV.Models;
using R = ExHyperV.Properties.Resources;

namespace ExHyperV.Services;

/// <summary>对交换机配置快照进行规则判断，不直接修改主机网络。</summary>
public static class SwitchConfigurationRules
{
    /// <summary>根据固定交换机身份判断是否为 Hyper-V 默认交换机。</summary>
    /// <returns>匹配默认交换机 ID 时为 true。</returns>
    /// <param name="id">交换机 ID，非显示名称。</param>
    public static bool IsDefaultSwitch(string id) =>
        string.Equals(id.Trim('{', '}'), "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444", StringComparison.OrdinalIgnoreCase);

    /// <summary>检查交换机身份是否受到删除保护。</summary>
    /// <returns>受保护时的本地化错误，否则为 null。</returns>
    /// <param name="targetId">待删除交换机 ID。</param>
    public static string? GetDeletionError(string targetId) =>
        IsDefaultSwitch(targetId) ? R.Warning_CannotModifyDefaultSwitch : null;

    /// <summary>判断指定连接模式是否要求上游网卡。</summary>
    /// <returns>桥接或 NAT 需要上游时为 true。</returns>
    /// <param name="mode">交换机连接模式。</param>
    public static bool RequiresUpstream(SwitchMode mode) => mode is SwitchMode.Bridge or SwitchMode.NAT;

    /// <summary>判断所选模式与上游信息是否已满足基本配置条件。</summary>
    /// <returns>基本输入完整时为 true；提交前还需校验主机状态。</returns>
    /// <param name="mode">目标连接模式：桥接、ICS NAT 或隔离。</param>
    /// <param name="upstream">桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。</param>
    public static bool IsConfigurationComplete(SwitchMode mode, SwitchUpstreamAdapter? upstream) =>
        !RequiresUpstream(mode) || upstream != null;

    /// <summary>结合模式与已占用网卡筛选新建交换机可选的上游。</summary>
    /// <returns>供调用方选择的网卡列表。</returns>
    /// <param name="mode">目标连接模式：桥接、ICS NAT 或隔离。</param>
    /// <param name="existing">当前交换机快照。</param>
    /// <param name="physical">当前物理上游网卡列表。</param>
    /// <param name="bridgeable">当前可桥接网卡列表。</param>
    public static List<SwitchUpstreamAdapter> GetAvailableCreationAdapters(SwitchMode mode,
        IEnumerable<SwitchInfo> existing, IEnumerable<SwitchUpstreamAdapter> physical, IEnumerable<SwitchUpstreamAdapter> bridgeable)
    {
        var occupied = existing.Where(s => s.Upstream != null).Select(s => s.Upstream!.ConnectionId).ToHashSet();
        var source = mode == SwitchMode.Bridge ? bridgeable : physical;
        return source.Where(a => !occupied.Contains(a.ConnectionId)).ToList();
    }

    /// <summary>检查名称、模式和上游是否满足新建交换机规则。</summary>
    /// <returns>本地化错误；null 表示当前快照下规则通过。</returns>
    /// <param name="name">新交换机显示名称。</param>
    /// <param name="mode">目标连接模式：桥接、ICS NAT 或隔离。</param>
    /// <param name="upstream">桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。</param>
    /// <param name="existing">当前交换机快照。</param>
    /// <param name="physical">当前物理上游网卡列表。</param>
    /// <param name="bridgeable">当前可桥接网卡列表。</param>
    public static string? GetCreationError(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream,
        IReadOnlyList<SwitchInfo> existing, IEnumerable<SwitchUpstreamAdapter> physical, IEnumerable<SwitchUpstreamAdapter> bridgeable)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (string.IsNullOrWhiteSpace(name)) return R.AddSwitch_Validation_NameCannotBeEmpty;
        if (existing.Any(s => s.SwitchName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return string.Format(R.AddSwitch_Validation_NameExists, name);
        var available = GetAvailableCreationAdapters(mode, existing, physical, bridgeable);
        if (RequiresUpstream(mode) && available.Count == 0) return R.AddSwitch_Validation_NoAdaptersForExternalOrNat;
        if (!IsConfigurationComplete(mode, upstream)) return R.AddSwitch_Validation_AdapterRequiredForExternalOrNat;
        if (mode == SwitchMode.NAT && existing.Any(s => !IsDefaultSwitch(s.Id) && s.SwitchType == SwitchMode.NAT))
            return R.AddSwitch_Validation_OnlyOneNatAllowed;
        if (RequiresUpstream(mode) && !available.Any(a => a.ConnectionId == upstream!.ConnectionId))
            return R.Network_UpstreamMissing;
        return null;
    }

    /// <summary>检查现有交换机修改是否违反默认交换机或上游占用规则。</summary>
    /// <returns>本地化冲突说明；null 表示未发现冲突。</returns>
    /// <param name="targetId">待修改交换机 ID。</param>
    /// <param name="mode">目标连接模式：桥接、ICS NAT 或隔离。</param>
    /// <param name="upstream">桥接/NAT 使用的当前上游网卡身份；隔离模式可为 null。</param>
    /// <param name="existing">当前交换机快照。</param>
    public static string? GetUpdateConflict(string targetId, SwitchMode mode, SwitchUpstreamAdapter? upstream,
        IEnumerable<SwitchInfo> existing)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (IsDefaultSwitch(targetId)) return R.Warning_CannotModifyDefaultSwitch;
        var others = existing.Where(s => !string.Equals(s.Id.Trim('{', '}'), targetId.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)).ToList();
        var nat = mode == SwitchMode.NAT
            ? others.FirstOrDefault(s => !IsDefaultSwitch(s.Id) && s.SwitchType == SwitchMode.NAT) : null;
        if (nat != null) return string.Format(R.Error_OnlyOneNatNetworkAllowed, nat.SwitchName);
        var occupied = RequiresUpstream(mode) && upstream != null
            ? others.FirstOrDefault(s => s.Upstream?.ConnectionId == upstream.ConnectionId) : null;
        return occupied == null ? null : string.Format(R.Error_PhysicalAdapterInUse, upstream, occupied.SwitchName);
    }
}

/// <summary>表示交换机变更违反当前配置保护或冲突规则。</summary>
/// <param name="message">可供调用方展示的冲突说明。</param>
public sealed class SwitchConfigurationConflictException(string message) : ExHyperV.Tools.CoreOperationException(
    ExHyperV.Tools.ApiResponse.Fail(message, kind: ExHyperV.Tools.ApiErrorKind.Conflict));
