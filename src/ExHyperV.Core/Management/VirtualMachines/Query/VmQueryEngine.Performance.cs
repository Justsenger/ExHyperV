using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed partial class VmQueryEngine
{
    private List<VmInstance> PerformanceMachines() => _ids.Select(id =>
    {
        var raw = _summary.Rows.Single(r => r.Key.Equals(id.ToString("D"), StringComparison.OrdinalIgnoreCase));
        var gpu = Read("GpuRaw");
        return new VmInstance(id, raw.Text("ElementName")) { StateCode = Convert.ToUInt16(raw["EnabledState"] ?? 0),
            HasGpuAssignment = gpu.State == VmQueryValueState.Available ? gpu.Rows.Any(r => Owned(r, id)) : null };
    }).ToList();

    private ReadResult BuildProcessorPerformance()
    {
        var perf = Read("ProcessorPerf");
        var rows = new List<RawRow>();
        foreach (var id in _ids)
        {
            var summary = _summary.Rows.Single(r => Guid.Parse(r.Key) == id);
            string name = summary.Text("ElementName");
            bool active = VmMapper.IsActiveState(Convert.ToUInt16(summary["EnabledState"] ?? 0));
            int count = Convert.ToInt32(summary["NumberOfProcessors"] ?? 0);
            bool ambiguous = _summary.Rows.Count(r => r.Text("ElementName").Equals(name, StringComparison.OrdinalIgnoreCase)) != 1;
            var samples = new List<double>();
            VmQueryValueState overall = VmQueryValueState.Available;
            for (int i = 0; i < count; i++)
            {
                var matches = perf.Rows.Where(r => r.Key.Equals(name + ":Hv VP " + i, StringComparison.OrdinalIgnoreCase)).ToArray();
                VmQueryValueState state = !active ? VmQueryValueState.NotApplicable : ambiguous ? VmQueryValueState.Ambiguous
                    : perf.State != VmQueryValueState.Available ? perf.State : matches.Length > 1 ? VmQueryValueState.Ambiguous
                    : matches.Length == 0 || matches[0]["PercentTotalRunTime"] == null ? VmQueryValueState.Unavailable : VmQueryValueState.Available;
                double value = state == VmQueryValueState.Available ? Convert.ToDouble(matches[0]["PercentTotalRunTime"]) : 0;
                if (state == VmQueryValueState.Available && (!double.IsFinite(value) || value < 0)) state = VmQueryValueState.Unavailable;
                if (state != VmQueryValueState.Available) overall = state; else samples.Add(value);
                rows.Add(Row(id + ":" + i, id, ("Index", i), ("UsagePercent", VmProcessorMetricFields.UsagePercent.Make(state, value, perf.Time, perf.Error))));
            }
            if (count == 0) overall = VmQueryValueState.Unavailable;
            // VM aggregate is deliberately not a member of the per-processor collection.
            rows.Add(new(id.ToString("D"), new() { ["UsagePercent"] = VmProcessorFields.UsagePercent.Make(overall,
                overall == VmQueryValueState.Available ? samples.Average() : null, perf.Time, perf.Error) }));
        }
        return Done(rows);
    }

    private async Task<ReadResult> BuildGpuPerformanceAsync()
    {
        var data = await owner.SampleProjectionGpuAsync(PerformanceMachines()).ConfigureAwait(false);
        return Done(data.Select(pair =>
        {
            var g = pair.Value;
            return Row(pair.Key.ToString("D"), pair.Key,
                ("Gpu3d", Value(VmGpuPerformanceFields.Gpu3d, g.Gpu3d, g.Sample)),
                ("GpuCopy", Value(VmGpuPerformanceFields.GpuCopy, g.GpuCopy, g.Sample)),
                ("GpuEncode", Value(VmGpuPerformanceFields.GpuEncode, g.GpuEncode, g.Sample)),
                ("GpuDecode", Value(VmGpuPerformanceFields.GpuDecode, g.GpuDecode, g.Sample)),
                ("HasGpuEngineInstance", Value(VmGpuPerformanceFields.HasGpuEngineInstance, g.HasGpuEngineInstance, g.EnginePresenceSample)));
        }));
    }
    private static VmQueryValue Value(VmQueryField field, object value, PerformanceSampleInfo? sample) => field.Make(SampleState(sample), value, sample?.ObservedAtUtc ?? DateTimeOffset.UtcNow, sample?.Error);

    private Task<ReadResult> BuildNetworkPerformanceAsync()
    {
        var ports = Ports();
        if (ports.State != VmQueryValueState.Available) return Task.FromResult(ports);
        var perf = Read("NetworkPerf");
        var rows = new List<RawRow>();
        var names = perf.Rows.Select(r => r.Key).ToArray();
        // A checkpoint retains its adapter GUID. Historical configuration rows are not
        // live owners of that counter; including them creates false ownership conflicts.
        // Read all live VM identities even for a target subset to detect actual shared IDs.
        var livePorts = ports.Rows.Where(p => _summary.Rows.Any(vm => Guid.TryParse(vm.Key, out var id) && Owned(p, id))).ToArray();
        var activePorts = livePorts.Where(p => _summary.Rows.Any(vm => Guid.TryParse(vm.Key, out var id) && Owned(p, id)
            && VmMapper.IsActiveState(Convert.ToUInt16(vm["EnabledState"] ?? 0))));
        var bindings = activePorts.ToDictionary(p => p.Key, p => VmQueryService.FindNetworkCounterInstances(p.Key, names));
        foreach (var vm in PerformanceMachines())
            foreach (var port in livePorts.Where(p => Owned(p, vm.Id)))
            {
                var matches = bindings.GetValueOrDefault(port.Key) ?? [];
                bool ambiguous = matches.Length > 1 || matches.Length == 1 && bindings.Values.Count(v => v.Contains(matches[0], StringComparer.OrdinalIgnoreCase)) > 1;
                var sample = matches.Length == 1 ? perf.Rows.FirstOrDefault(p => p.Key.Equals(matches[0], StringComparison.OrdinalIgnoreCase)) : null;
                VmQueryValueState state = !vm.IsRunning ? VmQueryValueState.NotApplicable : perf.State != VmQueryValueState.Available ? perf.State
                    : ambiguous ? VmQueryValueState.Ambiguous : sample == null ? VmQueryValueState.Unavailable : VmQueryValueState.Available;
                var row = Row(port.Key, vm.Id);
                foreach (var pair in new[] { (VmNetworkFields.SendBytesPerSecond, "BytesSentPersec"), (VmNetworkFields.ReceiveBytesPerSecond, "BytesReceivedPersec") })
                {
                    object? value = sample?[pair.Item2];
                    var cellState = state == VmQueryValueState.Available && value == null ? VmQueryValueState.Unavailable : state;
                    string? reason = cellState == VmQueryValueState.Ambiguous ? "当前虚拟网卡与性能实例不能唯一关联；不使用检查点中的历史网卡作为运行时拥有者。" : perf.Error;
                    row.Values[pair.Item1.Property] = pair.Item1.Make(cellState, value == null ? null : Convert.ToUInt64(value), perf.Time, reason);
                }
                rows.Add(row);
            }
        return Task.FromResult(Done(rows));
    }

    private async Task<ReadResult> BuildDiskPerformanceAsync()
    {
        var storage = await DerivedAsync("Storage").ConfigureAwait(false);
        if (storage.State != VmQueryValueState.Available) return storage;
        var machines = PerformanceMachines();
        var identities = new Dictionary<VmDiskStatus, RawRow>();
        foreach (var vm in machines)
            foreach (var item in storage.Rows.Where(r => r["@Owner"] is Guid id && id == vm.Id))
            {
                var disk = new VmDiskStatus { Path = item.Text("Path"), DiskType = item.Text("DiskType"), PnpDeviceId = item.Text("PnpDeviceId") };
                vm.Disks.Add(disk); identities.Add(disk, item);
            }
        await owner.UpdateDiskPerformanceAsync(machines).ConfigureAwait(false);
        return Done(machines.SelectMany(vm => vm.Disks.Select(d => Row(identities[d].Key, vm.Id,
            ("ReadBytesPerSecond", Value(VmStorageFields.ReadBytesPerSecond, d.ReadBytesPerSecond, d.PerformanceSample)),
            ("WriteBytesPerSecond", Value(VmStorageFields.WriteBytesPerSecond, d.WriteBytesPerSecond, d.PerformanceSample))))));
    }
}
