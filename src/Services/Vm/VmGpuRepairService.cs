using System.IO;
using ExHyperV.Tools;
using ExHyperV.Vmcx;

namespace ExHyperV.Services
{
    /// <summary>
    /// 失效 GPU-PV(GPU Partition 钉死的物理 GPU 路径在当前主机失配)的检测与修复。
    ///
    /// 关键事实(真机实测):此类记录在 WMI 层完全隐形 —— Get-VMGpuPartitionAdapter 与裸 WMI
    /// Msvm_GpuPartitionSettingData 均返回 0,Remove-VMGpuPartitionAdapter 报"找不到适配器"。
    /// 因此官方 cmdlet/WMI 删不掉它,必须走 .vmcx 引擎(ExHyperV.Vmcx.VmcxStore)直接编辑单个 .vmcx。
    /// 单个 .vmcx 被 vmms 以 FILE_SHARE_READ|WRITE 协作共享打开:引擎可就地写,改完 Start-VM 即生效,无需停 vmms。
    ///
    /// 失配判定走【完整路径串精确比对】(不是只看"池里有没有这张卡"):GPU-PV 钉死的是某块卡的
    /// 完整 GPUPARAV 路径(含 PCIe 位置段),宿主重启/重插后即使同一张卡仍在,位置段也可能变 → 路径失配 → 起不来。
    /// 失配后再比对硬件标识(VEN/DEV/SUBSYS/REV)区分:
    ///   · 同标识的卡仍在池里(只是路径变了)→ 可【重指】到新路径,保住 GPU 加速;
    ///   · 池里无同标识的卡(卡真的不在了)→ 只能【清除】这条 GPU-PV(VM 可开机,无 GPU)。
    /// </summary>
    public static class VmGpuRepairService
    {
        /// <summary>一条失配的 GPU-PV。RebindPath != null 表示"同一张卡仍在主机、只是路径变了",其值为该卡当前的可分区路径;
        /// RebindPath == null 表示"该卡在主机已不存在"。</summary>
        public sealed record StaleGpuPartition(int VdevNumber, string Instance, string HostResource, string? RebindPath);

        // VMComputerSystemState: 2=Running, 3=Off
        private const int State_Running = 2;

        /// <summary>定位注册 VM 的单个 .vmcx 路径(WMI 取 ConfigurationDataRoot + GUID 拼接,容错若干布局)。
        /// 返回 (vmcx 路径, VM GUID, EnabledState);找不到时 Path 为 null。</summary>
        public static async Task<(string? Path, string? Guid, int State)> ResolveVmcxAsync(string vmName)
        {
            var resp = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot, VirtualSystemIdentifier FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => (
                    Root: obj["ConfigurationDataRoot"]?.ToString() ?? string.Empty,
                    Guid: obj["VirtualSystemIdentifier"]?.ToString() ?? string.Empty));
            if (!resp.HasData || string.IsNullOrEmpty(resp.Data.Guid)) return (null, null, -1);

            string guid = resp.Data.Guid;
            string root = resp.Data.Root ?? string.Empty;

            var stateResp = await WmiApi.QueryFirstAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE Name = '{guid}'",
                obj => System.Convert.ToInt32(obj["EnabledState"] ?? 0));
            int state = stateResp.HasData ? stateResp.Data : -1;

            foreach (var cand in new[]
            {
                Path.Combine(root, "Virtual Machines", guid + ".vmcx"),
                Path.Combine(root, guid + ".vmcx"),
            })
            {
                if (File.Exists(cand)) return (cand, guid, state);
            }
            return (null, guid, state);
        }

        /// <summary>取主机可分区 GPU 池(Msvm_PartitionableGpu.Name 完整路径列表)。null = 查询失败(此时不做失配判定,避免误删)。</summary>
        private static async Task<List<string>?> GetPartitionableGpuPoolAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_PartitionableGpu",
                obj => obj["Name"]?.ToString() ?? string.Empty);
            if (!resp.HasData) return null;
            return resp.Data.Where(n => !string.IsNullOrEmpty(n)).ToList();
        }

        /// <summary>从 GPUPARAV 路径提取硬件标识段(VEN_xxxx&DEV_xxxx&SUBSYS_xxxx&REV_xx),用于区分"同卡换路径"与"卡已不在"。
        /// 路径形如 \\?\PCI#&lt;标识&gt;#&lt;PCIe位置&gt;#{接口GUID}\GPUPARAV,标识是第一个 '#' 与第二个 '#' 之间那段。</summary>
        private static string GpuIdentity(string hostResource)
        {
            if (string.IsNullOrEmpty(hostResource)) return string.Empty;
            var parts = hostResource.Split('#');
            return (parts.Length >= 2 ? parts[1] : hostResource).ToUpperInvariant();
        }

        /// <summary>检测某注册 VM 的失配 GPU-PV:用引擎读单个 .vmcx,对每条钉死了物理路径的 GPU Partition,
        /// 用【完整路径串】比对可分区 GPU 池;不在池里即失配,再按硬件标识判断能否重指。
        /// (WMI 看不见这些记录,必须读文件。)HostResource 为空=运行时自动池匹配,不算失配。
        /// 池查询失败时返回空列表(保守,不误判)。</summary>
        public static async Task<List<StaleGpuPartition>> FindStaleGpuPartitionsAsync(string vmName)
        {
            var (vmcx, _, _) = await ResolveVmcxAsync(vmName);
            if (vmcx == null) return new List<StaleGpuPartition>();

            var poolList = await GetPartitionableGpuPoolAsync();
            if (poolList == null) return new List<StaleGpuPartition>();
            var poolSet = new HashSet<string>(poolList, StringComparer.OrdinalIgnoreCase);

            return await Task.Run(() =>
            {
                var result = new List<StaleGpuPartition>();
                List<VmcxSchema.VmcxGpuPv> pvs;
                using (var s = VmcxStore.Open(vmcx))
                    pvs = VmcxSchema.GpuPvDevices(s.Enumerate());

                foreach (var p in pvs)
                {
                    if (string.IsNullOrEmpty(p.HostResource)) continue;     // 未钉死,自动池匹配,不算失配
                    if (poolSet.Contains(p.HostResource)) continue;         // 完整路径精确命中 = 健康
                    // 路径串对不上:核对硬件标识,看同一张卡是否仍在池里(只是路径变了)
                    string id = GpuIdentity(p.HostResource);
                    string? rebind = poolList.FirstOrDefault(x => GpuIdentity(x) == id);
                    result.Add(new StaleGpuPartition(p.VdevNumber, p.Instance, p.HostResource, rebind));
                }
                return result;
            });
        }

        /// <summary>修复指定的失配 GPU-PV:能重指的(同卡换路径)更新 HostResource 到新路径;不能的(卡已不在)删除该设备。
        /// 全部走引擎就地改单个 .vmcx,改完复校结构。VM 须关机;无需停 vmms,改完 Start-VM 即生效。
        /// 返回 (成功, 信息, 重指数, 删除数)。</summary>
        public static async Task<(bool Success, string Message, int Rebound, int Removed)> RepairAsync(
            string vmName, IEnumerable<StaleGpuPartition> targets)
        {
            var list = targets?.ToList() ?? new List<StaleGpuPartition>();
            if (list.Count == 0) return (true, string.Empty, 0, 0);

            var (vmcx, _, state) = await ResolveVmcxAsync(vmName);
            if (vmcx == null) return (false, Properties.Resources.GpuRepair_VmcxNotFound, 0, 0);
            if (state == State_Running) return (false, Properties.Resources.GpuRepair_VmRunning, 0, 0);

            return await Task.Run(() =>
            {
                int rebound = 0, removed = 0;
                try
                {
                    using (var s = VmcxStore.Open(vmcx))
                    {
                        // 先重指(只改值、不动结构),再删除(RemoveDevice 会重排 manifest 编号)
                        var nodes = s.Enumerate();
                        foreach (var t in list.Where(t => !string.IsNullOrEmpty(t.RebindPath)))
                        {
                            string? key = FindHostResourceKey(nodes, t.Instance);
                            if (key == null) continue;
                            using (var w = s.BeginWrite()) { w.SetString(key, t.RebindPath!); w.Commit(); }
                            rebound++;
                        }
                        foreach (var t in list.Where(t => string.IsNullOrEmpty(t.RebindPath)))
                        {
                            s.RemoveDevice(t.Instance);
                            removed++;
                        }
                        var issues = s.ValidateManifest();
                        if (issues.Count > 0)
                            return (false, Properties.Resources.GpuRepair_ValidationFailed + string.Join("; ", issues), rebound, removed);
                    }
                    return (true, string.Empty, rebound, removed);
                }
                catch (System.Exception ex)
                {
                    return (false, Properties.Resources.GpuRepair_RepairFailed + ex.Message, rebound, removed);
                }
            });
        }

        /// <summary>在枚举结果里找某实例 GPU-PV 的 HostResource 键的精确路径(避免大小写假设)。</summary>
        private static string? FindHostResourceKey(List<VmcxNode> nodes, string instance)
        {
            string suffix = "_" + instance + "_/HostResource";
            foreach (var n in nodes)
                if (n.IsValue
                    && n.Path.StartsWith("/configuration/_", StringComparison.OrdinalIgnoreCase)
                    && n.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return n.Path;
            return null;
        }
    }
}
