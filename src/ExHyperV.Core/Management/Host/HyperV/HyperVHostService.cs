using System.Management;
using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>检测本机 Hyper-V 环境，并提交 Windows 功能及引导配置变更。</summary>
    public static class HyperVHostService
    {


        /// <summary>检查宿主是否具备已启用的硬件虚拟化扩展。</summary>
        /// <returns>检测为可用时为 true；不满足条件或读取异常时为 false。</returns>
        public static bool IsVirtualizationEnabled()
        {
            try
            {
                // ARM 无 CPUID；该标志不被 hypervisor 掩盖，主机 true / 来宾无嵌套 false。
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                    return VmMonitorModeExtensionsEnabled();

                if (!CpuId.Supported)   // 保持仅 x64 进程使用 CPUID 的支持范围。
                    return IsHypervisorPresent() || VirtualizationFirmwareEnabled();

                if (!CpuId.HypervisorPresent())         // 物理机无 Hyper-V → 读 BIOS 标志
                    return VirtualizationFirmwareEnabled();
                if (CpuId.IsHyperVRootPartition())      // 物理机跑着 Hyper-V（VFE/VMX 被掩盖）→ 已启用
                    return true;
                return CpuId.HardwareVirtualizationExposed();   // 来宾：VMX/SVM 反映嵌套是否透传
            }
            catch { return false; }
        }

        // Win32_Processor.VirtualizationFirmwareEnabled：固件/BIOS 是否启用虚拟化。x64 物理机无 Hyper-V 时用。
        private static bool VirtualizationFirmwareEnabled()
        {
            var response = WmiApi.QueryAsync(
                "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor",
                obj => obj["VirtualizationFirmwareEnabled"] is bool enabled && enabled,
                WmiScope.CimV2).GetAwaiter().GetResult();
            return response.Success && (response.Data?.Any(x => x) ?? false);
        }

        // Win32_Processor.VMMonitorModeExtensions：ARM64 用。主机 true / 来宾无嵌套 false，不被 hypervisor 掩盖。
        private static bool VmMonitorModeExtensionsEnabled()
        {
            var response = WmiApi.QueryAsync(
                "SELECT VMMonitorModeExtensions FROM Win32_Processor",
                obj => obj["VMMonitorModeExtensions"] is bool enabled && enabled,
                WmiScope.CimV2).GetAwaiter().GetResult();
            return response.Success && (response.Data?.Any(x => x) ?? false);
        }

        /// <summary>检测系统是否上报存在监控程序。</summary>
        /// <returns>检测为可用时为 true；不满足条件或读取异常时为 false。</returns>
        public static bool IsHypervisorPresent()
        {
            try
            {
                var response = WmiApi.QueryAsync(
                    "SELECT HypervisorPresent FROM Win32_ComputerSystem",
                    obj => obj["HypervisorPresent"] is bool present && present,
                    WmiScope.CimV2).GetAwaiter().GetResult();
                return response.Success && (response.Data?.Any(x => x) ?? false);
            }
            catch { return false; }
        }

        /// <summary>检查 Device Guard 的可用安全属性中是否包含 DMA 保护。</summary>
        /// <returns>检测为可用时为 true；不满足条件或读取异常时为 false。</returns>
        /// <remarks>以 Windows Device Guard 上报的 DMA 保护能力判断。</remarks>
        public static bool IsIommuEnabled()
        {
            try
            {
                var response = WmiApi.QueryAsync(
                    "SELECT AvailableSecurityProperties FROM Win32_DeviceGuard",
                    obj => obj["AvailableSecurityProperties"] as int[],
                    WmiScope.DeviceGuard).GetAwaiter().GetResult();
                return response.Success &&
                       (response.Data?.Any(props => props?.Contains(3) ?? false) ?? false);
            }
            catch { return false; }
        }

        /// <summary>读取宿主 VMMS 服务的运行状态代码。</summary>
        /// <returns>0：未找到或无法查询；1：Running；2：服务存在但未运行。详细诊断见 GetHyperVDiagnosticsAsync。</returns>
        public static int GetVmmsStatus()
        {
            var result = HyperVEnvironmentInspector.GetVmmsAsync().GetAwaiter().GetResult();
            return result.Service == null || result.Check.State == HyperVCheckState.Unknown ? 0
                : string.Equals(result.Service.State, "Running", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        /// <summary>检测本机 Hyper-V WMI 管理服务是否可连接且存在。</summary>
        /// <returns>检测为可用时为 true；未通过或超时为 false。等待有界，详细错误由 GetHyperVDiagnosticsAsync 提供。</returns>
        public static bool IsHyperVWmiNamespaceAvailable()
            => HyperVEnvironmentInspector.GetWmiAsync().GetAwaiter().GetResult().State == HyperVCheckState.Passed;

        private static bool? IsHypervisorActive()
        {
            try
            {
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64 || !CpuId.Supported)
                    return HypervisorPlatformInfo.Read();
                return CpuId.IsHyperVRootPartition();
            }
            catch { return null; }
        }

        /// <summary>取得 Hyper-V 运行环境的结构化检查结果，按监控程序、VMMS、WMI 顺序报告首个问题。</summary>
        /// <returns>各阶段状态、首个问题、原始错误和 VMMS 服务详情；未执行的阶段为 NotChecked。</returns>
        /// <remarks>每阶段最多等待 10 秒，前一阶段未通过时停止后续检查。超时结束等待，底层查询继续；重复调用共用仍在进行的查询。</remarks>
        public static Task<HyperVEnvironmentStatus> GetHyperVDiagnosticsAsync()
            => HyperVEnvironmentInspector.GetAsync(IsHypervisorActive);

        /// <summary>读取 Hyper-V 环境是否就绪、是否查到 VMMS 及首个问题说明。</summary>
        /// <returns>IsReady 表示三项均通过；IsInstalled 只在本次确认 VMMS 存在时为 true，未检查、查询失败和不存在均为 false。StatusText 只报告首个问题，正常就绪时为空。</returns>
        /// <remarks>需要区分未检查、查询失败与服务不存在时，使用 GetHyperVDiagnosticsAsync。</remarks>
        public static async Task<(bool IsReady, bool IsInstalled, string StatusText)> GetHyperVStatusAsync()
        {
            var status = await GetHyperVDiagnosticsAsync().ConfigureAwait(false);
            return (status.IsReady, status.IsInstalled == true, status.StatusText);
        }
        /// <summary>启用或停用宿主 Hyper-V。</summary>
        /// <param name="enabled">true 启用所需 Windows 功能并允许监控程序启动；false 停用监控程序启动，保留已安装组件。</param>
        /// <returns>配置与运行检查的结果。RequiresRestart 和 RestartReasons 表示重启要求；Steps 保留各步骤结果。后续步骤失败时，已完成的变更和重启要求仍保留。</returns>
        /// <remarks>
        /// <para>开启时先启用必要组件，再修改引导配置；组件失败或尚待完成时停止后续修改。确认家庭版缺少功能时，尝试登记适用的本机 Hyper-V 安装包。无需重启时继续检查监控程序、VMMS 和 WMI 是否就绪。</para>
        /// <para>需要管理员权限，同一宿主的 Core 请求串行执行。此方法不关闭虚拟机、不自动重启；调用方根据 RequiresRestart 安排重启。</para>
        /// </remarks>
        public static async Task<ApiResponse> SetHyperVEnabledAsync(bool enabled)
        {
            try
            {
                return await HostResourceLock.RunAsync("HyperV.EnableDisable",
                    () => SetHyperVEnabledAsync(enabled, new ConfigurationBackend())).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return ApiResponse.Fail($"Hyper-V 启停请求未确认完成：{error.Message}", error.HResult,
                    ApiErrorSource.Win32, error, ApiErrorKind.OutcomeUnknown);
            }
        }

        internal interface IConfigurationBackend
        {
            Task<ApiResponse> EnableFeaturesAsync();
            ApiResponse<ulong> ReadLaunchType();
            Task<ApiResponse> WriteLaunchTypeAsync(bool enabled);
            Task<HyperVEnvironmentStatus> GetDiagnosticsAsync();
            bool? ReadHypervisorState();
        }

        internal static async Task<ApiResponse> SetHyperVEnabledAsync(bool enabled, IConfigurationBackend backend)
        {
            bool restart = false;
            bool submitted = false;
            var reasons = new HashSet<RestartReason>();
            var steps = new List<OperationStepResult>();
            ApiResponse Finish(ApiResponse result) => result.WithRestartRequired(restart).WithRestartReasons(reasons).WithSteps(steps);
            try
            {
                if (enabled)
                {
                    submitted = true;
                    var feature = await backend.EnableFeaturesAsync().ConfigureAwait(false);
                    restart |= feature.RequiresRestart;
                    reasons.UnionWith(feature.RestartReasons);
                    steps.AddRange(feature.Steps);
                    if (!feature.Success) return Finish(feature);
                }
                var current = backend.ReadLaunchType();
                var read = current.Success ? ApiResponse.Ok() : ApiResponse.Fail(
                    $"启动配置读取失败：{current.Error}", current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
                steps.Add(OperationStepResult.From("ReadLaunchType", read));
                if (!read.Success) return Finish(read);
                if (current.HasData && current.Data > 1)
                    return Finish(ApiResponse.Fail($"无法识别监控程序启动配置值 {current.Data}，未修改。", kind: ApiErrorKind.InvalidState));
                ulong expected = enabled ? 1ul : 0ul;
                bool changed = !current.HasData || current.Data != expected;
                bool? active;
                try { active = backend.ReadHypervisorState(); }
                catch { active = null; } // 运行状态读取失败不改写为未运行，稍后保留详细诊断。
                if (changed)
                {
                    submitted = true;
                    var written = await backend.WriteLaunchTypeAsync(enabled).ConfigureAwait(false);
                    if (written.Success && active.HasValue && active.Value != enabled)
                        written = written.WithRestartReason(RestartReason.BootConfiguration);
                    restart |= written.RequiresRestart;
                    reasons.UnionWith(written.RestartReasons);
                    steps.Add(OperationStepResult.From("SetLaunchType", written));
                    if (!written.Success) return Finish(written);
                }
                else if (!enabled && active == true)
                {
                    // 已配置 Off 但当前仍运行：变更尚未在启动时采用。
                    restart = true;
                    reasons.Add(RestartReason.BootConfiguration);
                    steps.Add(OperationStepResult.From("AwaitLaunchType",
                        ApiResponse.Ok().WithRestartReason(RestartReason.BootConfiguration)));
                }
                if (!restart)
                {
                    var runtime = await backend.GetDiagnosticsAsync().ConfigureAwait(false);
                    bool satisfied = enabled ? runtime.IsReady
                        : runtime.Hypervisor.State == HyperVCheckState.NotReady
                          && runtime.Hypervisor.DiagnosticKind == HyperVDiagnosticKind.NotRunning;
                    // 轻量运行探测不确定时，以后续有界诊断补确认；不把 unknown 自动当作需重启。
                    bool? observedActive = runtime.Hypervisor.State == HyperVCheckState.Passed ? true
                        : runtime.Hypervisor.State == HyperVCheckState.NotReady
                          && runtime.Hypervisor.DiagnosticKind == HyperVDiagnosticKind.NotRunning ? false : null;
                    if (active == null && observedActive.HasValue && observedActive.Value != enabled && (changed || !enabled))
                    {
                        reasons.Add(RestartReason.BootConfiguration);
                        steps.Add(OperationStepResult.From("AwaitLaunchType",
                            ApiResponse.Ok().WithRestartReason(RestartReason.BootConfiguration)));
                        return Finish(ApiResponse.Ok());
                    }
                    ApiFailure? failure = runtime.FirstIssue?.Failure;
                    var verified = satisfied ? ApiResponse.Ok() : ApiResponse.Fail(
                        $"启动配置已确认，但当前运行状态未达到要求：{runtime.StatusText}",
                        failure?.Code ?? -1, failure?.Source ?? ApiErrorSource.None,
                        kind: failure?.Kind ?? ApiErrorKind.InvalidState);
                    steps.Add(OperationStepResult.From("VerifyRuntime", verified));
                    return Finish(verified);
                }
                return Finish(ApiResponse.Ok());
            }
            catch (Exception error)
            {
                return Finish(ApiResponse.Fail($"Hyper-V 启停处理未确认完成：{error.Message}", error.HResult,
                    ApiErrorSource.Win32, error, submitted ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed));
            }
        }

        private sealed class ConfigurationBackend : IConfigurationBackend
        {
            public Task<ApiResponse> EnableFeaturesAsync() => HyperVFeatureInstaller.EnableAsync();
            public ApiResponse<ulong> ReadLaunchType() => BcdStoreClient.Read(BcdSetting.HypervisorLaunchType);
            public Task<ApiResponse> WriteLaunchTypeAsync(bool enabled) => BcdStoreClient.SetAsync(BcdSetting.HypervisorLaunchType, enabled ? 1ul : 0ul);
            public Task<HyperVEnvironmentStatus> GetDiagnosticsAsync() => GetHyperVDiagnosticsAsync();
            public bool? ReadHypervisorState() => IsHypervisorActive();
        }
    }
}
