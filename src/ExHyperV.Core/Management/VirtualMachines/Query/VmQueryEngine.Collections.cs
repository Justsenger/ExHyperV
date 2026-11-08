using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed partial class VmQueryEngine
{
    private static RawRow Row(string key, Guid ownerId, params (string Name, object? Value)[] values)
    {
        var data = values.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        data["@Owner"] = ownerId;
        return new(key, data);
    }
    private static ReadResult Done(IEnumerable<RawRow> rows) => new(rows.ToArray(), VmQueryValueState.Available, DateTimeOffset.UtcNow);
    private static ReadResult Forward<T>(ApiResponse<T> result) => new([], result.ErrorKind == ApiErrorKind.Unsupported
        ? VmQueryValueState.Unsupported : VmQueryValueState.Failed, DateTimeOffset.UtcNow, result.Error, ApiFailure.From(result));
    private ReadResult Ports()
    {
        var a = Read("Ports"); var b = Read("EmulatedPorts");
        if (a.State != VmQueryValueState.Available) return a;
        if (b.State != VmQueryValueState.Available && b.State != VmQueryValueState.Unsupported) return b;
        return new(a.Rows.Concat(b.Rows).ToArray(), VmQueryValueState.Available, a.Time > b.Time ? a.Time : b.Time);
    }
    private async Task<VmQueryCollectionResult> CollectionAsync(Guid id, VmQueryCollection kind, VmQueryField[] fields)
    {
        string source = kind switch
        {
            VmQueryCollection.Groups => "Groups", VmQueryCollection.Metadata => "Metadata",
            VmQueryCollection.Processors => "ProcessorPerformance", VmQueryCollection.Storage => "Storage",
            VmQueryCollection.NetworkAdapters => "Ports", VmQueryCollection.BootEntries => "Boot",
            VmQueryCollection.GpuAssignments => "Gpu", VmQueryCollection.PcieDevices => "PcieDevices",
            VmQueryCollection.Checkpoints => "Checkpoints", _ => throw new InvalidOperationException("未知查询集合。")
        };
        ReadResult basis = source == "Ports" ? Ports() : await DerivedAsync(source, id).ConfigureAwait(false);
        if (basis.State != VmQueryValueState.Available) return new(basis.State, [], basis.Error, basis.Time) { Failure = basis.Failure };
        var rows = basis.Rows.Where(r => source == "Ports" ? Owned(r, id) : r["@Owner"] is Guid g && g == id).ToArray();
        if (rows.Any(r => r.Key.Length == 0) || rows.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Length)
            return new(VmQueryValueState.Ambiguous, [], "集合含空身份或重复身份。", basis.Time);
        var output = new List<VmQueryRow>();
        foreach (var row in rows)
        {
            var cells = new Dictionary<string, VmQueryValue>();
            foreach (var field in fields)
            {
                ReadResult data = basis;
                RawRow? valueRow = row;
                if (field.Source != source)
                {
                    if (Sources.ContainsKey(field.Source))
                    {
                        data = Read(field.Source);
                        var matches = data.Rows.Where(r => r.Key.StartsWith(row.Key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
                        valueRow = matches.Length == 1 ? matches[0] : null;
                        if (matches.Length > 1) data = data with { State = VmQueryValueState.Ambiguous, Error = "存在多个同类设备功能记录。" };
                    }
                    else
                    {
                        data = await DerivedAsync(field.Source, id).ConfigureAwait(false);
                        valueRow = data.Rows.FirstOrDefault(r => r.Key.Equals(row.Key, StringComparison.OrdinalIgnoreCase));
                    }
                }
                cells[field.Id] = Cell(field, valueRow, data);
            }
            output.Add(new(row.Key, cells));
        }
        return new(VmQueryValueState.Available, output.AsReadOnly(), null, basis.Time);
    }

    private async Task<ReadResult> BuildDerivedAsync(string source, Guid id)
    {
        switch (source)
        {
            case "Groups":
                var groups = await VmGroupService.ReadAsync().ConfigureAwait(false);
                return Done(groups.Groups.SelectMany(g => g.VmIds.Where(_ids.Contains).Select(vm => Row(g.Id.ToString("D"), vm, ("Id", g.Id), ("Name", g.Name)))));
            case "Metadata": return BuildMetadata();
            case "Storage": return BuildStorage();
            case "DiskFiles": return await BuildDiskFilesAsync().ConfigureAwait(false);
            case "Connections": return BuildConnections();
            case "IpAddresses": return BuildIps();
            case "Affinity":
                var affinity = await CpuAffinityService.ReadStateAsync(id).ConfigureAwait(false);
                return affinity.HasData ? Done([Row(id.ToString("D"), id,
                    ("AppliedProcessors", affinity.Data.AppliedProcessors == null ? null : Array.AsReadOnly(affinity.Data.AppliedProcessors.ToArray())),
                    ("SavedProcessors", affinity.Data.SavedProcessors == null ? null : Array.AsReadOnly(affinity.Data.SavedProcessors.ToArray())),
                    ("SavedPreferenceStatus", affinity.Data.SavedPreferenceStatus), ("SavedPreferenceIssue", affinity.Data.SavedPreferenceIssue),
                    ("ProcessId", affinity.Data.ProcessId), ("ProcessCreationTime", affinity.Data.ProcessCreationTime))]) : Forward(affinity);
            case "Boot":
                var boot = await VmBootService.GetBootOrderAsync(id).ConfigureAwait(false);
                return boot.HasData ? Done(boot.Data.Select((b, i) => Row(id.ToString("D") + ":" + i, id,
                    ("Order", i), ("Name", b.Name), ("Kind", b.Kind), ("Description", b.Description)))) : Forward(boot);
            case "Gpu": return await BuildGpuAsync().ConfigureAwait(false);
            case "PcieDevices": return await BuildPcieAsync().ConfigureAwait(false);
            case "Checkpoints": return BuildCheckpoints();
            case "CheckpointDisks": return await BuildCheckpointDisksAsync().ConfigureAwait(false);
            case "ProcessorPerformance": return BuildProcessorPerformance();
            case "GpuPerformance": return await BuildGpuPerformanceAsync().ConfigureAwait(false);
            case "NetworkPerformance": return await BuildNetworkPerformanceAsync().ConfigureAwait(false);
            case "DiskPerformance": return await BuildDiskPerformanceAsync().ConfigureAwait(false);
            default: throw new InvalidOperationException("读取阶段未实现：" + source);
        }
    }

    private ReadResult BuildMetadata()
    {
        var settings = Read("Settings"); var data = Read("MetadataRaw");
        if (settings.State != VmQueryValueState.Available) return settings;
        if (data.State != VmQueryValueState.Available) return data;
        var rows = new List<RawRow>();
        foreach (var id in _ids)
        {
            var config = settings.Rows.SingleOrDefault(r => Owned(r, id));
            if (config == null) throw new InvalidDataException("未找到虚拟机当前配置。");
            var kvp = data.Rows.SingleOrDefault(r => r.Key.Equals(config.Text("InstanceID") + "\\" + VmMetadataService.ComponentId, StringComparison.OrdinalIgnoreCase));
            if (kvp == null) throw new InvalidDataException("未找到当前配置的 HostOnlyItems 组件。");
            foreach (var pair in VmMetadataService.ParseItems(kvp.Strings("HostOnlyItems")))
                rows.Add(Row(id + ":" + pair.Key, id, ("Key", pair.Key), ("Value", pair.Value)));
        }
        return Done(rows);
    }

    private ReadResult BuildConnections()
    {
        var ports = Ports(); var allocations = Read("Allocations"); var switches = Read("Switches");
        if (ports.State != VmQueryValueState.Available) return ports;
        if (allocations.State != VmQueryValueState.Available) return allocations;
        return Done(ports.Rows.Select(port =>
        {
            var a = allocations.Rows.Where(r => r.Key.StartsWith(port.Key + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (a.Length > 1) throw new InvalidDataException("同一网卡存在多个连接资源。");
            values["IsConnected"] = a.Length == 0 ? false : a[0]["EnabledState"] == null ? null : Convert.ToUInt16(a[0]["EnabledState"]) == 2;
            string switchId = a.Length == 0 ? "" : RefId(a[0].Strings("HostResource").FirstOrDefault() ?? "", "Name");
            values["SwitchId"] = Guid.TryParse(switchId, out var sid) ? sid : VmNetworkFields.SwitchId.Make(VmQueryValueState.NotApplicable, null, allocations.Time, "没有连接目标交换机。");
            values["SwitchName"] = switchId.Length == 0 ? VmNetworkFields.SwitchName.Make(VmQueryValueState.NotApplicable, null, allocations.Time, "没有连接目标交换机。")
                : switches.State != VmQueryValueState.Available ? Failure(VmNetworkFields.SwitchName, switches)
                : switches.Rows.FirstOrDefault(s => s.Key.Equals(switchId, StringComparison.OrdinalIgnoreCase))?["ElementName"];
            return new RawRow(port.Key, values);
        }));
    }

    private ReadResult BuildIps()
    {
        var ports = Ports(); var guest = Read("GuestIp"); var neighbors = Read("Neighbors");
        if (ports.State != VmQueryValueState.Available) return ports;
        var rows = new List<RawRow>();
        foreach (var port in ports.Rows)
        {
            string mac = MacAddress.Format(port.Text("Address"));
            string key = port.Key.Split('\\').Last();
            var matches = guest.Rows.Where(g => g.Key.Split('\\').Last().Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] ips; string source;
            if (ArpSnoopService.Instance.TryGetIp(mac, out var observed)) { ips = [observed]; source = "HostArp"; }
            else if (matches.SelectMany(g => g.Strings("IPAddresses")).Any()) { ips = matches.SelectMany(g => g.Strings("IPAddresses")).Distinct().ToArray(); source = "Guest"; }
            else { ips = neighbors.Rows.Where(n => MacAddress.Format(n.Text("LinkLayerAddress")) == mac).Select(n => n.Text("IPAddress")).Where(a => a.Length > 0).Distinct().ToArray(); source = ips.Length > 0 ? "NeighborCache" : "None"; }
            var data = new Dictionary<string, object?> { ["IpAddresses"] = Array.AsReadOnly(ips), ["IpAddressSource"] = source };
            if (ips.Length == 0 && (guest.State != VmQueryValueState.Available || neighbors.State != VmQueryValueState.Available))
            {
                var failed = guest.State != VmQueryValueState.Available ? guest : neighbors;
                data["IpAddresses"] = Failure(VmNetworkFields.IpAddresses, failed);
                data["IpAddressSource"] = Failure(VmNetworkFields.IpAddressSource, failed);
            }
            rows.Add(new(port.Key, data));
        }
        return Done(rows);
    }

    private async Task<ReadResult> BuildGpuAsync()
    {
        var raw = Read("GpuRaw");
        if (raw.State != VmQueryValueState.Available) return raw;
        if (!raw.Rows.Any(r => _ids.Any(id => Owned(r, id)))) return Done([]);
        var host = await HostGpuService.GetHostGpusAsync().ConfigureAwait(false);
        var rows = new List<RawRow>();
        foreach (var id in _ids)
        {
            var adapters = raw.Rows.Where(r => Owned(r, id)).ToArray();
            foreach (var adapter in adapters)
            {
                string resource = adapter.Strings("HostResource").FirstOrDefault() ?? "";
                string name = RefId(resource, "Name");
                if (name.Length == 0) name = resource;
                var assignment = VmGpuService.BuildGpuAssignments([(adapter.Key, name)], host.Data ?? []).Single();
                var row = Row(adapter.Key, id, ("HostDeviceInstanceId", assignment.HostDeviceInstanceId), ("Name", assignment.Name),
                    ("Manu", assignment.Manu), ("Vendor", assignment.Vendor), ("DriverVersion", assignment.DriverVersion), ("MemoryBytes", assignment.MemoryBytes));
                foreach (var field in request.Fields.Where(f => f.Source == "Gpu"))
                {
                    if (!host.HasData) row.Values[field.Property] = Failure(field, Forward(host));
                    else if (assignment.HostDeviceInstanceId.Length == 0 || field == VmGpuFields.MemoryBytes && !assignment.MemoryBytes.HasValue)
                        row.Values[field.Property] = field.Make(VmQueryValueState.Unavailable, null, raw.Time, "未能确认宿主 GPU 对应关系或容量；不以首张卡代替。");
                }
                rows.Add(row);
            }
        }
        return Done(rows);
    }

    private async Task<ReadResult> BuildPcieAsync()
    {
        var raw = Read("PcieRaw");
        if (raw.State != VmQueryValueState.Available) return raw;
        if (!raw.Rows.Any(r => _ids.Any(id => Owned(r, id)))) return Done([]);
        var host = await HostPcieService.GetPCIeInfoAsync().ConfigureAwait(false);
        var rows = new List<RawRow>();
        foreach (var id in _ids)
            foreach (var device in raw.Rows.Where(r => Owned(r, id)))
            {
                string key = VmPcieService.NormalizePhysicalDeviceId(VmPcieService.ExtractPhysicalDeviceId(device.Strings("HostResource").FirstOrDefault() ?? ""));
                var match = host.Data?.Devices.FirstOrDefault(d => VmPcieService.NormalizePhysicalDeviceId(d.InstanceId).Equals(key, StringComparison.OrdinalIgnoreCase));
                var row = Row(device.Key, id, ("DeviceInstanceId", match?.InstanceId), ("Name", match?.FriendlyName),
                    ("ClassType", match?.ClassType), ("Vendor", match?.Vendor), ("Path", match?.Path),
                    ("GuestMode", device["GuestPciExpressMode"] == null ? null : (VmPcieGuestMode)Convert.ToByte(device["GuestPciExpressMode"])));
                if (!host.HasData) foreach (var field in request.Fields.Where(f => f.Source == "PcieDevices" && f != VmPcieDeviceFields.GuestMode)) row.Values[field.Property] = Failure(field, Forward(host));
                rows.Add(row);
            }
        return Done(rows);
    }

    private ReadResult BuildCheckpoints()
    {
        var raw = Read("CheckpointsRaw");
        if (raw.State != VmQueryValueState.Available) return raw;
        var result = new List<RawRow>();
        foreach (var id in _ids)
        {
            var nodes = raw.Rows.Where(r => r.Text("VirtualSystemIdentifier").Equals(id.ToString("D"), StringComparison.OrdinalIgnoreCase)
                && (r.Text("VirtualSystemType").Contains("Snapshot", StringComparison.Ordinal) || r.Text("VirtualSystemType") == "Microsoft:Hyper-V:System:Realized")).ToArray();
            var current = nodes.SingleOrDefault(r => r.Text("VirtualSystemType") == "Microsoft:Hyper-V:System:Realized");
            foreach (var node in nodes)
            {
                string nodeId = node.Text("ConfigurationID");
                string parent = RefId(node.Text("Parent"));
                string? parentId = nodes.FirstOrDefault(r => r.Key.Equals(parent, StringComparison.OrdinalIgnoreCase))?.Text("ConfigurationID");
                var row = Row(node.Key, id, ("Id", nodeId), ("Name", node["ElementName"]),
                    ("CreatedDate", node["CreationTime"] == null ? null : ManagementDateTimeConverter.ToDateTime(node.Text("CreationTime"))),
                    ("ParentId", parentId ?? ""), ("NodeType", node == current ? SpacetimeNodeType.Current : SpacetimeNodeType.Snapshot),
                    ("IsCurrent", node == current || current != null && RefId(current.Text("Parent")).Equals(node.Key, StringComparison.OrdinalIgnoreCase)));
                result.Add(row);
            }
        }
        return Done(result);
    }

    private async Task<ReadResult> BuildCheckpointDisksAsync()
    {
        var nodes = await DerivedAsync("Checkpoints").ConfigureAwait(false);
        var disks = Read("Sasd");
        if (nodes.State != VmQueryValueState.Available) return nodes;
        if (disks.State != VmQueryValueState.Available) return disks;
        var rows = new List<RawRow>();
        foreach (var node in nodes.Rows)
        {
            var paths = disks.Rows.Where(d => d.Key.StartsWith(node.Key + "\\", StringComparison.OrdinalIgnoreCase)).SelectMany(d => d.Strings("HostResource")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            object path = Array.AsReadOnly(paths);
            // Reuse the read-only domain check only when attachment status is requested.
            object? attached = null;
            if (request.Fields.Contains(VmCheckpointFields.IsWormhole))
            {
                var id = (Guid)node["@Owner"]!;
                bool hasTemporaryAttachment = disks.Rows.Where(r => Owned(r, id)).SelectMany(r => r.Strings("HostResource"))
                    .Any(p => p.Contains("_wormhole_tmp", StringComparison.OrdinalIgnoreCase));
                if (!hasTemporaryAttachment) attached = false;
                else
                {
                    var state = await GetCheckpointStateAsync(id).ConfigureAwait(false);
                    attached = state.State == VmQueryValueState.Available
                        ? state.Rows.FirstOrDefault(r => r.Key == node.Text("Id"))?["IsWormhole"] : Failure(VmCheckpointFields.IsWormhole, state);
                }
            }
            rows.Add(new(node.Key, new() { ["DiskPaths"] = path, ["IsWormhole"] = attached }));
        }
        return Done(rows);
    }
    private readonly ConcurrentDictionary<Guid, Lazy<Task<ReadResult>>> _checkpointStates = new();
    private Task<ReadResult> GetCheckpointStateAsync(Guid id) => _checkpointStates.GetOrAdd(id, vm => new(async () =>
    {
        try { return Done((await VmSpacetimeService.ReadSpacetimeStateAsync(vm).ConfigureAwait(false)).Select(n => Row(n.Id, vm, ("IsWormhole", n.IsWormhole)))); }
        catch (Exception error) { return new([], VmQueryValueState.Failed, DateTimeOffset.UtcNow, error.Message); }
    })).Value;
}
