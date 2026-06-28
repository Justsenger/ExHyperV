#nullable disable
// VmcxSchema — .vmcx 知识层(增量可补充)。组织键到子系统、已知设备类型、互斥/不变量规则。
// 没有它编辑器也能用(裸树模式);有了它就能做友好结构化视图 + 存前校验。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ExHyperV.Vmcx {

public enum VmcxSubsystem { Properties, Settings, GlobalSettings, Manifest, Device, Other }

/// <summary>一个设备(由 manifest 的 vdevNNN 描述)。</summary>
public sealed class VmcxDevice {
    public int    VdevNumber;
    public string Instance;      // 实例 GUID(对应数据节点 /_<instance>_)
    public string DeviceType;    // 类型 GUID
    public string Name;          // manifest name
    public string TypeFriendly;  // 已知类型友好名
}

public static class VmcxSchema {
    /// <summary>已知设备类型 GUID → 友好名(逆向 + 实测,可持续补充)。</summary>
    public static readonly Dictionary<string,string> DeviceTypes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
        // 实测自 Gen1+Gen2 VM 的 manifest(device 类型 GUID → name),含平台/模拟/合成/集成/DDA/GPU-PV/vTPM 全谱。
        { "197f74e3-b84b-46de-8ae6-82f1cd181cdc", "Microsoft Synthetic Keyboard" },
        { "2497f4de-e9fa-4204-80e4-4b75c46419c0", "Microsoft Time Synchronization Component" },
        { "2a34b1c2-fd73-4043-8a5b-dd2159bc743f", "Microsoft Key-Value Pair Exchange Component" },
        { "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", "Synthetic Ethernet Port" },
        { "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", "Virtual Pci Express Port (DDA)" },
        { "35b0b12f-a0d7-482f-80a0-f52f1ab3da2e", "Microsoft Emulated SuperIo Device" },
        { "4d42d9f7-6531-4f6c-9e46-1f0477876104", "Microsoft Emulated ISA Bus" },
        { "4d46d139-7821-4dc4-98a3-01e98a586a44", "Microsoft Emulated Speaker Device" },
        { "58f75a6d-d949-4320-99e1-a2a2576d581c", "Microsoft Synthetic Mouse" },
        { "5ced1297-4598-4915-a5fc-ad21bb4d02a4", "Microsoft VSS Component" },
        { "655bc5c5-a784-46b7-81bc-e26328f7eb0e", "Microsoft Emulated I8042 Controller" },
        { "6c09bb55-d683-4da0-8931-c9bf705f6480", "Microsoft Guest Interface Component" },
        { "6c5addb9-a11a-4e8e-84cb-e6208201db63", "Microsoft RDV Component" },
        { "72682fc4-040a-430a-be0b-224574b953fe", "Microsoft Emulated IoAPIC" },
        { "736e6aa9-a3f8-49c0-9550-a963214d259a", "Microsoft Virtual TPM Device" },
        { "7d80d3db-61ee-4879-8879-5609f1100ad0", "Microsoft Emulated Video S3" },
        { "83f8638b-8dca-4152-9eda-2ca8b33039b4", "Microsoft Emulated IDE Controller" },
        { "84535fad-4d98-4a6a-bdcd-21d5720dc430", "Microsoft Emulated PCI Bus" },
        { "84eaae65-2f2e-45f5-9bb5-0e857dc8eb47", "Microsoft Heartbeat Component" },
        { "87045ce9-5323-438f-93bb-1e83dcbce18e", "Microsoft Emulated DMA Controller" },
        { "8e3a359f-559a-4b6a-98a9-1690a6100ed7", "Microsoft Emulated Serial Controller" },
        { "8f0d2762-0b00-4e04-af4f-19010527cb93", "Microsoft Emulated Floppy Controller" },
        { "917bd8cb-3bb6-4124-9383-b7d21ac07f79", "Microsoft Guest Runtime State" },
        { "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", "GPU Partition" },
        { "9cb98db1-4d09-4538-a192-2d3d8c0b6cdb", "Microsoft Rdp Encoder" },
        { "9ed5fd4b-40c3-4de3-8597-98ecd17035da", "Microsoft Synthetic Rdp Device" },
        { "9edd1639-9bca-40dc-b3a2-07c828da60b5", "Microsoft Emulated PIC" },
        { "9f8233ac-be49-4c79-8ee3-e7e1985b2077", "Microsoft Shutdown Component" },
        { "a28e4d02-3323-4148-9569-565930a5cb39", "Microsoft Emulated PIT" },
        { "ac6b8dc1-3257-4a70-b1b2-a9c9215659ad", "Microsoft Virtual BIOS" },
        { "ba8735ef-e3a9-4f1b-badd-dbf3a5909915", "Microsoft Video Monitor" },
        { "bc12c717-8898-4688-8ee4-2cd14894f8ea", "Microsoft Hyper-V Activation Component" },
        { "d41a1872-3740-41ce-a1ee-4522ab82f991", "Microsoft VmBus" },
        { "d422512d-2bf2-4752-809d-7b82b5fcb1b4", "Synthetic SCSI Controller" },
        { "db8b9818-b4bb-4725-b99d-b4612716b6b4", "Microsoft Emulated Power Management Device" },
        { "de6cdc86-e1fb-4940-801b-c3c1a26c4da4", "Microsoft Input Management Device" },
        { "deaeeed3-4119-4b44-95cf-a73604b76971", "Microsoft Synthetic Debug Device" },
        { "e3c82929-edb4-475e-85a4-29aaa2a30c2d", "Microsoft Dynamic Memory Controller" },
        { "e51b7ef6-4a7f-4780-aaae-d4b291aacd2e", "Microsoft Emulated Real Time Clock" },
        { "f3cf6965-e8d3-44a9-9b7d-a04245ea7525", "Microsoft Synthetic Video" },
    };

    /// <summary>功能设备类型:必须有非空数据节点。这些类型的 manifest 条目若数据节点为空=幽灵(致命,VM起不来)。
    /// 补"同类型兄弟全坏时漏判"的缺口——不依赖兄弟有数据,只要属此集合且数据节点空即判幽灵。</summary>
    public static readonly HashSet<string> FunctionalDeviceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", // Synthetic Ethernet Port (NIC)
        "d422512d-2bf2-4752-809d-7b82b5fcb1b4", // Synthetic SCSI Controller
        "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", // Virtual Pci Express Port (DDA)
        "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", // GPU Partition
    };

    /// <summary>设备模板的一个键(相对数据节点)。Key 与 Value 都支持占位符,AddDevice 时替换:
    /// $B={新大写花括号GUID,每次新} / $S=本设备的命名 Setting GUID(模板内复用,路径与值都用) / $MAC=随机 Hyper-V MAC / $P:name=调用方参数。</summary>
    public sealed class DeviceTemplateKey {
        public string Key; public VmcxValueType Type; public string Value;
        public DeviceTemplateKey(string k, VmcxValueType t, string v){ Key=k; Type=t; Value=v; }
    }
    /// <summary>一种设备的建模(类型GUID + manifest名 + 数据节点必填键)。键集实测自官方 cmdlet 所写——官方写的=vmms 接受的合法最小集。</summary>
    public sealed class DeviceTemplate {
        public string DeviceType; public string Name; public DeviceTemplateKey[] Keys; public string[] RequiredParams;
    }
    static DeviceTemplateKey K(string k, VmcxValueType t, string v){ return new DeviceTemplateKey(k,t,v); }

    /// <summary>各设备类型 → 建设备所需模板。AddDevice 据此从零建出合法设备。可持续补充。</summary>
    public static readonly Dictionary<string,DeviceTemplate> DeviceTemplates = new Dictionary<string,DeviceTemplate>(StringComparer.OrdinalIgnoreCase) {
        { "d422512d-2bf2-4752-809d-7b82b5fcb1b4", new DeviceTemplate { DeviceType="d422512d-2bf2-4752-809d-7b82b5fcb1b4", Name="Synthetic SCSI Controller", Keys=new[]{
            K("ChannelInstanceGuid", VmcxValueType.String,  "$B"),
            K("TargetVtl",           VmcxValueType.Integer, "0"),
            K("VDEVVersion",         VmcxValueType.Integer, "512"),
        }}},
        { "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", new DeviceTemplate { DeviceType="99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", Name="GPU Partition", Keys=new[]{
            K("InstanceGuid", VmcxValueType.String,  "$B"),
            K("PoolID",       VmcxValueType.String,  ""),
            K("VDEVVersion",  VmcxValueType.Integer, "256"),
        }}},
        { "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", new DeviceTemplate { DeviceType="2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", Name="Virtual Pci Express Port", RequiredParams=new[]{"devicepath"}, Keys=new[]{
            K("HostResources/count",                   VmcxValueType.Integer, "1"),
            K("HostResources/HostResource/Instance",   VmcxValueType.String,  "$P:devicepath"), // PCIP\VEN_...\...
            K("InstanceGuid",        VmcxValueType.String,  "$B"),
            K("NumaAwarePlacement",  VmcxValueType.Boolean, "False"),
            K("PoolId",              VmcxValueType.String,  ""),
            K("TargetVtl",           VmcxValueType.Integer, "0"),
            K("VDEVVersion",         VmcxValueType.Integer, "256"),
        }}},
        { "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", new DeviceTemplate { DeviceType="2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", Name="Synthetic Ethernet Port", Keys=new[]{
            K("AllowDirectTranslatedP2P", VmcxValueType.Boolean, "False"),
            K("AllowPacketDirect",        VmcxValueType.Boolean, "False"),
            K("ChannelInstanceGuid",      VmcxValueType.String,  "$B"),
            K("ClusterMonitored",         VmcxValueType.Boolean, "True"),
            K("Connection/AltPortName",      VmcxValueType.String,  ""),
            K("Connection/AltSwitchName",    VmcxValueType.String,  ""),
            K("Connection/AuthorizationScope", VmcxValueType.String, ""),
            K("Connection/ChimneyOffloadWeight", VmcxValueType.Integer, "0"),
            K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/DisplayName", VmcxValueType.String, ""),
            K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Flags", VmcxValueType.Integer, "0"),
            K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Setting_$S/Data", VmcxValueType.Raw, "AAIAAGQAAAAAAAAAAQAAAAAAAAAAAAAAQAAAAEBCDwABAQAAEAAAAAEAAAAAAAAAAAAAAAMAAAABAAAA"),
            K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Setting_$S/Version", VmcxValueType.Integer, "1280"),
            K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Settings/Id", VmcxValueType.String, "$S"),
            K("Connection/Features/Id",      VmcxValueType.String,  "C885BFD1-ABB7-418F-8163-9F379C9F7166"),
            K("Connection/PoolId",           VmcxValueType.String,  ""),
            K("Connection/PreventIPSpoofing", VmcxValueType.Boolean, "False"),
            K("Connection/TestReplicaPoolId", VmcxValueType.String, ""),
            K("Connection/TestReplicaSwitchName", VmcxValueType.String, ""),
            K("DeviceNamingEnabled",      VmcxValueType.Boolean, "False"),
            K("FriendlyName",             VmcxValueType.String,  "Network Adapter"),
            K("InterruptModerationDisabled", VmcxValueType.Boolean, "False"),
            K("IsConnected",              VmcxValueType.Boolean, "False"),
            K("MacAddress",               VmcxValueType.String,  "$MAC"),
            K("MacAddressIsStatic",       VmcxValueType.Boolean, "False"),
            K("MediaType",                VmcxValueType.Integer, "0"),
            K("NumaAwarePlacement",       VmcxValueType.Boolean, "False"),
            K("PortName",                 VmcxValueType.String,  ""),
            K("SwitchName",               VmcxValueType.String,  ""),
            K("VDEVVersion",              VmcxValueType.Integer, "768"),
            K("VpciInstanceGuid",         VmcxValueType.String,  "$B"),
        }}},
    };

    /// <summary>设备别名 → 类型 GUID(CLI 友好)。</summary>
    public static string ResolveTypeAlias(string s){ switch((s??"").ToLowerInvariant()){
        case "scsi": return "d422512d-2bf2-4752-809d-7b82b5fcb1b4";
        case "nic": case "net": return "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778";
        case "dda": case "vpci": return "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c";
        case "gpu": case "gpupv": return "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61";
        default: return s; } }

    /// <summary>把键路径归类到子系统。</summary>
    public static VmcxSubsystem Subsystem(string path) {
        if (string.IsNullOrEmpty(path)) return VmcxSubsystem.Other;
        if (path.StartsWith("/configuration/properties/")) return VmcxSubsystem.Properties;
        if (path.StartsWith("/configuration/settings/")) return VmcxSubsystem.Settings;
        if (path.StartsWith("/configuration/global_settings/")) return VmcxSubsystem.GlobalSettings;
        if (path.StartsWith("/configuration/manifest/")) return VmcxSubsystem.Manifest;
        if (Regex.IsMatch(path, @"^/configuration/_[0-9a-fA-F-]{36}_")) return VmcxSubsystem.Device;
        return VmcxSubsystem.Other;
    }

    /// <summary>互斥规则:两个布尔键不能同时为真(WMI 会强制;直接改 vmcx 须自查,否则 WMI 会锁死)。</summary>
    public sealed class MutualExclusion { public string KeyA; public string KeyB; public string Reason; }
    // 已确证的互斥(实测 WMI 拒绝)。完整约束集由 vmms 在启动时校验(startcheck 是终判据);此处收录已验证的高频规则,供保存前预警。
    public static readonly List<MutualExclusion> Exclusions = new List<MutualExclusion> {
        new MutualExclusion {
            KeyA="/configuration/settings/memory/bank/dynamic_memory_enabled",
            KeyB="/configuration/settings/vnuma/enabled",
            Reason="动态内存 与 虚拟 NUMA 不能同时启用" },
        new MutualExclusion {
            KeyA="/configuration/settings/memory/bank/dynamic_memory_enabled",
            KeyB="/configuration/settings/processors/nested_virtualization/enabled",
            Reason="动态内存 与 嵌套虚拟化 不能同时启用" },
    };

    /// <summary>查互斥冲突(给定一组当前为真的布尔键路径)。返回违反的规则。</summary>
    public static List<MutualExclusion> CheckExclusions(HashSet<string> trueBoolKeys) {
        var hit = new List<MutualExclusion>();
        foreach (var ex in Exclusions)
            if (trueBoolKeys.Contains(ex.KeyA) && trueBoolKeys.Contains(ex.KeyB)) hit.Add(ex);
        return hit;
    }

    /// <summary>从枚举结果解析设备清单(manifest vdev)。</summary>
    public static List<VmcxDevice> Devices(List<VmcxNode> nodes) {
        var byNum = new SortedDictionary<int, VmcxDevice>();
        foreach (var n in nodes) {
            if (!n.IsValue) continue;
            var m = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/(device|instance|name)$");
            if (!m.Success) continue;
            int num = int.Parse(m.Groups[1].Value);
            if (!byNum.ContainsKey(num)) byNum[num] = new VmcxDevice { VdevNumber = num };
            var d = byNum[num];
            string v = n.Value ?? "";
            if (m.Groups[2].Value == "device") d.DeviceType = v.Trim('{','}');
            else if (m.Groups[2].Value == "instance") d.Instance = v.Trim('{','}');
            else d.Name = v;
        }
        var list = new List<VmcxDevice>();
        foreach (var d in byNum.Values) {
            d.TypeFriendly = (d.DeviceType != null && DeviceTypes.ContainsKey(d.DeviceType)) ? DeviceTypes[d.DeviceType] : (d.Name ?? "(未知)");
            list.Add(d);
        }
        return list;
    }

    /// <summary>GPU-PV(GPU Partition)设备类型 GUID。</summary>
    public const string GpuPartitionType = "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61";

    /// <summary>一个 GPU-PV(GPU Partition)设备。HostResource="" 表示运行时从可分区 GPU 池自动匹配(未钉死具体物理卡);
    /// 非空则钉死了某块物理 GPU 的路径(\\?\PCI#...\GPUPARAV),宿主换卡/重启后该路径可能失配致 VM 起不来。</summary>
    public sealed class VmcxGpuPv {
        public int    VdevNumber;
        public string Instance;      // manifest 实例 GUID(= 数据节点 /_<instance>_)
        public string HostResource;  // 钉死的物理 GPU 路径;"" = 自动池匹配
    }

    /// <summary>解析所有 GPU-PV(GPU Partition)设备及其 HostResource(钉死的物理 GPU 路径)。
    /// GPU-PV 的 HostResource 是设备数据节点的直接子键 <c>/_&lt;inst&gt;_/HostResource</c>(单数,区别于 DDA 的 HostResources/HostResource/Instance)。</summary>
    public static List<VmcxGpuPv> GpuPvDevices(IEnumerable<VmcxNode> nodes) {
        var instByNum = new SortedDictionary<int,string>();
        var typeByNum = new Dictionary<int,string>();
        var hostRes   = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); // 设备节点GUID → HostResource
        foreach (var n in nodes) {
            if (!n.IsValue) continue;
            var mi = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/instance$");
            if (mi.Success) instByNum[int.Parse(mi.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
            var mt = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/device$");
            if (mt.Success) typeByNum[int.Parse(mt.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
            var mh = Regex.Match(n.Path, @"^/configuration/_([0-9a-fA-F-]{36})_/HostResource$");
            if (mh.Success) hostRes[mh.Groups[1].Value.ToLowerInvariant()] = n.Value ?? "";
        }
        var list = new List<VmcxGpuPv>();
        foreach (var kv in instByNum) {
            string t; if (!typeByNum.TryGetValue(kv.Key, out t) || t != GpuPartitionType) continue;
            string hr; hostRes.TryGetValue(kv.Value, out hr);
            list.Add(new VmcxGpuPv { VdevNumber = kv.Key, Instance = kv.Value, HostResource = hr ?? "" });
        }
        return list;
    }

    // ===== 字段 schema(key→type→default,实测自真机 dump,嵌入 key_schema.tsv)=====
    /// <summary>某键的实测信息。Known=false 表 schema 未收录该键。</summary>
    public struct KeyInfo { public string Type; public string Default; public bool Known; }

    static Dictionary<string,KeyInfo> _schema;

    /// <summary>归一化键路径(设备 GUID / 编号 → 占位),用于匹配 schema 模板。</summary>
    public static string NormalizeKey(string path) {
        if (path == null) return "";
        path = Regex.Replace(path, @"/_[0-9a-fA-F-]{36}_", "/_<dev>_");
        path = Regex.Replace(path, @"Feature_[0-9A-Fa-f-]{36}", "Feature_<g>");
        path = Regex.Replace(path, @"Setting_[0-9A-Fa-f-]{36}", "Setting_<g>");
        path = Regex.Replace(path, @"/vdev\d+", "/vdev<n>");
        path = Regex.Replace(path, @"/(device|drive|controller|bank|adapter|channel|port|node|ethernet_card)(\d+)", "/$1<n>");
        return path;
    }

    static void EnsureSchema() {
        if (_schema != null) return;
        var d = new Dictionary<string,KeyInfo>(StringComparer.OrdinalIgnoreCase);
        try {
            var asm = typeof(VmcxSchema).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("key_schema.tsv", StringComparison.OrdinalIgnoreCase));
            if (res != null)
                using (var s = asm.GetManifestResourceStream(res))
                using (var r = new StreamReader(s)) {
                    string line;
                    while ((line = r.ReadLine()) != null) {
                        if (line.Length == 0 || line[0] == '#' || line.StartsWith("key_pattern")) continue;
                        var p = line.Split('\t');
                        if (p.Length >= 2) d[p[0]] = new KeyInfo { Type = p[1], Default = p.Length > 2 ? p[2] : "", Known = true };
                    }
                }
        } catch { }
        _schema = d;
    }

    /// <summary>查某键的实测类型 / 默认值(按归一化模式匹配 key_schema.tsv)。未收录返回 Known=false。</summary>
    public static KeyInfo GetKeyInfo(string path) {
        EnsureSchema();
        KeyInfo info;
        return _schema.TryGetValue(NormalizeKey(path), out info) ? info : new KeyInfo { Known = false };
    }

    /// <summary>schema 收录的字段模式总数。</summary>
    public static int KeySchemaCount() { EnsureSchema(); return _schema.Count; }
}
}
