using ExHyperV.Tools;
using ExHyperV.Models;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ExHyperV.Services
{
    /// <summary>执行单台虚拟机电源控制，管理电源行为和虚拟电池。</summary>
    public static partial class VmPowerService
    {
        internal sealed record VmRuntimeState(ushort EnabledState, int ProcessId);

        private static readonly TimeSpan PowerRequestTimeout = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan RequestConfirmedPowerOffWaitTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PowerOffWaitTimeout = TimeSpan.FromSeconds(5);

        /// <summary>按唯一显示名称定位虚拟机并执行指定电源动作。</summary>
        /// <param name="vmName">目标虚拟机的显示名称；重名时拒绝操作。</param>
        /// <param name="action">区分大小写的 Start、TurnOff、Stop、Restart、Save 或 Suspend；其他值返回失败。</param>
        /// <returns>身份解析或电源请求的结果。</returns>
        /// <remarks>供只持有显示名称的调用方使用。名称先解析为 GUID；已有 GUID 时使用对应重载。</remarks>
        public static Task<ApiResponse> ExecuteControlActionAsync(string vmName, string action)
            => TryParseAction(action, out var parsed) ? ExecuteControlActionAsync(vmName, parsed)
                : Task.FromResult(InvalidAction());

        /// <summary>按稳定身份执行指定名称的电源动作。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <param name="action">区分大小写的 Start、TurnOff、Stop、Restart、Save 或 Suspend；其他值返回失败。</param>
        /// <returns>参数校验或电源请求的结果。</returns>
        /// <remarks>供接收文本动作的调用方使用；普通 C# 调用使用 VmPowerAction 重载。TurnOff 必要时会终止目标虚拟机工作进程。</remarks>
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

        /// <summary>使用虚拟机 GUID 和电源动作枚举执行电源操作。</summary>
        /// <param name="vmId">目标虚拟机的非空 GUID。</param>
        /// <param name="action">要执行的动作；未定义的枚举值返回失败。</param>
        /// <returns>参数校验或电源请求的结果；亲和性恢复在启动后由后台继续执行。</returns>
        /// <remarks>Stop 和 Restart 返回来宾请求结果。TurnOff 优先请求下电；超时、受阻或未能在预期时间内关机时，在核验目标虚拟机工作进程身份后直接终止工作进程完成断电。</remarks>
        /// <editentry label="电源控制" category="电源"/>
        public static async Task<ApiResponse> ExecuteControlActionAsync(Guid vmId, VmPowerAction action)
        {
            if (!Enum.IsDefined(action)) return InvalidAction();
            if (vmId == Guid.Empty)
                return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            string predicate = VmIdentity.ComputerSystemPredicate(vmId);
            string wql = $"SELECT * FROM Msvm_ComputerSystem WHERE {predicate}";
            Task<ApiResponse> RequestAsync(ushort state) => WmiApi.InvokeAsync(
                wql, "RequestStateChange", parameters => parameters["RequestedState"] = state);
            var backend = new PowerActionBackend(RequestAsync,
                () => ForceTurnOffAsync(vmId, wql, predicate),
                () => StartWithDiagnosticsAsync(vmId, () => HostAzureFeatureSetService.RunTemporarilyDisabledAsync(() => RequestAsync(2))),
                () => { _ = ApplyStartupAffinityAsync(predicate); });
            CoreDiagnostics.Report("PowerRequested", TraceEventType.Information, details: $"action={action}", vmId: vmId);
            var result = await ExecuteControlActionAsync(action, backend).ConfigureAwait(false);
            CoreDiagnostics.Report("PowerCompleted", TraceEventType.Information, details: $"action={action} success={result.Success}", vmId: vmId);
            return result;
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
                    CoreDiagnostics.Report("StartupAffinityRestoreFailed", details: $"status={result}", vmId: vm.Data);
            }
            catch (Exception ex)
            {
                CoreDiagnostics.Report("StartupAffinityException", error: ex);
            }
        }

        private static Task<ApiResponse> ForceTurnOffAsync(Guid vmId, string wql, string predicate) =>
            ForceTurnOffAsync(
                vmId,
                cancellationToken => WmiApi.InvokeAsync(
                    wql, "RequestStateChange", p => p["RequestedState"] = (ushort)3, cancellationToken: cancellationToken),
                timeout => WaitForPoweredOffAsync(predicate, timeout),
                () => GetRuntimeStateAsync(predicate, TimeSpan.FromSeconds(2)),
                (processId, requireWmiStateMatch) => TerminateWorkerAsync(vmId, processId, predicate, requireWmiStateMatch));

        internal static async Task<ApiResponse> ForceTurnOffAsync(
            Guid vmId,
            Func<CancellationToken, Task<ApiResponse>> sendTurnOffRequest,
            Func<TimeSpan, Task<bool>> waitForPoweredOff,
            Func<Task<VmRuntimeState?>> readRuntimeState,
            Func<int, bool, Task<ApiResponse>> terminateWorker,
            TimeSpan? requestTimeout = null)
        {
            var timeout = requestTimeout ?? PowerRequestTimeout;
            using var requestCts = new CancellationTokenSource(timeout);
            var requestTask = sendTurnOffRequest(requestCts.Token);

            var requestResult = await WaitForTurnOffRequestAsync(requestTask, timeout).ConfigureAwait(false);
            if (!requestResult.Success)
                requestCts.Cancel();

            if (requestResult.ErrorKind is ApiErrorKind.AccessDenied or ApiErrorKind.NotFound)
                return requestResult;

            if (requestResult.Success && await waitForPoweredOff(RequestConfirmedPowerOffWaitTimeout).ConfigureAwait(false))
                return ApiResponse.Ok();

            var state = await readRuntimeState().ConfigureAwait(false);
            if (state?.EnabledState == 3)
                return ApiResponse.Ok();

            int processId = state is not null
                ? state.ProcessId
                : await FindWorkerProcessIdAsync(vmId).ConfigureAwait(false);
            if (processId <= 0)
            {
                return requestResult.Success
                    ? WmiJobWaiter.Unknown("已接受关机请求，但尚未确认虚拟机已关闭。")
                    : requestResult;
            }

            return await terminateWorker(processId, state is not null).ConfigureAwait(false);
        }

        private static async Task<int> FindWorkerProcessIdAsync(Guid vmId)
        {
            var workers = await WmiApi.QueryAsync(
                "SELECT Handle, ProcessId, Name, CreationDate FROM Win32_Process WHERE Name = 'vmwp.exe'",
                process =>
                {
                    using var owner = process.InvokeMethod("GetOwner", null, null);
                    if (owner is null || owner["ReturnValue"] is null || Convert.ToUInt32(owner["ReturnValue"]) != 0)
                        return 0;
                    string domain = owner["Domain"]?.ToString() ?? string.Empty;
                    string user = owner["User"]?.ToString() ?? string.Empty;
                    return domain.Equals("NT VIRTUAL MACHINE", StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParse(user, out var ownerVm) && ownerVm == vmId
                        ? Convert.ToInt32(process["ProcessId"])
                        : 0;
                }, WmiScope.CimV2).ConfigureAwait(false);
            return workers.HasData ? workers.Data!.FirstOrDefault(pid => pid > 0) : 0;
        }

        private static async Task<ApiResponse> TerminateWorkerAsync(
            Guid vmId, int processId, string predicate, bool requireWmiStateMatch)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                // Retain the native handle before reading identity. Kill and the wait
                // use this same Process instance rather than reopening a reused PID.
                _ = process.SafeHandle;
                long creationTime = process.StartTime.ToUniversalTime().ToFileTimeUtc();
                var identity = await ReadWorkerIdentityAsync(processId).ConfigureAwait(false);
                if (!process.HasExited)
                {
                    if (!identity.HasData)
                        return ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource,
                            identity.Exception, identity.Success ? ApiErrorKind.Conflict : identity.ErrorKind);
                    if (!MatchesWorkerIdentity(vmId, processId, creationTime, identity.Data!))
                        return ApiResponse.Fail("虚拟机工作进程的归属或创建时间已变化，未终止进程。", kind: ApiErrorKind.Conflict);

                    var current = await GetRuntimeStateAsync(predicate, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (current?.EnabledState == 3) return ApiResponse.Ok();
                    if ((requireWmiStateMatch && current is null) || (current is not null && current.ProcessId != processId))
                        return ApiResponse.Fail("无法确认该进程仍属于目标虚拟机，未终止进程。", kind: ApiErrorKind.Conflict);

                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (ArgumentException)
            {
                // The worker exited between the WMI query and Process.GetProcessById.
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(
                    string.Format(Properties.Resources.Error_VmPower_TerminateWorkerFailed, vmId.ToString("D"), ex.Message),
                    exception: ex);
            }

            return await WaitForPoweredOffAsync(predicate, PowerOffWaitTimeout).ConfigureAwait(false)
                ? ApiResponse.Ok()
                : ApiResponse.Fail(string.Format(Properties.Resources.Error_VmPower_StillNotOff, vmId.ToString("D")));
        }

        internal static async Task<ApiResponse> WaitForTurnOffRequestAsync(Task<ApiResponse> request, TimeSpan timeout)
        {
            try
            {
                var result = await request.WaitAsync(timeout).ConfigureAwait(false);
                if (result.ErrorKind == ApiErrorKind.OutcomeUnknown) return result;
                if (result.ErrorKind == ApiErrorKind.Cancelled || result.DiagnosticCode == CoreErrorCode.OperationTimedOut)
                    return WmiJobWaiter.Unknown(result.Error, result.Code, result.ErrorSource, result.Exception);
                return result;
            }
            catch (Exception error)
            {
                ObserveFault(request);
                return WmiJobWaiter.FromException(error, submitted: true);
            }
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

        private static async Task<ApiResponse> StartWithDiagnosticsAsync(Guid vmId, Func<Task<ApiResponse>> startCore)
        {
            var result = await startCore().ConfigureAwait(false);
            if (result.Success) return result;

            if (result.DiagnosticCode == CoreErrorCode.InsufficientMemory)
            {
                long requiredMiB = await TryReadRequiredMemoryMiBAsync(vmId).ConfigureAwait(false);
                var hostMemory = HostSystemInfoService.TryReadHostMemoryMiB();
                if (requiredMiB > 0 && hostMemory is { TotalMiB: > 0 } hm)
                {
                    long deficitMiB = Math.Max(0L, requiredMiB - hm.AvailableMiB);
                    return result.WithDiagnostic(
                        CoreErrorCode.InsufficientMemory,
                        requiredMiB.ToString(CultureInfo.InvariantCulture),
                        hm.AvailableMiB.ToString(CultureInfo.InvariantCulture),
                        hm.TotalMiB.ToString(CultureInfo.InvariantCulture),
                        deficitMiB.ToString(CultureInfo.InvariantCulture));
                }

                return requiredMiB > 0
                    ? result.WithDiagnostic(CoreErrorCode.InsufficientMemory, requiredMiB.ToString(CultureInfo.InvariantCulture))
                    : result;
            }

            if (result.DiagnosticCode == CoreErrorCode.FileNotFound
                && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                try
                {
                    var state = await VmConfigurationState.ReadAsync(vmId).ConfigureAwait(false);
                    if (state.HasData && state.Data.Generation == 1)
                        return result.WithDiagnostic(CoreErrorCode.StartFailedGen1OnArm64);
                }
                catch (Exception diagnosticError) { CoreDiagnostics.Report("GenerationDiagnosisFailed", error: diagnosticError); }
            }

            if (result.DiagnosticCode == CoreErrorCode.Unsupported)
            {
                string[] suspects = await TryDetectUnsupportedStartFeaturesAsync(vmId).ConfigureAwait(false);
                if (suspects.Length > 0)
                    return result.WithDiagnostic(CoreErrorCode.StartUnsupportedWithSuspects, suspects);
                return string.IsNullOrWhiteSpace(result.Error)
                    ? result.WithDiagnostic(CoreErrorCode.StartFailedUnsupported)
                    : result.WithDiagnostic(CoreErrorCode.StartFailedUnsupported, result.Error);
            }

            if (result.DiagnosticCode == CoreErrorCode.MethodFailed)
            {
                var diag = await TryDiagnoseStartMethodFailedAsync(vmId, result).ConfigureAwait(false);
                if (diag != null)
                    return diag;
            }

            return result;
        }

        private static async Task<ApiResponse?> TryDiagnoseStartMethodFailedAsync(Guid vmId, ApiResponse original)
        {
            try
            {
                var procResult = await VmProcessorService.GetVmProcessorAsync(vmId).ConfigureAwait(false);
                if (!procResult.HasData) return null;
                var proc = procResult.Data.Values;
                int hostCount = Environment.ProcessorCount;
                if (hostCount > 0 && proc.Count > hostCount)
                {
                    return original.WithDiagnostic(
                        CoreErrorCode.StartFailedProcessorTopology,
                        proc.Count.ToString(CultureInfo.InvariantCulture),
                        hostCount.ToString(CultureInfo.InvariantCulture));
                }
                if (proc.ExposeVirtualizationExtensions == true)
                {
                    string[] suspects = await TryDetectUnsupportedStartFeaturesAsync(vmId, proc).ConfigureAwait(false);
                    if (suspects.Length > 0)
                        return original.WithDiagnostic(CoreErrorCode.StartUnsupportedWithSuspects, suspects);
                }
            }
            catch (Exception diagnosticError) { CoreDiagnostics.Report("StartFeatureDiagnosisFailed", error: diagnosticError); }
            return null;
        }

        private static async Task<long> TryReadRequiredMemoryMiBAsync(Guid vmId)
        {
            try
            {
                var memResult = await VmMemoryService.GetVmMemorySettingsAsync(vmId).ConfigureAwait(false);
                return memResult.HasData ? Math.Max(0, memResult.Data.Values.Startup) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static async Task<string[]> TryDetectUnsupportedStartFeaturesAsync(
            Guid vmId,
            VmProcessorSettings? prefetchedProc = null)
        {
            var suspects = new List<string>();
            try
            {
                var memTask = VmMemoryService.GetVmMemorySettingsAsync(vmId);
                var procTask = prefetchedProc == null ? VmProcessorService.GetVmProcessorAsync(vmId) : null;
                var hostTask = VmProcessorService.GetHostCapabilitiesAsync();
                if (procTask != null)
                    await Task.WhenAll(memTask, procTask, hostTask).ConfigureAwait(false);
                else
                    await Task.WhenAll(memTask, hostTask).ConfigureAwait(false);

                var mem = (await memTask.ConfigureAwait(false)) is { HasData: true } mr ? mr.Data.Values : null;
                var proc = prefetchedProc ?? ((await procTask!.ConfigureAwait(false)) is { HasData: true } pr ? pr.Data.Values : null);
                var host = (await hostTask.ConfigureAwait(false)) is { HasData: true } hr ? hr.Data : null;

                // Even Intel hosts without SGX (e.g. 11th+ Gen Core) fail Start with 0xC0350005 when SgxEnabled=true.
                if (mem?.SgxEnabled == true)
                    suspects.Add("SGX");
                if (proc?.ExposeVirtualizationExtensions == true && host is { IsArm64: true })
                    suspects.Add("NestedVirt");
                if (proc != null && host is { IsIntel: false } &&
                    (proc.EnablePerfmonPmu == true || proc.EnablePerfmonLbr == true || proc.EnablePerfmonPebs == true || proc.EnablePerfmonIpt == true))
                    suspects.Add("IntelPerfmon");
                if (proc?.HardwareIsolationExtensionsEnabled == true && host is { HardwareIsolationSupported: false })
                    suspects.Add("HwIsolation");
                if (host?.IsArm64 == true && proc?.ApicMode is not null and not VmApicMode.Default)
                    suspects.Add("ApicMode");
                if (host is { IsArm64: false } && proc?.LpiMode is not null and not LpiMode.Default)
                    suspects.Add("LpiMode");
                if (mem?.CxlEnabled == true)
                    suspects.Add("CXL");
                if (mem?.BackingPageSize == 2)
                    suspects.Add("HugePages1Gb");
                if (mem?.MemoryEncryptionPolicy == 2)
                    suspects.Add("MemEncryption");
            }
            catch (Exception diagnosticError) { CoreDiagnostics.Report("MemoryDiagnosisFailed", error: diagnosticError); }
            return suspects.ToArray();
        }
    }
}
