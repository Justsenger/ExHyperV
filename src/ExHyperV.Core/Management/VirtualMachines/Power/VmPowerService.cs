using ExHyperV.Tools;
using ExHyperV.Models;

using System.Diagnostics;

namespace ExHyperV.Services
{
    /// <summary>执行单台虚拟机电源控制，管理电源行为和虚拟电池。</summary>
    public static partial class VmPowerService
    {
        private sealed record VmRuntimeState(ushort EnabledState, int ProcessId);

        private static readonly TimeSpan PowerRequestTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan PowerOffWaitTimeout = TimeSpan.FromSeconds(5);

        /// <summary>按唯一显示名称定位虚拟机并执行指定电源动作。</summary>
        /// <param name="vmName">目标虚拟机的显示名称；重名时拒绝操作。</param>
        /// <param name="action">区分大小写的 Start、TurnOff、Stop、Restart、Save 或 Suspend；其他值返回失败。</param>
        /// <returns>身份解析或电源请求的结果。</returns>
        /// <remarks>名称仅在入口解析一次，后续执行使用 GUID。Stop 或 Restart 失败不会自动升级为强制操作。</remarks>
        public static Task<ApiResponse> ExecuteControlActionAsync(string vmName, string action)
            => TryParseAction(action, out var parsed) ? ExecuteControlActionAsync(vmName, parsed)
                : Task.FromResult(InvalidAction());

        /// <summary>按稳定身份执行指定名称的电源动作。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <param name="action">区分大小写的 Start、TurnOff、Stop、Restart、Save 或 Suspend；其他值返回失败。</param>
        /// <returns>参数校验或电源请求的结果。</returns>
        /// <remarks>Stop 或 Restart 失败不会自动升级为强制操作。TurnOff 明确允许必要时终止该虚拟机工作进程。</remarks>
        public static Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, string action)
            => TryParseAction(action, out var parsed) ? ExecuteControlActionAsync(vmId, parsed)
                : Task.FromResult(InvalidAction());

        /// <summary>按唯一显示名称定位虚拟机并执行类型化电源动作。</summary>
        /// <param name="vmName">目标虚拟机的显示名称；重名时拒绝操作。</param>
        /// <param name="action">要执行的动作；未定义的枚举值返回失败。</param>
        /// <returns>身份解析或电源请求的结果。</returns>
        /// <remarks>解析成功后只使用 GUID 定位执行目标，避免名称变化导致操作其他虚拟机。</remarks>
        public static async Task<ApiResponse> ExecuteControlActionAsync(string vmName, VmPowerAction action)
        {
            if (!Enum.IsDefined(action)) return InvalidAction();
            var identity = await VmIdentity.ResolveAsync(vmName).ConfigureAwait(false);
            if (!identity.HasData)
                return ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource,
                    identity.Exception, identity.ErrorKind);
            return await ExecuteControlActionAsync(identity.Data, action).ConfigureAwait(false);
        }

        /// <summary>按稳定身份执行类型化电源动作，不隐式升级正常关机或重启。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <param name="action">要执行的动作；未定义的枚举值返回失败。</param>
        /// <returns>参数校验或电源请求的结果；启动成功不表示后台亲和性恢复已经结束。</returns>
        /// <remarks>Stop 和 Restart 直接返回请求结果。TurnOff 先请求下电，失败或超时后可终止对应 vmwp 进程；终止前核对 VM 归属、创建时间和当前进程映射。本方法不提供取消已提交系统操作的保证。</remarks>
        /// <editentry label="电源控制" category="电源"/>
        public static Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, VmPowerAction action)
        {
            if (!Enum.IsDefined(action)) return Task.FromResult(InvalidAction());
            if (vmId == Guid.Empty)
                return Task.FromResult(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
            string predicate = VmIdentity.ComputerSystemPredicate(vmId);
            string wql = $"SELECT * FROM Msvm_ComputerSystem WHERE {predicate}";
            Task<ApiResponse> RequestAsync(ushort state) => WmiApi.InvokeAsync(
                wql, "RequestStateChange", parameters => parameters["RequestedState"] = state);
            var backend = new PowerActionBackend(RequestAsync,
                () => ForceTurnOffAsync(vmId.ToString("D"), wql, predicate),
                () => HostAzureFeatureSetService.RunTemporarilyDisabledAsync(() => RequestAsync(2)),
                () => { _ = ApplyStartupAffinityAsync(predicate); });
            return ExecuteControlActionAsync(action, backend);
        }

        internal static bool TryParseAction(string? action, out VmPowerAction parsed)
            => Enum.TryParse(action, ignoreCase: false, out parsed) && Enum.IsDefined(parsed)
                && string.Equals(Enum.GetName(parsed), action, StringComparison.Ordinal);

        private static ApiResponse InvalidAction() => ApiResponse.Fail(
            "不支持指定的虚拟机电源动作。", kind: ApiErrorKind.InvalidArgument);

        internal sealed record PowerActionBackend(
            Func<ushort, Task<ApiResponse>> RequestStateChange,
            Func<Task<ApiResponse>> ForceTurnOff,
            Func<Task<ApiResponse>> Start,
            Action ApplyStartupAffinity);

        internal static async Task<ApiResponse> ExecuteControlActionAsync(VmPowerAction action, PowerActionBackend backend)
        {
            switch (action)
            {
                case VmPowerAction.Start:
                    var startResult = await backend.Start().ConfigureAwait(false);
                    if (startResult.Success) backend.ApplyStartupAffinity();
                    return startResult;
                case VmPowerAction.TurnOff:
                    return await backend.ForceTurnOff().ConfigureAwait(false);
                case VmPowerAction.Stop:
                    return await backend.RequestStateChange(4).ConfigureAwait(false);
                case VmPowerAction.Restart:
                    var restartResult = await backend.RequestStateChange(10).ConfigureAwait(false);
                    if (restartResult.Success) backend.ApplyStartupAffinity();
                    return restartResult;
                case VmPowerAction.Save:
                    return await backend.RequestStateChange(6).ConfigureAwait(false);
                case VmPowerAction.Suspend:
                    return await backend.RequestStateChange(9).ConfigureAwait(false);
                default:
                    return InvalidAction();
            }
        }

        private static async Task ApplyStartupAffinityAsync(string predicate)
        {
            try
            {
                if (HyperVSchedulerService.GetSchedulerType() != HyperVSchedulerType.Root) return;
                var vm = await WmiApi.QueryFirstAsync($"SELECT Name FROM Msvm_ComputerSystem WHERE {predicate}",
                    obj => Guid.Parse(obj["Name"].ToString()!));
                if (!vm.HasData) return;
                var result = await CpuAffinityService.ApplySavedAffinityAfterStartAsync(vm.Data).ConfigureAwait(false);
                if (result is CpuAffinityService.StartupApplyStatus.Exhausted or CpuAffinityService.StartupApplyStatus.InvalidConfiguration
                    or CpuAffinityService.StartupApplyStatus.ReselectionRequired)
                    Debug.WriteLine($"[ExHyperV] CPU affinity restore for {vm.Data:D}: {result}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.VmPage_AffinityApplyException, ex.Message));
            }
        }

        private static async Task<ApiResponse> ForceTurnOffAsync(string vmName, string wql, string predicate)
        {
            var requestTask = WmiApi.InvokeAsync(wql, "RequestStateChange",
                p => p["RequestedState"] = (ushort)3);

            ApiResponse? requestResult = null;
            if (await Task.WhenAny(requestTask, Task.Delay(PowerRequestTimeout)) == requestTask)
                requestResult = await requestTask;
            else
                ObserveFault(requestTask);

            if (requestResult?.Success == true &&
                await WaitForPoweredOffAsync(predicate, PowerOffWaitTimeout))
                return ApiResponse.Ok();

            var state = await GetRuntimeStateAsync(predicate, TimeSpan.FromSeconds(3));
            if (state is null)
            {
                return requestResult is { Success: false }
                    ? requestResult
                    : ApiResponse.Fail(Properties.Resources.Error_VmPower_WorkerNotFound);
            }

            if (state.EnabledState == 3)
                return ApiResponse.Ok();

            if (state.ProcessId <= 0)
            {
                return requestResult is { Success: false }
                    ? requestResult
                    : ApiResponse.Fail(Properties.Resources.Error_VmPower_WorkerNotFound);
            }

            try
            {
                using var process = Process.GetProcessById(state.ProcessId);
                // Retain the native handle before reading identity. Kill and the wait
                // use this same Process instance rather than reopening a reused PID.
                _ = process.SafeHandle;
                long creationTime = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                var identity = await ReadWorkerIdentityAsync(state.ProcessId).ConfigureAwait(false);
                if (!process.HasExited)
                {
                    if (!identity.HasData)
                        return ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource,
                            identity.Exception, identity.Success ? ApiErrorKind.Conflict : identity.ErrorKind);
                    if (!MatchesWorkerIdentity(Guid.Parse(vmName), state.ProcessId, creationTime, identity.Data!))
                        return ApiResponse.Fail("虚拟机工作进程的归属或创建时间已变化，未终止进程。", kind: ApiErrorKind.Conflict);

                    var current = await GetRuntimeStateAsync(predicate, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    if (current?.EnabledState == 3) return ApiResponse.Ok();
                    if (current is null || current.ProcessId != state.ProcessId)
                        return ApiResponse.Fail("无法确认该进程仍属于目标虚拟机，未终止进程。", kind: ApiErrorKind.Conflict);

                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (ArgumentException)
            {
                // The worker exited between the WMI query and Process.GetProcessById.
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(
                    string.Format(Properties.Resources.Error_VmPower_TerminateWorkerFailed, vmName, ex.Message),
                    exception: ex);
            }

            return await WaitForPoweredOffAsync(predicate, PowerOffWaitTimeout)
                ? ApiResponse.Ok()
                : ApiResponse.Fail(string.Format(Properties.Resources.Error_VmPower_StillNotOff, vmName));
        }

        private static async Task<bool> WaitForPoweredOffAsync(string predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var state = await GetRuntimeStateAsync(predicate, TimeSpan.FromSeconds(2));
                if (state?.EnabledState == 3)
                    return true;

                await Task.Delay(300);
            }

            return false;
        }

        private static async Task<VmRuntimeState?> GetRuntimeStateAsync(string predicate, TimeSpan timeout)
        {
            var queryTask = WmiApi.QueryFirstAsync(
                $"SELECT EnabledState, ProcessID FROM Msvm_ComputerSystem WHERE {predicate}",
                obj => new VmRuntimeState(
                    Convert.ToUInt16(obj["EnabledState"] ?? (ushort)0),
                    Convert.ToInt32(obj["ProcessID"] ?? 0)));

            if (await Task.WhenAny(queryTask, Task.Delay(timeout)) != queryTask)
            {
                ObserveFault(queryTask);
                return null;
            }

            var response = await queryTask;
            return response.HasData ? response.Data : null;
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }
}
