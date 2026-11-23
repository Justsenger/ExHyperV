using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

internal sealed class VmNetworkCardExpansion
{
    private readonly ConcurrentDictionary<Guid, ImmutableHashSet<Guid>> expandedByVm = new();

    internal ImmutableHashSet<Guid> GetExpanded(Guid vmId, IReadOnlyList<VmNetworkAdapter> adapters) =>
        adapters.Count == 0 ? [] : expandedByVm.GetOrAdd(vmId, _ => [adapters[0].DeviceId]);

    internal void SetExpanded(Guid vmId, Guid deviceId, bool expanded) =>
        expandedByVm.AddOrUpdate(vmId, expanded ? [deviceId] : ImmutableHashSet<Guid>.Empty,
            (_, ids) => expanded ? ids.Add(deviceId) : ids.Remove(deviceId));

    internal void ExpandNew(Guid vmId, Guid deviceId) => expandedByVm[vmId] = [deviceId];
}

// Keep text edits (including temporarily invalid input) separate from the queried adapter.
internal sealed class VmNetworkPolicyDraft
{
    internal VmNetworkAdapter Saved { get; }
    internal VmNetworkAdapter Values { get; }
    internal Guid? SwitchId { get; set; }
    internal string AllowedVlans { get; set; }

    internal VmNetworkPolicyDraft(VmNetworkAdapter saved)
    {
        Saved = saved;
        Values = saved.Clone();
        RestoreMac();
        SwitchId = saved.IsConnected ? saved.SwitchId : null;
        if (saved.VlanMode != VlanOperationMode.Access) Values.AccessVlanId = 20;
        if (saved.VlanMode != VlanOperationMode.Trunk) Values.NativeVlanId = 1;
        if (saved.VlanMode != VlanOperationMode.Private)
        {
            Values.PvlanMode = PvlanMode.Isolated;
            Values.PvlanPrimaryId = 100;
            Values.PvlanSecondaryId = 101;
        }
        else if (saved.PvlanMode == PvlanMode.Promiscuous) Values.PvlanSecondaryId = 101;
        AllowedVlans = saved.VlanMode == VlanOperationMode.Trunk ? string.Join(",", saved.TrunkAllowedVlanIds) : "10, 20, 30-40";
    }

    internal bool ConnectionChanged => SwitchId != (Saved.IsConnected ? Saved.SwitchId : null);
    internal string? ReportedDynamicMac => !Saved.IsStaticMac && Saved.HasReportedMac ? Saved.MacAddress : null;

    internal void RestoreMac()
    {
        Values.IsStaticMac = Saved.IsStaticMac;
        Values.MacAddress = Saved.IsStaticMac ? Saved.MacAddress : ReportedDynamicMac ?? string.Empty;
    }

    internal static string? SuggestStaticMac(IReadOnlyList<VmNetworkAdapter> adapters)
    {
        var occupied = adapters.Select(a => VmNetworkService.NormalizeStaticMac(a.MacAddress)).ToHashSet();
        for (int offset = 0; offset < 256; offset++)
        {
            int tail = (0x0E + adapters.Count + offset) & 0xFF;
            string candidate = $"00:15:5D:01:2A:{tail:X2}";
            if (!occupied.Contains(VmNetworkService.NormalizeStaticMac(candidate))) return candidate;
        }
        return null;
    }

    internal bool MacChanged => Values.IsStaticMac != Saved.IsStaticMac || Values.IsStaticMac &&
        !string.Equals(VmNetworkService.NormalizeStaticMac(Values.MacAddress) ?? Values.MacAddress,
                       VmNetworkService.NormalizeStaticMac(Saved.MacAddress) ?? Saved.MacAddress, StringComparison.Ordinal);
    internal bool BandwidthChanged => Values.BandwidthLimitBitsPerSecond != Saved.BandwidthLimitBitsPerSecond;
    internal bool IsDirty => ConnectionChanged || MacChanged || BandwidthChanged || VlanChanges().Count > 0 || SecurityChanges().Count > 0;

    internal List<string> SecurityChanges()
    {
        List<string> changes = [];
        if (Values.MacSpoofingAllowed != Saved.MacSpoofingAllowed) changes.Add(nameof(Values.MacSpoofingAllowed));
        if (Values.TeamingAllowed != Saved.TeamingAllowed) changes.Add(nameof(Values.TeamingAllowed));
        if (Values.DhcpGuardEnabled != Saved.DhcpGuardEnabled) changes.Add(nameof(Values.DhcpGuardEnabled));
        if (Values.RouterGuardEnabled != Saved.RouterGuardEnabled) changes.Add(nameof(Values.RouterGuardEnabled));
        if (Values.MonitorMode != Saved.MonitorMode) changes.Add(nameof(Values.MonitorMode));
        if (Values.StormLimit != Saved.StormLimit) changes.Add(nameof(Values.StormLimit));
        if (Values.SriovEnabled != Saved.SriovEnabled) changes.Add(nameof(Values.SriovEnabled));
        return changes;
    }

    internal List<string> VlanChanges()
    {
        List<string> changes = [];
        bool mode = Values.VlanMode != Saved.VlanMode;
        if (mode) changes.Add(nameof(Values.VlanMode));
        switch (Values.VlanMode)
        {
            case VlanOperationMode.Access:
                if (mode || Values.AccessVlanId != Saved.AccessVlanId) changes.Add(nameof(Values.AccessVlanId));
                break;
            case VlanOperationMode.Trunk:
                if (mode || Values.NativeVlanId != Saved.NativeVlanId) changes.Add(nameof(Values.NativeVlanId));
                if (mode || !SameIds(AllowedVlans, Saved.TrunkAllowedVlanIds)) changes.Add(nameof(Values.TrunkAllowedVlanIds));
                break;
            case VlanOperationMode.Private:
                bool role = mode || Values.PvlanMode != Saved.PvlanMode;
                if (role) changes.Add(nameof(Values.PvlanMode));
                if (mode || Values.PvlanPrimaryId != Saved.PvlanPrimaryId) changes.Add(nameof(Values.PvlanPrimaryId));
                if (Values.PvlanMode != PvlanMode.Promiscuous && (role || Values.PvlanSecondaryId != Saved.PvlanSecondaryId))
                    changes.Add(nameof(Values.PvlanSecondaryId));
                break;
        }
        return changes;
    }

    private static bool SameIds(string text, List<int> ids) =>
        text == string.Join(",", ids) || TryParseVlanList(text, out var parsed) && parsed.ToHashSet().SetEquals(ids);

    internal string? PrepareVlan()
    {
        switch (Values.VlanMode)
        {
            case VlanOperationMode.Access when !ValidId(Values.AccessVlanId):
            case VlanOperationMode.Trunk when !ValidId(Values.NativeVlanId) && !(Values.NativeVlanId == 0 && Saved.VlanMode == VlanOperationMode.Trunk && Saved.NativeVlanId == 0):
            case VlanOperationMode.Private when !ValidId(Values.PvlanPrimaryId):
                return "VLAN ID 必须是 1 至 4094 之间的整数。";
            case VlanOperationMode.Trunk:
                if (!TryParseVlanList(AllowedVlans, out var trunks)) return "允许的 VLAN 列表须使用 1 至 4094 的编号或升序区间，例如 10,20-30。";
                Values.TrunkAllowedVlanIds = trunks;
                break;
            case VlanOperationMode.Private when Values.PvlanMode == PvlanMode.Promiscuous:
                break;
            case VlanOperationMode.Private:
                if (!ValidId(Values.PvlanSecondaryId) || Values.PvlanSecondaryId == Values.PvlanPrimaryId)
                    return "辅助 VLAN ID 必须是 1 至 4094 之间的整数，并且与主 VLAN ID 不同。";
                break;
        }
        return null;
    }

    private static bool ValidId(int id) => id is >= 1 and <= 4094;

    internal static string FormatBandwidthMbps(ulong bits) =>
        (bits / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);

    internal static bool TryParseBandwidthMbps(string text, out ulong bits)
    {
        bits = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mbps)
            || mbps > ulong.MaxValue / 1_000_000m) return false;
        decimal value = mbps * 1_000_000m;
        if (value != decimal.Truncate(value)) return false;
        bits = (ulong)value;
        return true;
    }

    internal static bool TryParseVlanList(string text, out List<int> ids)
    {
        ids = [];
        var unique = new SortedSet<int>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length > 2 || !TryId(bounds[0], out int first)) return false;
            int last = first;
            if (bounds.Length == 2 && (!TryId(bounds[1], out last) || last < first)) return false;
            for (int id = first; id <= last; id++) unique.Add(id);
        }
        ids = [.. unique];
        return true;

        static bool TryId(string value, out int id) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && ValidId(id);
    }
}
