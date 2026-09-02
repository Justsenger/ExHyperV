#nullable disable
// 仅保留 GPU-PV 和 DDA 设备修复所需的 .vmcx 类型定义。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ExHyperV.Vmcx {

public static class VmcxSchema {

    /// <summary>数据节点不能为空的功能设备类型。</summary>
    public static readonly HashSet<string> FunctionalDeviceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", // Synthetic Ethernet Port (NIC)
        "d422512d-2bf2-4752-809d-7b82b5fcb1b4", // Synthetic SCSI Controller
        "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", // Virtual Pci Express Port (DDA)
        "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", // GPU Partition
    };

    /// <summary>GPU-PV(GPU Partition)设备类型 GUID。</summary>
    public const string GpuPartitionType = "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61";

    /// <summary>一个 GPU-PV(GPU Partition)设备。HostResource="" 表示运行时从可分区 GPU 池自动匹配(未钉死具体物理卡);
    /// 非空则钉死了某块物理 GPU 的路径(\\?\PCI#...\GPUPARAV),主机换卡/重启后该路径可能失配致 VM 起不来。</summary>
    public sealed class VmcxGpuPv {
        public int    VdevNumber;
        public string Instance;      // manifest 实例 GUID(= 数据节点 /_<instance>_)
        public string HostResource;  // 钉死的物理 GPU 路径;"" = 自动池匹配
        public bool HostResourcePresent;
        public bool InstanceGuidPresent;
        public bool PoolIdPresent;
        public bool VdevVersionPresent;

        // 未钉死物理卡的合法 GPU-PV 可以没有 HostResource，但官方创建的
        // 通用池设备仍包含 InstanceGuid 和 VDEVVersion（当前系统还会写入
        // PoolID，但旧系统可能省略默认值）。保留键是否
        // 存在的信息，避免把仅剩 VDEVVersion 的残缺设备误判为合法通用池设备。
        public bool IsStructurallyComplete =>
            InstanceGuidPresent && VdevVersionPresent;
    }

    /// <summary>解析所有 GPU-PV(GPU Partition)设备及其 HostResource(钉死的物理 GPU 路径)。
    /// GPU-PV 的 HostResource 是设备数据节点的直接子键 <c>/_&lt;inst&gt;_/HostResource</c>(单数,区别于 DDA 的 HostResources/HostResource/Instance)。</summary>
    public static List<VmcxGpuPv> GpuPvDevices(IEnumerable<VmcxNode> nodes) {
        var instByNum = new SortedDictionary<int,string>();
        var typeByNum = new Dictionary<int,string>();
        var hostRes   = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); // 设备节点GUID → HostResource
        var deviceKeys = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) {
            if (!n.IsValue) continue;
            var mi = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/instance$");
            if (mi.Success) instByNum[int.Parse(mi.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
            var mt = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/device$");
            if (mt.Success) typeByNum[int.Parse(mt.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
            var md = Regex.Match(n.Path, @"^/configuration/_([0-9a-fA-F-]{36})_/([^/]+)$");
            if (md.Success) {
                string instance = md.Groups[1].Value.ToLowerInvariant();
                string key = md.Groups[2].Value;
                if (!deviceKeys.TryGetValue(instance, out var keys)) {
                    keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    deviceKeys[instance] = keys;
                }
                keys.Add(key);
                if (key.Equals("HostResource", StringComparison.OrdinalIgnoreCase))
                    hostRes[instance] = n.Value ?? "";
            }
        }
        var list = new List<VmcxGpuPv>();
        foreach (var kv in instByNum) {
            string t; if (!typeByNum.TryGetValue(kv.Key, out t) || t != GpuPartitionType) continue;
            string hr; hostRes.TryGetValue(kv.Value, out hr);
            deviceKeys.TryGetValue(kv.Value, out var keys);
            list.Add(new VmcxGpuPv {
                VdevNumber = kv.Key,
                Instance = kv.Value,
                HostResource = hr ?? "",
                HostResourcePresent = keys?.Contains("HostResource") == true,
                InstanceGuidPresent = keys?.Contains("InstanceGuid") == true,
                PoolIdPresent = keys?.Contains("PoolID") == true,
                VdevVersionPresent = keys?.Contains("VDEVVersion") == true,
            });
        }
        return list;
    }
}
}
