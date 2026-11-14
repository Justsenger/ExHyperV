using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>读取和配置虚拟机 MMIO 地址空间，并按需通过启停目标虚拟机选择可用的宿主上限。</summary>
    /// <remarks>ConfigureMmioAsync 的首次探测会改写配置、反复启动并强制关闭目标；结果保存到主机共享配置并在进程内缓存。ComputeMmioPlan 只使用已有结果，不发起探测。</remarks>
    public static partial class VmMmioService
    {
        /// <summary>判断当前状态是否允许手动编辑 MMIO 配置。</summary>
        /// <param name="isRunning">虚拟机当前是否处于活动状态。</param>
        /// <returns>虚拟机未运行时为 true。</returns>
        internal static bool CanEdit(bool isRunning) => !isRunning;

    /// <summary>评估 MMIO 配置的修改条件。</summary>
    /// <param name="state">查询得到的虚拟机电源状态。</param>
    /// <returns>是否允许修改、原因及需要的电源状态。</returns>
    public static VmModificationCondition GetModificationCondition(VmState state) => VmModificationCondition.PoweredOff(state);

        /// <summary>读取虚拟机 MMIO 基址和间隙大小，单位为 MiB。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>成功时返回设置；目标不存在为 NotFound，读取失败保留错误；可空属性表示主机未提供该属性。</returns>
        public static async Task<ApiResponse<VmMmioSettings>> GetSettingsAsync(Guid vmId)
        {
            if (vmId == Guid.Empty)
                return ApiResponse<VmMmioSettings>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var response = await WmiApi.QueryFirstAsync(
                RealizedSettingsWql(vmId),
                obj => new VmMmioSettings
                {
                    LowSizeMiB = obj.TryGet<ulong>("LowMmioGapSize"),
                    HighSizeMiB = obj.TryGet<ulong>("HighMmioGapSize"),
                    HighBaseMiB = obj.TryGet<ulong>("HighMmioGapBase")
                });

            return response.IsEmpty ? ApiResponse<VmMmioSettings>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound) : response;
        }

        /// <summary>核对虚拟机未运行后，只写入指定的一个 MMIO 属性。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="settings">所选字段的新值，单位 MiB；要修改的字段须填写非空值。</param>
        /// <param name="propertyName">要写入的 VmMmioSettings 属性名：LowSizeMiB、HighSizeMiB 或 HighBaseMiB。例如修改高位 MMIO 起始地址时传入 <c>nameof(VmMmioSettings.HighBaseMiB)</c>，只读取 settings 中该属性的值。</param>
        /// <returns>WMI 修改结果；无效属性名或执行失败通过错误响应报告。</returns>
        public static async Task<ApiResponse> SetSettingAsync(Guid vmId, VmMmioSettings settings, string propertyName)
        {
            if (vmId == Guid.Empty || settings == null)
                return ApiResponse.Fail("虚拟机 GUID 和 MMIO 配置不能为空。", kind: ApiErrorKind.InvalidArgument);
            if (propertyName is not (nameof(VmMmioSettings.LowSizeMiB) or nameof(VmMmioSettings.HighSizeMiB) or nameof(VmMmioSettings.HighBaseMiB)))
                return ApiResponse.Fail("不支持指定的 MMIO 属性。", kind: ApiErrorKind.InvalidArgument);
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData)
                return ApiResponse.Fail(state.Success ? Properties.Resources.Error_Net_VmNotFound : state.Error,
                    state.Code, state.ErrorSource, state.Exception, state.Success ? ApiErrorKind.NotFound : state.ErrorKind);
            var condition = GetModificationCondition(state.Data!.State);
            if (!condition.CanModify) return condition.ToResponse();
            return await WmiApi.WithObjectAsync(
                wql: RealizedSettingsWql(vmId),
                modifier: obj =>
                {
                    switch (propertyName)
                    {
                        case nameof(VmMmioSettings.LowSizeMiB):
                            obj.TrySet("LowMmioGapSize", settings.LowSizeMiB);
                            break;
                        case nameof(VmMmioSettings.HighSizeMiB):
                            obj.TrySet("HighMmioGapSize", settings.HighSizeMiB);
                            break;
                        case nameof(VmMmioSettings.HighBaseMiB):
                            obj.TrySet("HighMmioGapBase", settings.HighBaseMiB);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported MMIO setting");
                    }
                });
        }

        internal static async Task<VmMmioSettings?> GetSettingsAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return null;
            var response = await GetSettingsAsync(identity.Data);
            return response.HasData ? response.Data : null;
        }

        internal static async Task<ApiResponse> SetSettingAsync(string vmName, VmMmioSettings settings, string propertyName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind);
            return await SetSettingAsync(identity.Data, settings, propertyName);
        }

        internal static async Task<bool> ConfigureMmioAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return false;
            return (await ConfigureMmioAsync(identity.Data)).Success;
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
        /// <summary>高位 MMIO 间隙目标大小，262144 MiB，即 256 GiB。</summary>
        internal const ulong DefaultHighSizeMiB = 262144UL;

        // RequestStateChange 的目标状态
        private const ushort StateEnabled = 2;   // 开机
        private const ushort StateDisabled = 3;  // 关机（强制下电）

        // 主机 MMIO 上限（MB）缓存：只认第一次测得的结果，进程内复用，不再重探。
        private static ulong? _cachedCeilingMb;
        private static readonly SemaphoreSlim CeilingProbeLock = new(1, 1);

        /// <summary>复用主机已保存的 MMIO 上限；无结果时通过目标虚拟机逐档启停探测，再写入 MMIO 方案。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>最终配置写入结果；探测或提交失败保留说明。操作可能部分改写配置，首次探测包括强制下电及保存主机级结果。</returns>
        /// <remarks>缺少缓存时会反复启动并强制关闭目标虚拟机以探测上限；结果保存到宿主共享配置。执行前应确认允许这些电源操作。</remarks>
    /// <editentry label="自动配置 MMIO" category="MMIO"/>
        public static async Task<ApiResponse> ConfigureMmioAsync(Guid vmId)
        {
            if (vmId == Guid.Empty)
                return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                ulong ceilingMb = await EnsureCeilingAsync(vmId);

                var p = ComputeMmioPlan(ceilingMb);

                Debug.WriteLine(Properties.Resources.VmMmio_LogFinalResult);
                Debug.WriteLine($" - HighMmioGapBase: {p.BaseMiB}");
                Debug.WriteLine($" - HighMmioGapSize: {p.HighSizeMiB}");
                Debug.WriteLine($" - LowMmioGapSize: {p.LowSizeMiB}");

                var resp = await WmiApi.WithObjectAsync(
                    wql: RealizedSettingsWql(vmId),
                    modifier: obj =>
                    {
                        obj["HighMmioGapBase"] = p.BaseMiB;
                        obj["HighMmioGapSize"] = p.HighSizeMiB;
                        obj["LowMmioGapSize"] = p.LowSizeMiB;
                        obj["GuestControlledCacheTypes"] = true;
                    });

                if (resp.Success) Debug.WriteLine(Properties.Resources.VmMmio_LogConfigApplied);
                return resp;
            }
            catch (OperationOutcomeUnknownException ex) { return ex.Result; }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.VmMmio_LogError, ex.Message));
                return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
            }
        }

        /// <summary>MMIO 间隙方案（单位 MiB）：高位基址、高位大小、低位大小。</summary>
        /// <param name="BaseMiB">高位 MMIO 基址，单位 MiB。</param>
        /// <param name="HighSizeMiB">高位 MMIO 间隙大小，单位 MiB。</param>
        /// <param name="LowSizeMiB">低位 MMIO 间隙大小，单位 MiB。</param>
        internal readonly record struct MmioPlan(ulong BaseMiB, ulong HighSizeMiB, ulong LowSizeMiB);

        /// <summary>根据进程内缓存的宿主 MMIO 上限计算间隙。</summary>
        /// <returns>单位 MiB 的基址和高低间隙方案；没有已知上限时为 null。</returns>
        internal static MmioPlan? ComputeMmioPlan()
        {
            if (_cachedCeilingMb is not ulong ceilingMb || ceilingMb == 0) return null;
            return ComputeMmioPlan(ceilingMb);
        }

        private static MmioPlan ComputeMmioPlan(ulong ceilingMb)
        {
            ulong finalBase = ceilingMb / 2;
            ulong remaining = ceilingMb - finalBase - 1024;
            ulong finalHighSize = Math.Min(remaining, DefaultHighSizeMiB);
            return new MmioPlan(finalBase, finalHighSize, 3584UL);
        }

        /// <summary>
        /// 取得本次配置操作使用的主机 MMIO 上限。配置文件里有就直接使用；
        /// 没有才逐档启动探测，测得即写盘持久化。探测失败仅当前操作使用回退值，后续虚拟机继续探测。
        /// </summary>
        private static async Task<ulong> EnsureCeilingAsync(Guid vmId)
        {
            if (_cachedCeilingMb is ulong cached) return cached;

            await CeilingProbeLock.WaitAsync();
            try
            {
                if (_cachedCeilingMb is ulong cachedAfterWait) return cachedAfterWait;

                if (MmioSettingsStore.GetMmioCeilingMb() is ulong saved && saved > 0)
                {
                    _cachedCeilingMb = saved;
                    return saved;
                }

                ulong ceilingMb = await QueryHostMmioCeilingMbAsync(vmId);
                if (ceilingMb > 0)
                {
                    _cachedCeilingMb = ceilingMb;
                    MmioSettingsStore.SaveMmioCeilingMb(ceilingMb);   // 首次测得即持久化，此后不再启 VM 探测
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
        /// 逐档探测主机支持的高位 MMIO 上限（MiB）。
        /// 每个候选值都临时写入当前虚拟机并尝试启动；第一个启动成功的值即为上限。
        /// 成功后立即强制关机。返回 0 表示所有候选值均无法启动。
        /// 注意：本方法会临时改写 VM 的 MMIO，调用方随后写入最终配置覆盖它。
        /// </summary>
        private static Task<ulong> QueryHostMmioCeilingMbAsync(Guid vmId) => ProbeCeilingAsync(
            ceilingMb => WmiApi.WithObjectAsync(RealizedSettingsWql(vmId), obj =>
            {
                obj["HighMmioGapBase"] = ceilingMb - ProbeSizeMb;
                obj["HighMmioGapSize"] = ProbeSizeMb;
            }),
            state => WmiApi.InvokeAsync(ComputerSystemWql(vmId), "RequestStateChange", p => p["RequestedState"] = state));

        internal static async Task<ulong> ProbeCeilingAsync(Func<ulong, Task<ApiResponse>> configure, Func<ushort, Task<ApiResponse>> power)
        {
            foreach (ulong ceilingMb in ProbeCeilingCandidatesMb)
            {
                Debug.WriteLine($"[VmMmio] 正在探测 MMIO 上限: {ceilingMb} MB");

                var setResp = await configure(ceilingMb);
                OperationOutcomeUnknownException.ThrowIfUnknown(setResp);
                if (!setResp.Success) continue;

                var startResp = await power(StateEnabled);
                OperationOutcomeUnknownException.ThrowIfUnknown(startResp);
                if (!startResp.Success) continue;

                Debug.WriteLine($"[VmMmio] 探测成功，主机 MMIO 上限: {ceilingMb} MB");
                var stop = await power(StateDisabled);
                OperationOutcomeUnknownException.ThrowIfUnknown(stop);
                if (!stop.Success)
                    throw new InvalidOperationException("MMIO 探测启动成功，但无法关闭虚拟机。");

                return ceilingMb;
            }

            return 0;
        }

        private static string RealizedSettingsWql(Guid vmId) =>
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}";

        private static string ComputerSystemWql(Guid vmId) =>
            $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";
    }
}
