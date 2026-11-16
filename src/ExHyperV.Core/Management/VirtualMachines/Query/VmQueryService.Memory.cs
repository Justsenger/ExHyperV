using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services;

public partial class VmQueryService
{
    /// <summary>按虚拟机 GUID 读取运行时已分配内存和来宾可用内存比例，分别报告两个字段的有效性。</summary>
    /// <returns>摘要查询失败返回错误；成功时各项仍可能缺测。已分配内存单位为 MiB，可用比例按内存需求计算，负数表示需求超过分配量。</returns>
    /// <remarks>动态内存读取 MemoryAvailable；固定内存根据 Current Pressure 与 Guest Visible Physical Memory 推算需求，再计算可用比例。按 GUID、运行时间及分配量核对样本，缺测时保留历史值；分配量与可用比例的有效性分别见 AssignedSample 和 AvailableSample。</remarks>
    public async Task<ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>> GetVmRuntimeMemoryDataAsync()
    {
        var summaryTask = QueryMemorySummaryAsync();
        var settingsTask = WmiApi.QueryAsync(
            "SELECT InstanceID, DynamicMemoryEnabled FROM Msvm_MemorySettingData WHERE ResourceType = 4",
            obj => (Id: obj["InstanceID"]?.ToString(), Enabled: obj["DynamicMemoryEnabled"] as bool?), WmiScope.HyperV);
        await Task.WhenAll(summaryTask, settingsTask).ConfigureAwait(false);
        var summary = await summaryTask.ConfigureAwait(false);
        var settings = await settingsTask.ConfigureAwait(false);
        if (!summary.Success)
            return ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>.Fail(summary.Error, summary.Code,
                summary.ErrorSource, summary.Exception, summary.ErrorKind);
        try
        {
            var observed = DateTimeOffset.UtcNow;
            var dynamicSettings = (settings.Data ?? []).Where(row => TryGetMemoryVmId(row.Id, out _))
                .GroupBy(row => { TryGetMemoryVmId(row.Id, out var id); return id; })
                .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.Single().Enabled : null);
            var result = new Dictionary<Guid, VmDynamicMemoryData>();
            foreach (var row in summary.Data ?? [])
            {
                if (!Guid.TryParse(row.Id, out var id) || id == Guid.Empty || result.ContainsKey(id))
                    return ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>.Fail(
                        "内存摘要包含无效或重复的虚拟机 GUID，不能归属样本。", kind: ApiErrorKind.AmbiguousTarget);
                bool? dynamicEnabled = settings.Success ? dynamicSettings.GetValueOrDefault(id) : null;
                var data = MapRuntimeMemory(row.Assigned, row.Available, row.State, dynamicEnabled, observed, row.AvailableBuffer);
                if (!settings.Success)
                {
                    if (data.AvailableSample?.State != PerformanceSampleState.NotApplicable)
                        data = data with { AvailableSample = new(PerformanceSampleState.Failed, observed, settings.Error) };
                    if (data.AvailableBufferSample?.State != PerformanceSampleState.NotApplicable)
                        data = data with { AvailableBufferSample = new(PerformanceSampleState.Failed, observed, settings.Error) };
                }
                result.Add(id, data);
            }
            var balancerTargets = (summary.Data ?? []).Where(row => Guid.TryParse(row.Id, out var id)
                && dynamicSettings.ContainsKey(id) && VmMapper.IsActiveState(row.State)).ToArray();
            if (settings.Success && balancerTargets.Length > 0)
                await ReadBalancerMemoryCountersAsync(result, summary.Data ?? [], balancerTargets,
                    id => dynamicSettings.GetValueOrDefault(id)).ConfigureAwait(false);
            return ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>.Ok(result);
        }
        catch (Exception error)
        {
            return ApiResponse<Dictionary<Guid, VmDynamicMemoryData>>.Fail(error.Message,
                exception: error, kind: ApiErrorKind.OperationFailed);
        }
    }

    internal sealed record MemoryRuntimeSummary(string? Id, string? Name, ushort State,
        object? Assigned, object? Available, ulong Uptime, object? AvailableBuffer = null);

    private static Task<ApiResponse<List<MemoryRuntimeSummary>>> QueryMemorySummaryAsync() => WmiApi.QueryAsync(
        "SELECT Name, ElementName, EnabledState, UpTime, MemoryUsage, MemoryAvailable, AvailableMemoryBuffer FROM Msvm_SummaryInformation",
        obj => new MemoryRuntimeSummary(obj["Name"]?.ToString(), obj["ElementName"]?.ToString(),
            Convert.ToUInt16(obj["EnabledState"] ?? (ushort)0), obj["MemoryUsage"], obj["MemoryAvailable"],
            Convert.ToUInt64(obj["UpTime"] ?? 0), obj["AvailableMemoryBuffer"]), WmiScope.HyperV);

    internal static async Task ReadBalancerMemoryCountersAsync(Dictionary<Guid, VmDynamicMemoryData> result,
        IReadOnlyList<MemoryRuntimeSummary> before, IReadOnlyList<MemoryRuntimeSummary> targets,
        Func<Guid, bool?> dynamicLookup)
    {
        // MemoryAvailable is explicitly undefined for fixed-memory VMs. Azure Migrate instead
        // derives memory demand from Current Pressure * Guest Visible Physical Memory / 100.
        // https://learn.microsoft.com/azure/migrate/discovered-metadata
        var counters = await WmiApi.QueryAsync(
            "SELECT Name, CurrentPressure, GuestVisiblePhysicalMemory, GuestAvailableMemory, PhysicalMemory, SmartPagingWorkingSetSize FROM Win32_PerfFormattedData_BalancerStats_HyperVDynamicMemoryVM",
            obj => (Name: obj["Name"]?.ToString(), Pressure: obj["CurrentPressure"],
                Visible: obj["GuestVisiblePhysicalMemory"], GuestAvailable: obj["GuestAvailableMemory"],
                Physical: obj["PhysicalMemory"], SmartPaging: obj["SmartPagingWorkingSetSize"]), WmiScope.CimV2).ConfigureAwait(false);
        // These counter instances have display names, not IDs. Recheck their owners after reading;
        // never select the first duplicate or interpret a generated '#n' suffix as a VM identity.
        var after = counters.Success ? await QueryMemorySummaryAsync().ConfigureAwait(false) : null;
        var afterRows = after?.Data ?? [];
        var observed = DateTimeOffset.UtcNow;
        foreach (var row in targets)
        {
            var id = Guid.Parse(row.Id!);
            var data = result[id];
            bool? dynamicEnabled = dynamicLookup(id);
            PerformanceSampleInfo failure;
            if (!counters.Success || after?.Success != true)
                failure = new(!counters.Success && (counters.ErrorKind == ApiErrorKind.Unsupported
                    || counters.Code == (int)ManagementStatus.InvalidClass) ? PerformanceSampleState.Unsupported : PerformanceSampleState.Failed,
                    observed, !counters.Success ? counters.Error : after?.Error);
            else if (string.IsNullOrEmpty(row.Name) || row.Name.Contains('#')
                || before.Count(item => string.Equals(item.Name, row.Name, StringComparison.OrdinalIgnoreCase)) != 1
                || afterRows.Count(item => string.Equals(item.Name, row.Name, StringComparison.OrdinalIgnoreCase)) != 1
                || afterRows.Count(item => string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase)) != 1
                || (counters.Data ?? []).Any(item => item.Name?.StartsWith(row.Name + "#", StringComparison.OrdinalIgnoreCase) == true))
                failure = new(PerformanceSampleState.Ambiguous, observed, "内存计数器名称无法唯一对应虚拟机 GUID。");
            else
            {
                var owner = afterRows.Single(item => string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
                var matches = (counters.Data ?? []).Where(item => string.Equals(item.Name, row.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (!string.Equals(owner.Name, row.Name, StringComparison.OrdinalIgnoreCase)
                    || !VmMapper.IsActiveState(owner.State) || row.Uptime == 0 || owner.Uptime < row.Uptime
                    || !Equals(owner.Assigned, row.Assigned))
                    failure = new(PerformanceSampleState.Unavailable, observed, "内存采样期间虚拟机身份、运行状态或分配量发生变化。");
                else if (matches.Length != 1)
                    failure = new(matches.Length > 1 ? PerformanceSampleState.Ambiguous : PerformanceSampleState.Unavailable,
                        observed, "未取得唯一的来宾内存需求计数器。");
                else
                {
                    result[id] = MapBalancerMemoryCounters(data, matches[0].Pressure, matches[0].Visible,
                        matches[0].Physical, matches[0].GuestAvailable, matches[0].SmartPaging, dynamicEnabled, observed);
                    continue;
                }
            }
            result[id] = ApplyBalancerFailure(data, failure, dynamicEnabled);
        }
    }

    internal static VmDynamicMemoryData MapFixedMemoryDemand(VmDynamicMemoryData data,
        object? pressure, object? visible, object? physical, DateTimeOffset observed)
        => MapBalancerMemoryCounters(data, pressure, visible, physical, null, null, false, observed);

    internal static VmDynamicMemoryData MapBalancerMemoryCounters(VmDynamicMemoryData data,
        object? pressure, object? visible, object? physical, object? guestAvailable, object? smartPaging,
        bool? dynamicEnabled, DateTimeOffset observed)
    {
        try
        {
            var pressurePercent = pressure == null ? 0 : Convert.ToDecimal(pressure);
            var visibleMiB = visible == null ? 0 : Convert.ToInt64(visible);
            var physicalMiB = physical == null ? 0 : Convert.ToInt64(physical);
            // A zero/uninitialized pressure must not manufacture an empty-memory sample.
            if (data.AssignedSample?.IsValid != true || data.AssignedMiB <= 0
                || pressurePercent <= 0 || visibleMiB <= 0 || physicalMiB != data.AssignedMiB)
                return ApplyBalancerFailure(data, new(PerformanceSampleState.Unavailable, observed,
                    "来宾内存需求尚未就绪，或计数器分配量与虚拟机摘要不一致。"), dynamicEnabled);

            var demandMiB = checked((long)decimal.Round(pressurePercent * visibleMiB / 100m, 0, MidpointRounding.AwayFromZero));
            var roundedPressure = checked((int)decimal.Round(pressurePercent, 0, MidpointRounding.AwayFromZero));
            var valid = new PerformanceSampleInfo(PerformanceSampleState.Valid, observed);

            var updated = data with
            {
                PressurePercent = roundedPressure,
                PressureSample = valid,
                GuestVisibleMiB = visibleMiB,
                GuestVisibleSample = valid,
                GuestAvailableMiB = guestAvailable != null ? Math.Max(0L, Convert.ToInt64(guestAvailable)) : 0,
                GuestAvailableSample = guestAvailable != null ? valid : new(PerformanceSampleState.Unavailable, observed, "未取得来宾可用内存计数器。"),
                SmartPagingMiB = smartPaging != null ? Math.Max(0L, Convert.ToInt64(smartPaging)) : 0,
                SmartPagingSample = smartPaging != null ? valid : new(PerformanceSampleState.Unavailable, observed, "未取得智能分页计数器。")
            };

            if (dynamicEnabled != true)
            {
                var availablePercent = checked((int)decimal.Round(100m - pressurePercent * visibleMiB / data.AssignedMiB,
                    0, MidpointRounding.AwayFromZero));
                updated = updated with
                {
                    AvailablePercent = availablePercent,
                    AvailableSample = valid,
                    DemandMiB = demandMiB,
                    DemandSample = valid
                };
            }
            else if (updated.DemandSample?.IsValid != true)
            {
                updated = updated with { DemandMiB = demandMiB, DemandSample = valid };
            }
            return updated;
        }
        catch (Exception error)
        {
            return ApplyBalancerFailure(data, new(PerformanceSampleState.Failed, observed, error.Message), dynamicEnabled);
        }
    }

    private static VmDynamicMemoryData ApplyBalancerFailure(VmDynamicMemoryData data, PerformanceSampleInfo failure, bool? dynamicEnabled)
    {
        var updated = data with
        {
            PressureSample = failure,
            GuestVisibleSample = failure,
            GuestAvailableSample = failure,
            SmartPagingSample = failure
        };
        if (dynamicEnabled != true)
        {
            updated = updated with { AvailableSample = failure, DemandSample = failure };
        }
        else if (updated.DemandSample?.IsValid != true)
        {
            updated = updated with { DemandSample = failure };
        }
        return updated;
    }

    internal static bool TryGetMemoryVmId(string? instanceId, out Guid id)
    {
        id = Guid.Empty;
        if (instanceId?.StartsWith("Microsoft:", StringComparison.OrdinalIgnoreCase) != true) return false;
        var separator = instanceId.IndexOf('\\', "Microsoft:".Length);
        return separator > 0 && Guid.TryParse(instanceId.AsSpan("Microsoft:".Length, separator - "Microsoft:".Length), out id)
            && id != Guid.Empty;
    }

    internal static VmDynamicMemoryData MapRuntimeMemory(object? assigned, object? available,
        ushort state, bool? dynamicEnabled, DateTimeOffset observed, object? availableBuffer = null)
    {
        var assignedInfo = new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observed, "未取得已分配内存。");
        var availableInfo = new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observed, "未确认动态内存配置或来宾可用内存。");
        var bufferInfo = new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observed, "未确认动态内存配置或可用内存缓冲。");
        var demandInfo = new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observed, "未取得内存需求。");
        var balancerPending = new PerformanceSampleInfo(PerformanceSampleState.Unavailable, observed, "未取得动态内存平衡器计数器。");
        long assignedMiB = 0;
        int availablePercent = 0;
        int availableBufferPercent = 0;
        long demandMiB = 0;
        if (VmMapper.MapState(state) is VmState.PoweredOff or VmState.Saved or VmState.FastSaved or VmState.Hibernated)
        {
            var notApp = new PerformanceSampleInfo(PerformanceSampleState.NotApplicable, observed);
            return new()
            {
                AssignedSample = notApp,
                AvailableSample = notApp,
                AvailableBufferSample = notApp,
                DemandSample = notApp,
                PressureSample = notApp,
                GuestVisibleSample = notApp,
                GuestAvailableSample = notApp,
                SmartPagingSample = notApp
            };
        }
        if (!VmMapper.IsActiveState(state))
        {
            return new()
            {
                AssignedSample = assignedInfo,
                AvailableSample = availableInfo,
                AvailableBufferSample = bufferInfo,
                DemandSample = demandInfo,
                PressureSample = balancerPending,
                GuestVisibleSample = balancerPending,
                GuestAvailableSample = balancerPending,
                SmartPagingSample = balancerPending
            };
        }

        if (assigned != null)
        {
            try
            {
                assignedMiB = Convert.ToInt64(assigned);
                assignedInfo = assignedMiB > 0
                    ? new(PerformanceSampleState.Valid, observed)
                    : new(PerformanceSampleState.Unavailable, observed, "运行中的虚拟机未报告正数已分配内存。");
            }
            catch (Exception error) { assignedInfo = new(PerformanceSampleState.Failed, observed, error.Message); }
        }
        if (dynamicEnabled == false)
        {
            availableInfo = new(PerformanceSampleState.NotApplicable, observed, "未启用动态内存，此字段不适用。");
            bufferInfo = availableInfo;
        }
        else if (dynamicEnabled == true)
        {
            if (available != null)
            {
                try
                {
                    availablePercent = Convert.ToInt32(available);
                    // Microsoft documents negative MemoryAvailable as demand exceeding assigned memory.
                    availableInfo = availablePercent <= 100
                        ? new(PerformanceSampleState.Valid, observed)
                        : new(PerformanceSampleState.Unavailable, observed, "来宾可用内存比例超出有效范围。");
                }
                catch (Exception error) { availableInfo = new(PerformanceSampleState.Failed, observed, error.Message); }
            }
            if (availableBuffer != null)
            {
                try
                {
                    int rawBuffer = Convert.ToInt32(availableBuffer);
                    if (rawBuffer != int.MaxValue)
                    {
                        availableBufferPercent = rawBuffer;
                        bufferInfo = new(PerformanceSampleState.Valid, observed);
                    }
                    else
                    {
                        bufferInfo = new(PerformanceSampleState.Unavailable, observed, "可用内存缓冲尚未就绪。");
                    }
                }
                catch (Exception error) { bufferInfo = new(PerformanceSampleState.Failed, observed, error.Message); }
            }
            if (assignedInfo.IsValid && availableInfo.IsValid)
            {
                demandMiB = Math.Max(0L, checked((long)decimal.Round(
                    assignedMiB * (100m - availablePercent) / 100m, 0, MidpointRounding.AwayFromZero)));
                demandInfo = new(PerformanceSampleState.Valid, observed);
            }
        }
        return new()
        {
            AssignedMiB = assignedInfo.IsValid ? assignedMiB : 0,
            AvailablePercent = availableInfo.IsValid ? availablePercent : 0,
            AvailableBufferPercent = bufferInfo.IsValid ? availableBufferPercent : 0,
            DemandMiB = demandInfo.IsValid ? demandMiB : 0,
            AssignedSample = assignedInfo,
            AvailableSample = availableInfo,
            AvailableBufferSample = bufferInfo,
            DemandSample = demandInfo,
            PressureSample = balancerPending,
            GuestVisibleSample = balancerPending,
            GuestAvailableSample = balancerPending,
            SmartPagingSample = balancerPending
        };
    }
}
