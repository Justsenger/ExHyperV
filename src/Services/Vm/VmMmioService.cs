using System.Diagnostics;
using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>
    /// 配置虚拟机的高位 MMIO 间隙（GPU-PV 直通所需）。
    ///
    /// 获取宿主物理地址上限的方式：直接向 Hyper-V 根分区查询
    /// HvPartitionPropertyPhysicalAddressWidth（VID 属性码 0x60006），拿到宿主 enforced
    /// 物理地址位宽，上限即 2^bits。一次调用、无需启动任何虚拟机、x64/ARM64 同一路径。
    ///
    /// 注：该值是 hvaa64 实际配给分区的宽度，可能小于 CPU 架构上限——
    /// 例如 Snapdragon X Elite 实际 39 位，而 ID_AA64MMFR0.PARange=44 位——
    /// 故不能用 CPUID / PARange 代替。
    /// </summary>
    public static class VmMmioService
    {
        private const ulong BytesPerMb = 1024UL * 1024UL;

        // 合理性区间：架构上限恒为 2^N，位宽落在 [34, 52] 视为可信。
        private const int MinSaneBits = 34;
        private const int MaxSaneBits = 52;

        // VID 根分区句柄（-1）与"物理地址宽度"属性码。
        private const ulong RootPartition = 0xFFFFFFFFFFFFFFFFUL;
        private const ulong HvPartitionPropertyPhysicalAddressWidth = 0x00060006UL;

        [DllImport("vid.dll", SetLastError = true)]
        private static extern int VidGetPartitionProperty(ulong partition, ulong propertyCode, ref ulong value);

        /// <summary>
        /// 读取宿主 MMIO 上限并写入最优的 MMIO 间隙配置。
        /// </summary>
        /// <returns>最终设置写入成功返回 true；无法确定上限或写入失败返回 false。</returns>
        public static async Task<bool> ConfigureMmioAsync(string vmName)
        {
            try
            {
                ulong ceilingMb = QueryHostMmioCeilingMb();
                if (ceilingMb == 0)
                {
                    Debug.WriteLine("[VmMmio] 无法读取宿主物理地址宽度，跳过 MMIO 配置。");
                    return false;
                }

                // 基础地址 = 上限的一半
                ulong finalBase = ceilingMb / 2;
                // 空间大小 = 128GB(131072MB) 与 (上限 - 基础地址 - 1GB) 的较小值
                ulong remaining = ceilingMb - finalBase - 1024;
                ulong finalHighSize = Math.Min(remaining, 131072UL);
                ulong finalLowSize = 1024UL; // 固定 1GB

                Debug.WriteLine(Properties.Resources.VmMmio_LogFinalResult);
                Debug.WriteLine($" - HighMmioGapBase: {finalBase}");
                Debug.WriteLine($" - HighMmioGapSize: {finalHighSize}");
                Debug.WriteLine($" - LowMmioGapSize: {finalLowSize}");

                var resp = await WmiApi.WithObjectAsync(
                    wql: RealizedSettingsWql(vmName),
                    modifier: obj =>
                    {
                        obj["HighMmioGapBase"] = finalBase;
                        obj["HighMmioGapSize"] = finalHighSize;
                        obj["LowMmioGapSize"] = finalLowSize;
                        obj["GuestControlledCacheTypes"] = true;
                    });

                if (resp.Success) Debug.WriteLine(Properties.Resources.VmMmio_LogConfigApplied);
                return resp.Success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.VmMmio_LogError, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 直读宿主 enforced 物理地址宽度，换算 MMIO 上限（MB）。
        /// 查询 Hyper-V 根分区属性 HvPartitionPropertyPhysicalAddressWidth（VID 0x60006），
        /// 无需启动虚拟机，x64/ARM64 通用。读不到或位宽不合理返回 0。
        /// </summary>
        private static ulong QueryHostMmioCeilingMb()
        {
            ulong bits = 0;
            if (VidGetPartitionProperty(RootPartition, HvPartitionPropertyPhysicalAddressWidth, ref bits) == 0)
                return 0;
            if (bits < MinSaneBits || bits > MaxSaneBits)
                return 0;
            return (1UL << (int)bits) / BytesPerMb; // 2^bits 字节 → MB
        }

        private static string RealizedSettingsWql(string vmName) =>
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";
    }
}
