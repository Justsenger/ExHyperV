using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>
    /// 配置虚拟机的高位 MMIO 间隙（GPU-PV / DDA 直通所需）。
    ///
    /// 探测宿主物理地址上限的方式：故意把 MMIO 区域设到一个明显超限的位置并尝试启动，
    /// Hyper-V 在引导前的校验阶段拒绝启动，作业错误信息里回报“受支持的上限”（如 0x0000100000000000）。
    /// 解析该上限即可，全程 WMI、无 PowerShell，语言无关（抓消息里全部 0x 十六进制取最小值）。
    ///
    /// 相比“查 VID 属性”：探测拿到的是 hypervisor 建分区那一刻的真实判据，嵌套下子分区被削减的
    /// 位宽、ARM64 上 hvaa64 实际配给的位宽都能拿准，而属性查询只报根分区自己的位宽（嵌套下高报）。
    /// 探测结果按进程缓存，只认第一次测得的值，后续复用不再重探。
    /// </summary>
    public static class VmMmioService
    {
        // 探测用的超限区域：top = 2^52 字节。
        // 经实测，top 在 (2^52, 2^53) 之间会触发字段回绕而“意外启动”，2^52 是可靠干净失败的最大值；
        // 它高于任何物理地址 ≤51 位的宿主（涵盖绝大多数 x86-64 与全部 ARM64 目标）。
        // 单位为 MB：2^52 字节 = 2^32 MB。
        private const ulong ProbeBaseMb = 4294966272UL;   // 2^32 - 1024
        private const ulong ProbeSizeMb = 1024UL;

        // 解析失败时的回退上限（MB），保证 VM 仍可启动且不残留探测值。
        private const ulong FallbackCeilingMb = 34816UL;

        private const ulong BytesPerMb = 1024UL * 1024UL;

        // 解析结果的合理性区间（字节）：架构上限恒为 2^N，落在 [2^34, 2^52] 之间视为可信。
        private const ulong MinSaneCeilingBytes = 1UL << 34;
        private const ulong MaxSaneCeilingBytes = 1UL << 52;

        // RequestStateChange 的目标状态
        private const ushort StateEnabled = 2;   // 开机
        private const ushort StateDisabled = 3;  // 关机（强制下电）

        // 宿主 MMIO 上限（MB）缓存：只认第一次测得的结果，进程内复用，不再重探。
        private static ulong? _cachedCeilingMb;

        /// <summary>
        /// 探测宿主 MMIO 上限并写入最优的 MMIO 间隙配置。
        /// </summary>
        /// <returns>最终设置写入成功返回 true。</returns>
        public static async Task<bool> ConfigureMmioAsync(string vmName)
        {
            try
            {
                await EnsureCeilingAsync(vmName);

                var plan = ComputeMmioPlan();
                if (plan is null)
                {
                    Debug.WriteLine("[VmMmio] 无法确定宿主 MMIO 上限，跳过 MMIO 配置。");
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
        /// 按已缓存的宿主 MMIO 上限计算最优间隙：base = 上限/2、
        /// highSize = min(上限 - base - 1GB, 128GB)、lowSize = 1GB。
        /// 尚未探测（缓存为空）时返回 null——调用方（DDA/GPU-PV 的“间隙够不够大”预检）据此回退。
        /// </summary>
        public static MmioPlan? ComputeMmioPlan()
        {
            if (_cachedCeilingMb is not ulong ceilingMb || ceilingMb == 0) return null;
            ulong finalBase = ceilingMb / 2;
            ulong remaining = ceilingMb - finalBase - 1024;
            ulong finalHighSize = Math.Min(remaining, 131072UL);
            return new MmioPlan(finalBase, finalHighSize, 1024UL);
        }

        /// <summary>
        /// 确保宿主 MMIO 上限已缓存。只认第一次测得的结果：配置文件里有就直接用、永不重探；
        /// 没有才 boot-probe，测得即写盘持久化。探测失败仅本进程用回退值（不写盘，下次重探）。
        /// </summary>
        private static async Task EnsureCeilingAsync(string vmName)
        {
            if (_cachedCeilingMb is not null) return;

            if (SettingsService.GetMmioCeilingMb() is ulong saved && saved > 0)
            {
                _cachedCeilingMb = saved;
                return;
            }

            ulong ceilingMb = await QueryHostMmioCeilingMbAsync(vmName);
            if (ceilingMb > 0)
            {
                _cachedCeilingMb = ceilingMb;
                SettingsService.SaveMmioCeilingMb(ceilingMb);   // 首次测得即持久化，此后不再启 VM 探测
            }
            else
            {
                _cachedCeilingMb = FallbackCeilingMb;            // 探测失败：本进程用回退值兜底，不持久化
            }
        }

        /// <summary>
        /// 探测宿主支持的高位 MMIO 上限（MB）。
        /// 设一个超限区域并尝试启动，启动被拒时从错误信息解析上限。返回 0 表示无法确定。
        /// 注意：本方法会临时把 VM 的 MMIO 改成探测值，调用方随后写入最终配置覆盖它。
        /// </summary>
        private static async Task<ulong> QueryHostMmioCeilingMbAsync(string vmName)
        {
            // 1. 写入超限 MMIO（沿用 ModifySystemSettings 默认路径）
            var setResp = await WmiApi.WithObjectAsync(
                wql: RealizedSettingsWql(vmName),
                modifier: obj =>
                {
                    obj["HighMmioGapBase"] = ProbeBaseMb;
                    obj["HighMmioGapSize"] = ProbeSizeMb;
                });
            if (!setResp.Success) return 0;

            // 2. 尝试启动 —— 预期失败，作业错误信息回报上限
            var startResp = await WmiApi.InvokeAsync(
                wql: ComputerSystemWql(vmName),
                methodName: "RequestStateChange",
                setParams: p => p["RequestedState"] = StateEnabled);

            // 3. 意外启动成功（探测值未超限，极罕见）：停机并放弃解析，由调用方回退
            if (startResp.Success)
            {
                await StopVmAsync(vmName);
                return 0;
            }

            // 4. 正常失败路径：VM 在 MMIO 校验阶段被拒、并未引导（State 仍为 Off），从错误信息解析上限
            ulong ceilingBytes = ParseCeilingBytes(startResp.Error);
            return ceilingBytes == 0 ? 0 : ceilingBytes / BytesPerMb;
        }

        /// <summary>
        /// 从启动失败的错误信息里解析受支持的上限（字节）。
        /// 语言无关：抓取消息中全部 0x 十六进制取最小值（探测值必然 &gt; 真实上限，故最小者即上限）。
        /// 再做合理性校验：必须是 2 的幂且落在 [2^34, 2^52]。无法确定返回 0。
        /// </summary>
        private static ulong ParseCeilingBytes(string? message)
        {
            if (string.IsNullOrEmpty(message)) return 0;

            ulong min = ulong.MaxValue;
            foreach (Match m in Regex.Matches(message, "0x([0-9A-Fa-f]+)"))
            {
                if (ulong.TryParse(m.Groups[1].Value, NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out ulong val) && val > 0 && val < min)
                {
                    min = val;
                }
            }
            if (min == ulong.MaxValue) return 0;

            bool isPowerOfTwo = (min & (min - 1)) == 0;
            if (!isPowerOfTwo) return 0;
            if (min < MinSaneCeilingBytes || min > MaxSaneCeilingBytes) return 0;

            return min;
        }

        /// <summary>强制关闭虚拟机（仅作防御性收尾，正常探测路径下 VM 并未启动）。</summary>
        private static async Task StopVmAsync(string vmName)
        {
            await WmiApi.InvokeAsync(
                wql: ComputerSystemWql(vmName),
                methodName: "RequestStateChange",
                setParams: p => p["RequestedState"] = StateDisabled);
        }

        private static string RealizedSettingsWql(string vmName) =>
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

        private static string ComputerSystemWql(string vmName) =>
            $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";
    }
}
