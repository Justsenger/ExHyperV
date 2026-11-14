using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>读取宿主 GPU 及分区能力，并配置宿主 GPU 分配策略。</summary>
public static partial class HostGpuService
{

        /// <summary>规范化 GPU 设备 ID：转为大写、移除设备前缀及接口 GUID，并统一路径分隔符。</summary>
        /// <param name="deviceId">设备实例 ID 或可分区 GPU 路径；空白值返回空字符串。</param>
        /// <returns>用于匹配的规范化字符串，不用于直接显示或证明物理卡唯一性。</returns>
        private static string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();

            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);

            int suffixIndex = normalizedId.IndexOf("{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);

            return normalizedId.Replace('\\', '#').TrimEnd('#');
        }


                /// <summary>读取当前在位的 PCI/ACPI 显示适配器，并关联 Hyper-V 可分区资源。</summary>
        /// <returns>GPU 清单或查询错误；专用显存为 null 表示未取得，0 是有效容量。原生设备枚举的部分失败可能表现为缺项或空清单。</returns>
        /// <remarks>GPU 分区要求 Windows build ≥ 17134，并由设备的 IsPartitionable 表示当前是否存在可分区资源。宿主分配限制通过 SetAssignmentRestrictionsRelaxed 配置。</remarks>
        public static async Task<ApiResponse<IReadOnlyList<HostGpuInfo>>> GetHostGpusAsync()
        {
            try { return ApiResponse<IReadOnlyList<HostGpuInfo>>.Ok((await ReadHostGpusAsync()).AsReadOnly()); }
            catch (Exception ex) { return ApiResponse<IReadOnlyList<HostGpuInfo>>.Fail(ex.Message, exception: ex); }
        }


        /// <summary>查询当前存在的显示适配器并补充驱动、显存和可分区资源信息。</summary>
        /// <returns>新的可变 GPU 列表；IsPartitionable 表示是否匹配到可分区资源。</returns>
        /// <remarks>专用显存为 null 表示未取得，0 是有效容量。首次查询可能初始化 PCI ID 名称数据。</remarks>
        internal static async Task<List<HostGpuInfo>> ReadHostGpusAsync()
        {
            var pciInfoProvider = new PciIds();
            await pciInfoProvider.EnsureInitializedAsync();

            var gpuList = new List<HostGpuInfo>();

            // 设备列表 + 完整 InstanceId + 厂商 + 驱动版本：走原生 CfgMgr 的"在位"枚举(Win32Api.GetPresentDisplayAdapters)。
            //    替代偶发数秒慢的 Win32_VideoController(它实探显示驱动、GPU 空闲会被唤醒)；FILTER_PRESENT 天然排除幻影设备。
            //    通过 CM_* 枚举在位 GPU，并从 DEVPKEY 读取 DriverVersion。
            var displayAdapters = await Task.Run(() => Win32Api.GetPresentDisplayAdapters());
            var dedicatedMemory = await Task.Run(DxgiGpuMemory.ReadAll);
            foreach (var (instanceId, name, manu, driverVersion) in displayAdapters)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId, manu);
                gpuList.Add(new HostGpuInfo
                {
                    Name = name,
                    Manu = string.IsNullOrEmpty(manu) ? vendor : manu,
                    InstanceId = instanceId,
                    DriverVersion = driverVersion,
                    Vendor = vendor,
                    MemoryBytes = dedicatedMemory.TryGetValue(instanceId, out ulong? bytes) ? bytes : null
                });
            }

            // Msvm_PartitionableGpu
            var partResp = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_PartitionableGpu",
                obj => obj["Name"]?.ToString() ?? "");

            if (!partResp.Success) throw new InvalidOperationException(partResp.Error, partResp.Exception);
            if (partResp.HasData)
            {
                foreach (var partitionableGpuPath in partResp.Data)
                {
                    if (string.IsNullOrEmpty(partitionableGpuPath)) continue;
                    string normalizedPartitionableGpuPath = NormalizeDeviceId(partitionableGpuPath);
                    var existingGpu = gpuList.FirstOrDefault(g =>
                        NormalizeDeviceId(g.InstanceId) == normalizedPartitionableGpuPath);
                    if (existingGpu != null) existingGpu.PartitionableGpuPath = partitionableGpuPath;
                }
            }

            return gpuList;
        }

        // ── 环境检测 ────────────────────────────────────────────────

        /// <summary>按应用规则检查 Windows 构建号是否达到 GPU 分区功能的版本门槛。</summary>
        /// <returns>构建号至少为 17134 时为 true。</returns>
        /// <param name="buildNumber">Windows 构建号；null 使用本机版本。</param>
        public static bool IsGpuPartitioningVersionSupported(int? buildNumber = null) =>
            (buildNumber ?? Environment.OSVersion.Version.Build) >= 17134;

}
