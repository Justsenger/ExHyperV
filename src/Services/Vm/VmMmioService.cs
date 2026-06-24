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

                var plan = ComputeMmioPlan();
                if (plan is null)
                {
                    Debug.WriteLine("[VmMmio] 无法读取宿主物理地址宽度，跳过 MMIO 配置。");
                    return false;
                }
                var p = plan.Value;

                Debug.WriteLine(Properties.Resources.VmMmio_LogFinalResult);
                Debug.WriteLine($" - HighMmioGapBase: {p.BaseMb}");
                Debug.WriteLine($" - HighMmioGapSize: {p.HighSizeMb}");
                Debug.WriteLine($" - LowMmioGapSize: {p.LowSizeMb}");

                var resp = await WmiApi.WithObjectAsync(
                    wql: RealizedSettingsWql(vmName),
                    modifier: obj =>
                    {
                        obj["HighMmioGapBase"] = p.BaseMb;
                        obj["HighMmioGapSize"] = p.HighSizeMb;
                        obj["LowMmioGapSize"] = p.LowSizeMb;
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

        /// <summary>MMIO 间隙方案（单位 MB）：高位基址、高位大小、低位大小。</summary>
        public readonly record struct MmioPlan(ulong BaseMb, ulong HighSizeMb, ulong LowSizeMb);

        /// <summary>
        /// 按宿主物理地址宽度计算最优 MMIO 间隙：base = 上限/2、
        /// highSize = min(上限 - base - 1GB, 128GB)、lowSize = 1GB。
        /// 读不到上限或位宽不合理时返回 null（调用方据此回退）。
        /// </summary>
        public static MmioPlan? ComputeMmioPlan()
        {
            ulong ceilingMb = QueryHostMmioCeilingMb();
            if (ceilingMb == 0) return null;
            ulong finalBase = ceilingMb / 2;
            ulong remaining = ceilingMb - finalBase - 1024;
            ulong finalHighSize = Math.Min(remaining, 131072UL);
            return new MmioPlan(finalBase, finalHighSize, 1024UL);
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
