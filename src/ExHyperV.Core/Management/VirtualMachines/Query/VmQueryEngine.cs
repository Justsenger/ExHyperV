using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// Each request owns its plan, raw snapshots and memoized domain reads. No live WMI object escapes.
internal sealed partial class VmQueryEngine(VmQueryService owner, VmQueryRequest request, CancellationToken token)
{
    internal sealed record RawRow(string Key, Dictionary<string, object?> Values)
    {
        internal object? this[string key] => Values.GetValueOrDefault(key);
        internal string Text(string key) => this[key]?.ToString() ?? "";
        internal string[] Strings(string key) => this[key] is string[] a ? a : this[key] is string s ? [s] : [];
    }
    internal sealed record ReadResult(IReadOnlyList<RawRow> Rows, VmQueryValueState State, DateTimeOffset Time, string? Error = null, ApiFailure? Failure = null);
    private sealed record Source(string Class, string Key, string Scope, string? Predicate = null, bool TargetByPrefix = false);
    private readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ReadResult>> _reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<ReadResult>>> _derived = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<VmQueryTiming> _timings = new();
    private readonly SemaphoreSlim _readGate = new(4, 4);
    private readonly SemaphoreSlim _domainGate = new(4, 4);
    private Guid[] _ids = [];
    private ReadResult _summary = new([], VmQueryValueState.Unavailable, default);
    private readonly Dictionary<Guid, VmQueryService.VmDynamicMemoryData> _fixedMemorySamples = new();

    private static readonly IReadOnlyDictionary<string, Source> Sources = new Dictionary<string, Source>
    {
        ["Summary"] = new("Msvm_SummaryInformation", "Name", WmiScope.HyperV),
        ["Settings"] = new("Msvm_VirtualSystemSettingData", "VirtualSystemIdentifier", WmiScope.HyperV, "VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'"),
        ["Processor"] = new("Msvm_ProcessorSettingData", "InstanceID", WmiScope.HyperV, "ResourceType = 3", true),
        ["Memory"] = new("Msvm_MemorySettingData", "InstanceID", WmiScope.HyperV, "ResourceType = 4", true),
        ["Computer"] = new("Msvm_ComputerSystem", "Name", WmiScope.HyperV, "Caption = 'Virtual Machine'"),
        ["Display"] = new("Msvm_SyntheticDisplayControllerSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["Security"] = new("Msvm_SecuritySettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["Battery"] = new("Msvm_BatterySettingData", "InstanceID", WmiScope.HyperV),
        ["PcieSystem"] = new("Msvm_VirtualSystemPciExpressSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["PcieRaw"] = new("Msvm_PciExpressSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["MetadataRaw"] = new("Msvm_KvpExchangeComponentSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["Ports"] = new("Msvm_SyntheticEthernetPortSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["EmulatedPorts"] = new("Msvm_EmulatedEthernetPortSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["Allocations"] = new("Msvm_EthernetPortAllocationSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["Switches"] = new("Msvm_VirtualEthernetSwitch", "Name", WmiScope.HyperV),
        ["NetSecurity"] = new("Msvm_EthernetSwitchPortSecuritySettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["NetOffload"] = new("Msvm_EthernetSwitchPortOffloadSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["NetVlan"] = new("Msvm_EthernetSwitchPortVlanSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["NetBandwidth"] = new("Msvm_EthernetSwitchPortBandwidthSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["GuestIp"] = new("Msvm_GuestNetworkAdapterConfiguration", "InstanceID", WmiScope.HyperV),
        ["Neighbors"] = new("MSFT_NetNeighbor", "InstanceID", WmiScope.StdCimV2, "AddressFamily = 2 AND State <> 0"),
        ["Rasd"] = new("Msvm_ResourceAllocationSettingData", "InstanceID", WmiScope.HyperV, "ResourceType = 5 OR ResourceType = 6 OR ResourceType = 16 OR ResourceType = 17 OR ResourceType = 22", true),
        ["Sasd"] = new("Msvm_StorageAllocationSettingData", "InstanceID", WmiScope.HyperV, "ResourceType = 31 OR ResourceType = 16 OR ResourceType = 22", true),
        ["HvDisks"] = new("Msvm_DiskDrive", "DeviceID", WmiScope.HyperV),
        ["HostDisks"] = new("Win32_DiskDrive", "Index", WmiScope.CimV2),
        ["GpuRaw"] = new("Msvm_GpuPartitionSettingData", "InstanceID", WmiScope.HyperV, null, true),
        ["ProcessorPerf"] = new("Win32_PerfFormattedData_HvStats_HyperVHypervisorVirtualProcessor", "Name", WmiScope.CimV2),
        ["NetworkPerf"] = new("Win32_PerfFormattedData_NvspNicStats_HyperVVirtualNetworkAdapter", "Name", WmiScope.CimV2),
        ["CheckpointsRaw"] = new("Msvm_VirtualSystemSettingData", "InstanceID", WmiScope.HyperV),
    };

    private void Need(string source, params string[] columns)
    {
        if (!_columns.TryGetValue(source, out var set)) _columns.Add(source, set = new(StringComparer.OrdinalIgnoreCase));
        set.Add(Sources[source].Key);
        foreach (string column in columns) set.Add(column);
    }

    internal void Plan()
    {
        Need("Summary", "Name");
        foreach (var field in request.Fields)
        {
            string source = field.Source;
            if (Sources.ContainsKey(source) && source != "Battery") Need(source, field.Property);
            if (field.Collection == VmQueryCollection.NetworkAdapters)
            {
                Need("Ports", "InstanceID", "Address", "ElementName");
                Need("EmulatedPorts", "InstanceID", "Address", "ElementName");
                if (source == "Ports") Need("EmulatedPorts", field.Property);
            }
            if (field.Collection == VmQueryCollection.Storage || source == "CheckpointDisks")
            {
                Need("Rasd", "ResourceType", "Parent", "AddressOnParent", "HostResource", "ResourceSubType");
                Need("Sasd", "ResourceType", "Parent", "AddressOnParent", "HostResource", "ResourceSubType");
            }
            switch (source)
            {
                case "VmFiles":
                    if (field == VmIdentityFields.GuestStateFilePath)
                        Need("Settings", "GuestStateDataRoot", "GuestStateFile");
                    else
                        Need("Settings", "ConfigurationDataRoot", "ConfigurationFile");
                    break;
                case "RuntimeMemory":
                    Need("Summary", "ElementName", "EnabledState", "UpTime", "MemoryUsage", "MemoryAvailable");
                    Need("Memory", "DynamicMemoryEnabled"); break;
                case "Metadata":
                    Need("Settings", "InstanceID"); Need("MetadataRaw", "HostOnlyItems"); break;
                case "DisplayPresence": Need("Display", "InstanceID"); break;
                case "Battery": Need("Battery", "InstanceID"); break;
                case "Connections": Need("Allocations", "EnabledState", "HostResource");
                    if (field == VmNetworkFields.SwitchName) Need("Switches", "ElementName"); break;
                case "IpAddresses": Need("GuestIp", "IPAddresses"); Need("Neighbors", "LinkLayerAddress", "IPAddress"); break;
                case "ProcessorPerformance": Need("Summary", "ElementName", "EnabledState", "NumberOfProcessors");
                    Need("ProcessorPerf", "PercentTotalRunTime"); break;
                case "NetworkPerformance":
                    Need("Summary", "EnabledState", "ElementName"); Need("NetworkPerf", "BytesSentPersec", "BytesReceivedPersec"); break;
                case "GpuPerformance":
                    Need("Summary", "EnabledState", "ElementName"); Need("GpuRaw", "HostResource"); break;
                case "DiskPerformance":
                    Need("Summary", "EnabledState", "ElementName"); break;
                case "Gpu": Need("GpuRaw", "HostResource"); break;
                case "PcieDevices": Need("PcieRaw", "HostResource", "GuestPciExpressMode"); break;
                case "Checkpoints": case "CheckpointDisks":
                    Need("CheckpointsRaw", "VirtualSystemIdentifier", "ConfigurationID", "ElementName", "CreationTime", "Parent", "VirtualSystemType"); break;
            }
        }
        if (request.Fields.Any(f => f.Collection == VmQueryCollection.Storage && f.Property is "DiskNumber" or "DiskModel" or "SerialNumber" or "PnpDeviceId" or "IsPassthroughStale" or "CapacityBytes" or "FileSizeBytes" or "ReadBytesPerSecond" or "WriteBytesPerSecond"))
        { Need("HvDisks", "DriveNumber"); Need("HostDisks", "Model", "Size", "SerialNumber", "PNPDeviceID"); }
        if (request.Fields.Contains(VmMemoryFields.BackingPageSize)) Need("Memory", "HugePagesEnabled");
    }

    internal async Task<ApiResponse<VmQueryResult>> ExecuteAsync()
    {
        var start = DateTimeOffset.UtcNow;
        if (request.VmIds?.Count == 0) return ApiResponse<VmQueryResult>.Ok(new([], [], start, start, []));
        Plan();
        try
        {
            // Start expensive independent providers early so short reads can overlap their wait.
            // The bound stays fixed: adding fields must not create unbounded WMI fan-out.
            foreach (var pair in _columns.OrderBy(p => p.Key switch
                { "Summary" => 0, "GuestIp" => 1, "ProcessorPerf" => 2, "NetworkPerf" => 3, "Rasd" => 4, "Sasd" => 5, _ => 6 }))
                _reads.Add(pair.Key, ReadAsync(pair.Key, pair.Value));
            await Task.WhenAll(_reads.Values).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _summary = await _reads["Summary"].ConfigureAwait(false);
            if (_summary.State != VmQueryValueState.Available)
                return ApiResponse<VmQueryResult>.Fail(_summary.Error ?? "虚拟机身份枚举失败。", _summary.Failure?.Code ?? -1, _summary.Failure?.Source ?? ApiErrorSource.None, kind: _summary.Failure?.Kind ?? ApiErrorKind.OperationFailed);
            var ids = _summary.Rows.Select(r => Guid.TryParse(r.Key, out var id) ? id : Guid.Empty).ToArray();
            if (ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
                return ApiResponse<VmQueryResult>.Fail("虚拟机摘要含无效或重复 GUID。", kind: ApiErrorKind.AmbiguousTarget);
            _ids = ids.Where(id => request.VmIds == null || request.VmIds.Contains(id)).OrderBy(id => id).ToArray();
            await ReadFixedMemorySamplesAsync().ConfigureAwait(false);
            var rows = await Task.WhenAll(_ids.Select(ProjectVmAsync)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return ApiResponse<VmQueryResult>.Ok(new(Array.AsReadOnly(rows), Array.AsReadOnly((request.VmIds ?? []).Except(ids).ToArray()), start,
                DateTimeOffset.UtcNow, Array.AsReadOnly(_timings.ToArray())));
        }
        finally
        {
            // Never abandon started native calls when cancelled or when one projection fails.
            try
            {
                try { await Task.WhenAll(_reads.Values).ConfigureAwait(false); } catch (OperationCanceledException) { }
                try { await Task.WhenAll(_derived.Values.Where(l => l.IsValueCreated).Select(l => l.Value)).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            finally { _readGate.Dispose(); _domainGate.Dispose(); }
        }
    }

    private async Task<ReadResult> ReadAsync(string name, HashSet<string> columns)
    {
        await _readGate.WaitAsync(token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            Source source = Sources[name];
            string? where = source.Predicate;
            // Only GUIDs produced by the request are interpolated. No user WQL is accepted.
            bool networkPerformance = request.Fields.Any(f => f.Source == "NetworkPerformance");
            bool globalIdentity = name == "Summary" && (networkPerformance || request.Fields.Any(f => f.Source == "ProcessorPerformance")
                || request.Fields.Contains(VmMemoryFields.AvailablePercent));
            bool globalAdapters = networkPerformance && name is "Ports" or "EmulatedPorts";
            bool historicalDisks = name == "Sasd" && request.Fields.Any(f => f.Source == "CheckpointDisks");
            if (!globalIdentity && !globalAdapters && !historicalDisks && request.VmIds is { Count: > 0 } targets && (source.TargetByPrefix || name is "Summary" or "Settings" or "Computer"))
            {
                string clause = string.Join(" OR ", targets.Select(id => source.TargetByPrefix
                    ? $"{source.Key} LIKE 'Microsoft:{id:D}%'" : $"{source.Key} = '{id:D}'"));
                where = where == null ? "(" + clause + ")" : "(" + where + ") AND (" + clause + ")";
            }
            string Query(IEnumerable<string> selected) => "SELECT " + string.Join(", ", selected.OrderBy(s => s, StringComparer.Ordinal)) + " FROM " + source.Class + (where == null ? "" : " WHERE " + where);
            var result = await WmiApi.QueryAsync(Query(columns), CopyRow, source.Scope).ConfigureAwait(false);
            if (!result.Success && result.Code == (int)ManagementStatus.InvalidQuery)
            {
                // Old providers may not know a newly added optional property. Discover schema, not data via SELECT *.
                var existing = await Task.Run(() =>
                {
                    using var cls = new ManagementClass(new ManagementScope(source.Scope), new ManagementPath(source.Class), null);
                    cls.Get();
                    return cls.Properties.Cast<PropertyData>().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                result = await WmiApi.QueryAsync(Query(columns.Where(existing.Contains)), CopyRow, source.Scope).ConfigureAwait(false);
            }
            return result.HasData ? new(result.Data.AsReadOnly(), VmQueryValueState.Available, DateTimeOffset.UtcNow)
                : new([], result.Code == (int)ManagementStatus.InvalidClass ? VmQueryValueState.Unsupported : VmQueryValueState.Failed,
                    DateTimeOffset.UtcNow, result.Error, ApiFailure.From(result));

            RawRow CopyRow(ManagementObject obj)
            {
                var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in obj.Properties)
                    if (columns.Contains(property.Name)) data[property.Name] = property.Value is Array a ? a.Clone() : property.Value;
                return new(data.GetValueOrDefault(source.Key)?.ToString() ?? "", data);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new([], VmQueryValueState.Failed, DateTimeOffset.UtcNow, ex.Message, ApiFailure.From(ex)); }
        finally { _timings.Enqueue(new(name, watch.Elapsed)); _readGate.Release(); }
    }

    private ReadResult Read(string name) => _reads.TryGetValue(name, out var read) ? read.GetAwaiter().GetResult()
        : new([], VmQueryValueState.NotRequested, default);
    private static bool Owned(RawRow row, Guid id) => row.Key.StartsWith("Microsoft:" + id.ToString("D") + "\\", StringComparison.OrdinalIgnoreCase)
        || string.Equals(row.Key, id.ToString("D"), StringComparison.OrdinalIgnoreCase);
    private static string RefId(string path, string property = "InstanceID")
    {
        var match = Regex.Match(path, "\\b" + property + "=\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"") : "";
    }
    private static VmQueryValueState SampleState(PerformanceSampleInfo? sample) => sample?.State switch
    {
        PerformanceSampleState.Valid => VmQueryValueState.Available,
        PerformanceSampleState.WarmingUp => VmQueryValueState.WarmingUp,
        PerformanceSampleState.Unsupported => VmQueryValueState.Unsupported,
        PerformanceSampleState.NotApplicable => VmQueryValueState.NotApplicable,
        PerformanceSampleState.Ambiguous => VmQueryValueState.Ambiguous,
        PerformanceSampleState.Failed => VmQueryValueState.Failed,
        _ => VmQueryValueState.Unavailable
    };
    private static VmQueryValue Failure(VmQueryField field, ReadResult source)
    {
        var value = field.Make(source.State, null, source.Time, source.Error);
        value.Failure = source.Failure;
        return value;
    }
    private static VmQueryValue Cell(VmQueryField field, RawRow? row, ReadResult source)
    {
        if (source.State != VmQueryValueState.Available) return Failure(field, source);
        if (row == null) return field.Make(VmQueryValueState.Unavailable, null, source.Time, "未找到当前对象记录。");
        if (row[field.Property] is VmQueryValue cell) return cell;
        if (!row.Values.TryGetValue(field.Property, out var raw)) return field.Make(VmQueryValueState.Unsupported, null, source.Time, "当前提供者没有该属性。");
        if (raw == null) return field.Make(VmQueryValueState.Unavailable, null, source.Time, "提供者返回空值。");
        try
        {
            object? value = field.ConvertValue != null ? field.ConvertValue(raw)
                : field.ValueType.IsInstanceOfType(raw) ? raw
                : field.ValueType.IsEnum ? Enum.ToObject(field.ValueType, raw)
                : Convert.ChangeType(raw, field.ValueType, CultureInfo.InvariantCulture);
            if (field.Source == "Processor" && value is uint u && u == uint.MaxValue && field.Property is
                "PerfCpuFreqCapMhz" or "PerfCpuFreqMinMhz" or "PerfCpuFreqDesiredMhz" or "PerfCpuEnergyPerformancePreference"
                or "PerfCpuAutonomousActivityWindow" or "MaxHwIsolatedGuests" or "MaxClusterCountPerSocket" or "MaxProcessorCountPerL3" or "PhysicalAddressWidth")
                return field.Make(VmQueryValueState.Unavailable, null, source.Time, "提供者以最大无符号整数表示未设置。");
            return field.Make(VmQueryValueState.Available, value, source.Time);
        }
        catch (Exception error) { return field.Make(VmQueryValueState.Failed, null, source.Time, "字段转换失败：" + error.Message); }
    }

    private async Task<VmQueryRow> ProjectVmAsync(Guid id)
    {
        var values = new Dictionary<string, VmQueryValue>();
        var collections = new Dictionary<VmQueryCollection, VmQueryCollectionResult>();
        foreach (var group in request.Fields.GroupBy(f => f.Collection))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key == VmQueryCollection.VirtualMachine)
                foreach (var field in group) values[field.Id] = await ScalarAsync(id, field).ConfigureAwait(false);
            else collections[group.Key] = await CollectionAsync(id, group.Key, group.ToArray()).ConfigureAwait(false);
        }
        return new(id.ToString("D"), values, collections);
    }

    private async Task<VmQueryValue> ScalarAsync(Guid id, VmQueryField field)
    {
        string sourceName = field.Source;
        if (sourceName == "VmFiles") return VmFilePath(id, field);
        if (sourceName == "RuntimeMemory")
        {
            var summary = _summary.Rows.Single(r => Guid.Parse(r.Key) == id);
            var memory = Read("Memory");
            var rows = memory.Rows.Where(r => Owned(r, id)).ToArray();
            bool? dynamic = rows.Length == 1 ? rows[0]["DynamicMemoryEnabled"] as bool? : null;
            var data = _fixedMemorySamples.TryGetValue(id, out var fixedSample) ? fixedSample
                : VmQueryService.MapRuntimeMemory(summary["MemoryUsage"], summary["MemoryAvailable"],
                    Convert.ToUInt16(summary["EnabledState"] ?? 0), dynamic, _summary.Time);
            var sample = field == VmMemoryFields.AssignedMiB ? data.AssignedSample : data.AvailableSample;
            if (field == VmMemoryFields.AvailablePercent && memory.State != VmQueryValueState.Available) return Failure(field, memory);
            return field.Make(SampleState(sample), field == VmMemoryFields.AssignedMiB ? (object)data.AssignedMiB : data.AvailablePercent, sample?.ObservedAtUtc ?? _summary.Time, sample?.Error);
        }
        if (sourceName == "DisplayPresence")
        {
            var display = Read("Display");
            return display.State == VmQueryValueState.Available ? field.Make(VmQueryValueState.Available, display.Rows.Any(r => Owned(r, id)), display.Time) : Failure(field, display);
        }
        if (sourceName == "Battery")
        {
            var battery = Read("Battery");
            if (battery.State != VmQueryValueState.Available) return Failure(field, battery);
            if (!battery.Rows.Any(r => r.Key.Contains("Default", StringComparison.OrdinalIgnoreCase))) return field.Make(VmQueryValueState.Unsupported, null, battery.Time, "没有虚拟电池默认模板。");
            return field.Make(VmQueryValueState.Available, battery.Rows.Any(r => Owned(r, id)), battery.Time);
        }
        if (sourceName is "Affinity" or "GpuPerformance" or "ProcessorPerformance")
        {
            var result = await DerivedAsync(sourceName, id).ConfigureAwait(false);
            return Cell(field, result.Rows.FirstOrDefault(r => r.Key == id.ToString("D")), result);
        }
        var source = Read(sourceName);
        var matched = source.Rows.Where(r => Owned(r, id)).ToArray();
        if (matched.Length > 1) return field.Make(VmQueryValueState.Ambiguous, null, source.Time, "当前配置包含多个同类记录，不能唯一选择。");
        if (field == VmMemoryFields.BackingPageSize && matched.Length == 1 && !matched[0].Values.ContainsKey("BackingPageSize") && matched[0]["HugePagesEnabled"] is bool huge)
            return field.Make(VmQueryValueState.Available, huge ? (byte)2 : (byte)1, source.Time);
        return Cell(field, matched.FirstOrDefault(), source);
    }

    private async Task ReadFixedMemorySamplesAsync()
    {
        if (!request.Fields.Contains(VmMemoryFields.AvailablePercent)) return;
        var memory = Read("Memory");
        if (memory.State != VmQueryValueState.Available) return;
        var before = _summary.Rows.Select(row => new VmQueryService.MemoryRuntimeSummary(row.Key,
            row.Text("ElementName"), Convert.ToUInt16(row["EnabledState"] ?? 0), row["MemoryUsage"],
            row["MemoryAvailable"], Convert.ToUInt64(row["UpTime"] ?? 0))).ToArray();
        var targets = before.Where(row => _ids.Contains(Guid.Parse(row.Id!)) && VmMapper.IsActiveState(row.State)
            && memory.Rows.Count(setting => Owned(setting, Guid.Parse(row.Id!))) == 1
            && memory.Rows.Single(setting => Owned(setting, Guid.Parse(row.Id!)))["DynamicMemoryEnabled"] is false).ToArray();
        if (targets.Length == 0) return;
        foreach (var row in targets)
            _fixedMemorySamples.Add(Guid.Parse(row.Id!), VmQueryService.MapRuntimeMemory(row.Assigned,
                row.Available, row.State, false, _summary.Time));
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        try { await VmQueryService.ReadFixedMemoryDemandAsync(_fixedMemorySamples, before, targets).ConfigureAwait(false); }
        finally { _timings.Enqueue(new("FixedMemoryPerformance", watch.Elapsed)); }
    }

    private Task<ReadResult> DerivedAsync(string source, Guid id = default)
    {
        if (source is not ("Affinity" or "Boot")) id = Guid.Empty;
        string key = source is "Affinity" or "Boot" ? source + ":" + id.ToString("D") : source;
        return _derived.GetOrAdd(key, _ => new(() => ReadDerivedAsync(source, id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
    private async Task<ReadResult> ReadDerivedAsync(string source, Guid id)
    {
        bool gated = source is "Affinity" or "Boot";
        if (gated) await _domainGate.WaitAsync(token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        try { token.ThrowIfCancellationRequested(); return await BuildDerivedAsync(source, id).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new([], VmQueryValueState.Failed, DateTimeOffset.UtcNow, ex.Message, ApiFailure.From(ex)); }
        finally { _timings.Enqueue(new(source + (id == Guid.Empty ? "" : ":" + id.ToString("D")), watch.Elapsed)); if (gated) _domainGate.Release(); }
    }
}
