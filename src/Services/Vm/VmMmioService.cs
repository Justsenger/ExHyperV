using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>
    /// 配置虚拟机的高位 MMIO 间隙（GPU-PV / DDA 直通所需）。
    ///
    /// 按候选上限从大到小临时设置 MMIO，并反复尝试启动当前虚拟机。
    /// 第一个能够启动的候选值即为平台实际允许的上限；启动成功后立即通过 WMI 强制关机。
    /// 全程使用 WMI，不依赖 PowerShell，也不依赖不同平台返回的错误文本。
    /// 探测结果写入配置文件并按进程缓存，后续复用不再重探。
    /// </summary>
    public static class VmMmioService
    {
        /// <summary>读取虚拟机的 MMIO 地址空间设置；null 字段表示当前 WMI 未提供该属性。</summary>
        public static async Task<VmMmioSettings?> GetSettingsAsync(string vmName)
        {
            var response = await WmiApi.QueryFirstAsync(
                RealizedSettingsWql(vmName),
                obj => new VmMmioSettings
                {
                    LowSizeMb = obj.TryGet<ulong>("LowMmioGapSize"),
                    HighSizeMb = obj.TryGet<ulong>("HighMmioGapSize"),
                    HighBaseMb = obj.TryGet<ulong>("HighMmioGapBase")
                });

            return response.HasData ? response.Data : null;
        }

        /// <summary>只写入用户按下“应用”的那个 MMIO 字段。</summary>
        public static Task<ApiResponse> SetSettingAsync(string vmName, VmMmioSettings settings, string propertyName)
        {
            return WmiApi.WithObjectAsync(
                wql: RealizedSettingsWql(vmName),
                modifier: obj =>
                {
                    switch (propertyName)
                    {
                        case nameof(VmMmioSettings.LowSizeMb):
                            obj.TrySet("LowMmioGapSize", settings.LowSizeMb);
                            break;
                        case nameof(VmMmioSettings.HighSizeMb):
                            obj.TrySet("HighMmioGapSize", settings.HighSizeMb);
                            break;
                        case nameof(VmMmioSettings.HighBaseMb):
                            obj.TrySet("HighMmioGapBase", settings.HighBaseMb);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported MMIO setting");
                    }
                });
        }

        private const ulong ProbeSizeMb = 1024UL;

        // 沿用旧版逐档启停探测顺序，覆盖 50、48、47、46、44、42、40、39、38、37、36 位。
        // 最后一项 34816MB 是旧平台的安全下限。
        private static readonly ulong[] ProbeCeilingCandidatesMb =
        {
            1073741824UL, 268435456UL, 134217728UL, 67108864UL,
            16777216UL, 4194304UL, 1048576UL, 524288UL,
            262144UL, 131072UL, 65536UL, 34816UL
        };

        // 所有候选值均无法启动时的回退上限（MB），保证 VM 不残留探测值。
        private const ulong FallbackCeilingMb = 34816UL;

        // 默认高位 MMIO 间隙大小（MB）= 256G。GPU-PV 与 DDA 共用这个目标（都经 ComputeMmioPlan 检测+配置）；
        // 间隙越大越能降低两者在同一 MMIO gap 里撞车的概率。Hyper-V 对该值有硬上限（实测 262656MB=256.5G），
        // 256G 稳在其下且实测能正常启动。改这一个常量即全项目一致。
        public const ulong DefaultHighSizeMb = 262144UL;

        // RequestStateChange 的目标状态
        private const ushort StateEnabled = 2;   // 开机
        private const ushort StateDisabled = 3;  // 关机（强制下电）

        // 宿主 MMIO 上限（MB）缓存：只认第一次测得的结果，进程内复用，不再重探。
        private static ulong? _cachedCeilingMb;
        private static readonly SemaphoreSlim CeilingProbeLock = new(1, 1);

        /// <summary>
        /// 探测宿主 MMIO 上限并写入最优的 MMIO 间隙配置。
        /// </summary>
        /// <returns>最终设置写入成功返回 true。</returns>
        public static async Task<bool> ConfigureMmioAsync(string vmName)
        {
            try
            {
                ulong ceilingMb = await EnsureCeilingAsync(vmName);

                var p = ComputeMmioPlan(ceilingMb);

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
        /// highSize = min(上限 - base - 1GB, 256GB)、lowSize = 3584MB。
        /// 尚未探测（缓存为空）时返回 null——调用方（DDA/GPU-PV 的“间隙够不够大”预检）据此回退。
        /// </summary>
        public static MmioPlan? ComputeMmioPlan()
        {
            if (_cachedCeilingMb is not ulong ceilingMb || ceilingMb == 0) return null;
            return ComputeMmioPlan(ceilingMb);
        }

        private static MmioPlan ComputeMmioPlan(ulong ceilingMb)
        {
            ulong finalBase = ceilingMb / 2;
            ulong remaining = ceilingMb - finalBase - 1024;
            ulong finalHighSize = Math.Min(remaining, DefaultHighSizeMb);
            return new MmioPlan(finalBase, finalHighSize, 3584UL);
        }

        /// <summary>
        /// 取得本次配置操作使用的宿主 MMIO 上限。配置文件里有就直接使用；
        /// 没有才逐档启动探测，测得即写盘持久化。探测失败仅当前操作使用回退值，后续虚拟机继续探测。
        /// </summary>
        private static async Task<ulong> EnsureCeilingAsync(string vmName)
        {
            if (_cachedCeilingMb is ulong cached) return cached;

            await CeilingProbeLock.WaitAsync();
            try
            {
                if (_cachedCeilingMb is ulong cachedAfterWait) return cachedAfterWait;

                if (SettingsService.GetMmioCeilingMb() is ulong saved && saved > 0)
                {
                    _cachedCeilingMb = saved;
                    return saved;
                }

                ulong ceilingMb = await QueryHostMmioCeilingMbAsync(vmName);
                if (ceilingMb > 0)
                {
                    _cachedCeilingMb = ceilingMb;
                    SettingsService.SaveMmioCeilingMb(ceilingMb);   // 首次测得即持久化，此后不再启 VM 探测
                    return ceilingMb;
                }

                // 探测失败时仅让当前配置操作使用回退值。不要写入进程缓存，
                // 这样后续虚拟机仍有机会重新探测并取得可持久化的真实上限。
                return FallbackCeilingMb;
            }
            finally
            {
                CeilingProbeLock.Release();
            }
        }

        /// <summary>
        /// 逐档探测宿主支持的高位 MMIO 上限（MB）。
        /// 每个候选值都临时写入当前虚拟机并尝试启动；第一个启动成功的值即为上限。
        /// 成功后立即强制关机。返回 0 表示所有候选值均无法启动。
        /// 注意：本方法会临时改写 VM 的 MMIO，调用方随后写入最终配置覆盖它。
        /// </summary>
        private static async Task<ulong> QueryHostMmioCeilingMbAsync(string vmName)
        {
            foreach (ulong ceilingMb in ProbeCeilingCandidatesMb)
            {
                Debug.WriteLine($"[VmMmio] 正在探测 MMIO 上限: {ceilingMb} MB");

                var setResp = await WmiApi.WithObjectAsync(
                    wql: RealizedSettingsWql(vmName),
                    modifier: obj =>
                    {
                        obj["HighMmioGapBase"] = ceilingMb - ProbeSizeMb;
                        obj["HighMmioGapSize"] = ProbeSizeMb;
                    });
                if (!setResp.Success) continue;

                var startResp = await WmiApi.InvokeAsync(
                    wql: ComputerSystemWql(vmName),
                    methodName: "RequestStateChange",
                    setParams: p => p["RequestedState"] = StateEnabled);
                if (!startResp.Success) continue;

                Debug.WriteLine($"[VmMmio] 探测成功，宿主 MMIO 上限: {ceilingMb} MB");
                if (!await StopVmAsync(vmName))
                    throw new InvalidOperationException("MMIO 探测启动成功，但无法关闭虚拟机。");

                return ceilingMb;
            }

            return 0;
        }

        /// <summary>探测到可启动的候选值后，立即通过 WMI 强制关闭虚拟机。</summary>
        private static async Task<bool> StopVmAsync(string vmName)
        {
            var response = await WmiApi.InvokeAsync(
                wql: ComputerSystemWql(vmName),
                methodName: "RequestStateChange",
                setParams: p => p["RequestedState"] = StateDisabled);
            return response.Success;
        }

        private static string RealizedSettingsWql(string vmName) =>
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

        private static string ComputerSystemWql(string vmName) =>
            $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";
    }
}
