#nullable disable
// VmcxSchema — .vmcx 知识层的最小保留集:仅 GPU-PV/DDA 类幽灵设备修复(VmGpuRepairService)所需。
// 编辑器全量知识层(设备类型字典/建设备模板/互斥规则/字段 schema)在独立项目 ExHyperV-Edit,
// 将来扩展编辑能力时从那边整体引入,此处不做超出修复用途的扩展。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ExHyperV.Vmcx {

public static class VmcxSchema {

    /// <summary>功能设备类型:必须有非空数据节点。这些类型的 manifest 条目若数据节点为空=幽灵(致命,VM起不来)。
    /// 补"同类型兄弟全坏时漏判"的缺口——不依赖兄弟有数据,只要属此集合且数据节点空即判幽灵。</summary>
    public static readonly HashSet<string> FunctionalDeviceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", // Synthetic Ethernet Port (NIC)
        "d422512d-2bf2-4752-809d-7b82b5fcb1b4", // Synthetic SCSI Controller
        "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", // Virtual Pci Express Port (DDA)
        "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", // GPU Partition
    };

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
}
}
