using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services;

public static partial class VmNetworkService
{
    private static readonly string[] VlanFields = [nameof(VmNetworkAdapter.VlanMode), nameof(VmNetworkAdapter.AccessVlanId),
        nameof(VmNetworkAdapter.NativeVlanId), nameof(VmNetworkAdapter.TrunkAllowedVlanIds), nameof(VmNetworkAdapter.PvlanMode),
        nameof(VmNetworkAdapter.PvlanPrimaryId), nameof(VmNetworkAdapter.PvlanSecondaryId), nameof(VmNetworkAdapter.PvlanSecondaryIds)];
    private static readonly string[] BandwidthFields = [nameof(VmNetworkAdapter.BandwidthLimitBitsPerSecond), nameof(VmNetworkAdapter.BandwidthReservationBitsPerSecond)];
    private static readonly string[] SecurityFields = [nameof(VmNetworkAdapter.MacSpoofingAllowed), nameof(VmNetworkAdapter.DhcpGuardEnabled),
        nameof(VmNetworkAdapter.RouterGuardEnabled), nameof(VmNetworkAdapter.TeamingAllowed), nameof(VmNetworkAdapter.MonitorMode), nameof(VmNetworkAdapter.StormLimit)];
    private static readonly string[] OffloadFields = [nameof(VmNetworkAdapter.VmqEnabled), nameof(VmNetworkAdapter.SriovEnabled), nameof(VmNetworkAdapter.IpsecOffloadEnabled)];

    private static async Task<ApiResponse> ApplySelectedFeatureAsync(Guid vmId, VmNetworkAdapter adapter,
        IReadOnlyCollection<string> changedProperties, string featureClass, string[] allowed)
    {
        SettingsSelection<VmNetworkAdapter> selection;
        try { selection = SettingsSelection<VmNetworkAdapter>.Capture(adapter, changedProperties, allowed); }
        catch (ArgumentException ex) { return ApiResponse.Fail(ex.Message, kind: ApiErrorKind.InvalidArgument); }
        string id = adapter.Id;
        return await VmSettingsWriteLock.RunAsync(vmId, async () =>
        {
            var ownership = await ValidateAdapterOwnershipAsync(vmId, id);
            return ownership.Success ? await ApplyCapturedFeatureAsync(id, featureClass, selection) : ownership;
        });
    }

    private static Task<ApiResponse> ApplyCapturedFeatureAsync(string id, string featureClass, SettingsSelection<VmNetworkAdapter> selection)
        => EnsureAndModifyFeatureAsync(id, featureClass, feature =>
        {
            var raw = feature.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value);
            foreach (var (name, value) in BuildNetworkWrites(featureClass, raw, selection)) feature[name] = value;
        });

    internal static Dictionary<string, object?> BuildNetworkWrites(string featureClass,
        IReadOnlyDictionary<string, object?> raw, SettingsSelection<VmNetworkAdapter> selection)
    {
        var values = new SettingsValues(raw);
        void Write(string property, string native, Func<object?, object?>? convert = null)
        {
            if (selection.Contains(property)) values[native] = convert == null ? selection[property] : convert(selection[property]);
        }
        void Toggle(string property, string native, uint enabled)
        {
            if (selection.Contains(property) && (Convert.ToUInt32(raw[native]) > 0) != (bool)selection[property]!)
                values[native] = (bool)selection[property]! ? enabled : 0u;
        }
        switch (featureClass)
        {
            case "Msvm_EthernetSwitchPortBandwidthSettingData":
                Write(nameof(VmNetworkAdapter.BandwidthLimitBitsPerSecond), "Limit");
                Write(nameof(VmNetworkAdapter.BandwidthReservationBitsPerSecond), "Reservation");
                break;
            case "Msvm_EthernetSwitchPortSecuritySettingData":
                Write(nameof(VmNetworkAdapter.MacSpoofingAllowed), "AllowMacSpoofing");
                Write(nameof(VmNetworkAdapter.DhcpGuardEnabled), "EnableDhcpGuard");
                Write(nameof(VmNetworkAdapter.RouterGuardEnabled), "EnableRouterGuard");
                Write(nameof(VmNetworkAdapter.TeamingAllowed), "AllowTeaming");
                Write(nameof(VmNetworkAdapter.MonitorMode), "MonitorMode", v => checked((byte)(PortMonitorMode)v!));
                Write(nameof(VmNetworkAdapter.StormLimit), "StormLimit");
                break;
            case "Msvm_EthernetSwitchPortOffloadSettingData":
                Toggle(nameof(VmNetworkAdapter.VmqEnabled), "VMQOffloadWeight", 100);
                Toggle(nameof(VmNetworkAdapter.SriovEnabled), "IOVOffloadWeight", 1);
                Toggle(nameof(VmNetworkAdapter.IpsecOffloadEnabled), "IPSecOffloadLimit", 512);
                break;
            case "Msvm_EthernetSwitchPortVlanSettingData":
                BuildVlanWrites(raw, values, selection);
                break;
            default: throw new ArgumentException("不支持的网卡设置类别。", nameof(featureClass));
        }
        return values.Writes;
    }

    internal static VlanOperationMode ReadVlanMode(uint mode, int accessId) => mode switch
    {
        0 => VlanOperationMode.Off,
        1 => accessId == 0 ? VlanOperationMode.Off : VlanOperationMode.Access,
        2 => VlanOperationMode.Trunk,
        3 => VlanOperationMode.Private,
        _ => VlanOperationMode.Unknown
    };

    private static void BuildVlanWrites(IReadOnlyDictionary<string, object?> raw, SettingsValues values,
        SettingsSelection<VmNetworkAdapter> selection)
    {
        int Number(string key) => raw.TryGetValue(key, out var v) ? Convert.ToInt32(v) : 0;
        var current = new VmNetworkAdapter
        {
            VlanMode = ReadVlanMode((uint)Number("OperationMode"), Number("AccessVlanId")),
            AccessVlanId = Number("AccessVlanId"), NativeVlanId = Number("NativeVlanId"),
            PvlanMode = (PvlanMode)Number("PvlanMode"), PvlanPrimaryId = Number("PrimaryVlanId"), PvlanSecondaryId = Number("SecondaryVlanId"),
            TrunkAllowedVlanIds = raw.GetValueOrDefault("TrunkVlanIdArray") is ushort[] ids ? ids.Select(x => (int)x).ToList() : [],
            PvlanSecondaryIds = raw.GetValueOrDefault("SecondaryVlanIdArray") is ushort[] secondary ? secondary.Select(x => (int)x).ToList() : []
        };
        var target = selection.ApplyTo(current);
        bool mode = selection.Contains(nameof(VmNetworkAdapter.VlanMode));
        // Existing callers may set only AccessVlanId on an untagged access port.
        if (!mode && target.VlanMode == VlanOperationMode.Off && selection.Contains(nameof(VmNetworkAdapter.AccessVlanId)))
        {
            target.VlanMode = VlanOperationMode.Access;
            values["OperationMode"] = 1u;
        }
        if (!mode && selection.Names.Any(name => name switch
        {
            nameof(VmNetworkAdapter.AccessVlanId) => target.VlanMode != VlanOperationMode.Access,
            nameof(VmNetworkAdapter.NativeVlanId) or nameof(VmNetworkAdapter.TrunkAllowedVlanIds) => target.VlanMode != VlanOperationMode.Trunk,
            nameof(VmNetworkAdapter.PvlanMode) or nameof(VmNetworkAdapter.PvlanPrimaryId) or nameof(VmNetworkAdapter.PvlanSecondaryId) => target.VlanMode != VlanOperationMode.Private,
            nameof(VmNetworkAdapter.PvlanSecondaryIds) => target.VlanMode != VlanOperationMode.Private || target.PvlanMode != PvlanMode.Promiscuous,
            _ => false
        })) throw new ArgumentException("所选字段不适用于当前 VLAN 模式，需要切换模式时请同时选择 VlanMode。");
        bool Selected(string name) => mode || selection.Contains(name);
        ushort Id(int id) => id is >= 0 and <= 4094 ? (ushort)id : throw new ArgumentException("VLAN ID 必须为 0 至 4094。");
        if (mode) values["OperationMode"] = target.VlanMode == VlanOperationMode.Off ? 1u : (uint)target.VlanMode;
        switch (target.VlanMode)
        {
            case VlanOperationMode.Off:
                break;
            case VlanOperationMode.Access:
                if (Selected(nameof(VmNetworkAdapter.AccessVlanId))) values["AccessVlanId"] = Id(target.AccessVlanId);
                break;
            case VlanOperationMode.Trunk:
                if (Selected(nameof(VmNetworkAdapter.NativeVlanId))) values["NativeVlanId"] = Id(target.NativeVlanId);
                if (Selected(nameof(VmNetworkAdapter.TrunkAllowedVlanIds))) values["TrunkVlanIdArray"] = target.TrunkAllowedVlanIds.Select(Id).ToArray();
                break;
            case VlanOperationMode.Private:
                bool role = Selected(nameof(VmNetworkAdapter.PvlanMode));
                uint pMode = target.PvlanMode == PvlanMode.None ? 1u : (uint)target.PvlanMode;
                if (pMode is < 1 or > 3) throw new ArgumentException("专用 VLAN 角色无效。");
                if (role) values["PvlanMode"] = pMode;
                ushort primary = Id(target.PvlanPrimaryId == 0 ? 100 : target.PvlanPrimaryId);
                if (Selected(nameof(VmNetworkAdapter.PvlanPrimaryId))) values["PrimaryVlanId"] = primary;
                if (role || selection.Contains(nameof(VmNetworkAdapter.PvlanSecondaryId)) || pMode == 3 && selection.Contains(nameof(VmNetworkAdapter.PvlanPrimaryId)))
                {
                    values["SecondaryVlanId"] = pMode == 3 ? (ushort)0 : Id(target.PvlanSecondaryId == 0 ? 101 : target.PvlanSecondaryId);
                    if (pMode != 3) values["SecondaryVlanIdArray"] = null;
                    else if (!selection.Contains(nameof(VmNetworkAdapter.PvlanSecondaryIds))
                        && (role || raw.GetValueOrDefault("SecondaryVlanIdArray") is not ushort[] { Length: > 0 }))
                        values["SecondaryVlanIdArray"] = new[] { primary };
                }
                if (pMode == 3 && selection.Contains(nameof(VmNetworkAdapter.PvlanSecondaryIds)))
                {
                    if (target.PvlanSecondaryIds.Count == 0)
                        throw new ArgumentException("混杂模式必须指定辅助 VLAN 列表。");
                    if (target.PvlanSecondaryIds.Contains(primary))
                        throw new ArgumentException("混杂模式的辅助 VLAN 列表不能包含主 VLAN ID。");
                    values["SecondaryVlanIdArray"] = target.PvlanSecondaryIds.Select(Id).ToArray();
                }
                break;
            default: throw new ArgumentException("VLAN 模式无效。");
        }
        // 只有调用方选择切换模式时才清空不适用的模式字段。
        if (mode)
        {
            if (target.VlanMode != VlanOperationMode.Access) values["AccessVlanId"] = (ushort)0;
            if (target.VlanMode != VlanOperationMode.Trunk) { values["NativeVlanId"] = (ushort)0; values["TrunkVlanIdArray"] = null; }
            if (target.VlanMode != VlanOperationMode.Trunk && values.HasProperty("PruneVlanIdArray")) values["PruneVlanIdArray"] = null;
            if (target.VlanMode != VlanOperationMode.Private)
            {
                values["PvlanMode"] = 0u; values["PrimaryVlanId"] = (ushort)0;
                values["SecondaryVlanId"] = (ushort)0; values["SecondaryVlanIdArray"] = null;
            }
        }
    }
}
