using System.Text.RegularExpressions;
using ExHyperV.Models;

namespace ExHyperV.Services;

internal sealed partial class VmQueryEngine
{
    private ReadResult BuildStorage()
    {
        var rasd = Read("Rasd"); var sasd = Read("Sasd");
        if (rasd.State != VmQueryValueState.Available) return rasd;
        if (sasd.State != VmQueryValueState.Available) return sasd;
        var host = Read("HostDisks"); var hv = Read("HvDisks");
        var result = new List<RawRow>();
        foreach (Guid id in _ids)
        {
            var all = rasd.Rows.Concat(sasd.Rows).Where(r => Owned(r, id)).GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToArray();
            var children = all.Where(r => r.Text("Parent").Length > 0).ToLookup(r => RefId(r.Text("Parent")), StringComparer.OrdinalIgnoreCase);
            int scsi = 0, ide = 0;
            foreach (var controller in all.Where(r => Convert.ToInt32(r["ResourceType"]) is 5 or 6).OrderBy(r => Convert.ToInt32(r["ResourceType"])))
            {
                bool isScsi = Convert.ToInt32(controller["ResourceType"]) == 6;
                int number = isScsi ? scsi++ : ide++;
                foreach (var slot in children[controller.Key].Where(r => Convert.ToInt32(r["ResourceType"]) is 16 or 17))
                {
                    var media = children[slot.Key].Where(r => Convert.ToInt32(r["ResourceType"]) is 31 or 16 or 22).ToArray();
                    var attachment = media.Length == 1 ? media[0] : slot;
                    string path = attachment.Strings("HostResource").FirstOrDefault()?.Trim('"') ?? "";
                    bool physical = path.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) || path.Contains("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase);
                    bool physicalOptical = path.Contains("CDROM", StringComparison.OrdinalIgnoreCase) || path.Contains("Msvm_OpticalDrive", StringComparison.OrdinalIgnoreCase);
                    var row = Row(slot.Key, id, ("AttachmentId", slot.Key), ("ControllerType", isScsi ? "SCSI" : "IDE"),
                        ("ControllerNumber", number), ("ControllerLocation", int.TryParse(slot.Text("AddressOnParent"), out var loc) ? loc : null),
                        ("DriveType", Convert.ToInt32(slot["ResourceType"]) == 16 ? "DvdDrive" : "HardDisk"),
                        ("DiskType", path.Length == 0 ? "Empty" : physical || physicalOptical ? "Physical" : "Virtual"), ("Path", path));
                    if (media.Length > 1)
                    {
                        foreach (var field in request.Fields.Where(f => f.Collection == VmQueryCollection.Storage && f.Property is not ("AttachmentId" or "ControllerType" or "ControllerNumber" or "ControllerLocation" or "DriveType")))
                            row.Values[field.Property] = field.Make(VmQueryValueState.Ambiguous, null, sasd.Time, "同一槽位存在多个介质记录。");
                        result.Add(row); continue;
                    }
                    foreach (string prop in new[] { "DiskNumber", "DiskModel", "SerialNumber", "PnpDeviceId", "IsPassthroughStale" })
                        row.Values[prop] = null;
                    if (physical)
                    {
                        string deviceId = RefId(path, "DeviceID");
                        var disks = hv.Rows.Where(r => r.Key.Equals(deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
                        int? diskNumber = disks.Length == 1 && disks[0]["DriveNumber"] != null ? Convert.ToInt32(disks[0]["DriveNumber"]) : null;
                        bool stale = diskNumber == null;
                        if (diskNumber == null)
                        {
                            var match = Regex.Match(deviceId.Length > 0 ? deviceId : path, deviceId.Length > 0 ? @"\\(\d+)$" : @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed)) diskNumber = parsed;
                        }
                        var disk = diskNumber.HasValue ? host.Rows.SingleOrDefault(r => r.Key == diskNumber.Value.ToString()) : null;
                        row.Values["DiskNumber"] = diskNumber;
                        row.Values["IsPassthroughStale"] = hv.State == VmQueryValueState.Available ? stale : Failure(VmStorageFields.IsPassthroughStale, hv);
                        row.Values["DiskModel"] = disk?["Model"];
                        row.Values["SerialNumber"] = disk?["SerialNumber"]?.ToString()?.Trim();
                        row.Values["PnpDeviceId"] = disk?["PNPDeviceID"];
                        row.Values["@PhysicalSize"] = host.State == VmQueryValueState.Available ? disk?["Size"] : Failure(VmStorageFields.CapacityBytes, host);
                        foreach (var field in request.Fields.Where(f => f.Collection == VmQueryCollection.Storage && f.Property is "DiskModel" or "SerialNumber" or "PnpDeviceId"))
                            if (host.State != VmQueryValueState.Available) row.Values[field.Property] = Failure(field, host);
                        if (hv.State != VmQueryValueState.Available) row.Values["DiskNumber"] = Failure(VmStorageFields.DiskNumber, hv);
                    }
                    else foreach (var field in request.Fields.Where(f => f.Collection == VmQueryCollection.Storage && f.Property is "DiskNumber" or "DiskModel" or "SerialNumber" or "PnpDeviceId" or "IsPassthroughStale"))
                        row.Values[field.Property] = field.Make(VmQueryValueState.NotApplicable, null, rasd.Time, "此挂接不是物理硬盘。");
                    result.Add(row);
                }
            }
        }
        return Done(result);
    }

    private async Task<ReadResult> BuildDiskFilesAsync()
    {
        var storage = await DerivedAsync("Storage").ConfigureAwait(false);
        if (storage.State != VmQueryValueState.Available) return storage;
        return await Task.Run(() =>
        {
            using var reader = new VmQueryService.DiskSettingsReader();
            var cached = new Dictionary<string, (long Size, long Max, string Type, PerformanceSampleInfo? SizeSample, PerformanceSampleInfo? CapacitySample)>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<RawRow>();
            bool readCapacity = request.Fields.Contains(VmStorageFields.CapacityBytes) || request.Fields.Contains(VmStorageFields.VirtualDiskType);
            foreach (var item in storage.Rows)
            {
                token.ThrowIfCancellationRequested();
                var values = new Dictionary<string, object?>();
                string path = item.Text("Path");
                if (item["Path"] is VmQueryValue invalid)
                {
                    foreach (var field in request.Fields.Where(f => f.Source == "DiskFiles")) values[field.Property] = field.Make(invalid.State, null, invalid.ObservedAtUtc, invalid.Error);
                }
                else if (item.Text("DiskType") == "Empty")
                {
                    foreach (var field in request.Fields.Where(f => f.Source == "DiskFiles")) values[field.Property] = field.Make(VmQueryValueState.NotApplicable, null, storage.Time, "槽位没有介质。");
                }
                else if (item.Text("DiskType") == "Physical")
                {
                    values["CapacityBytes"] = item["@PhysicalSize"] is VmQueryValue sizeFailure ? sizeFailure
                        : item["@PhysicalSize"] == null ? null : Convert.ToInt64(item["@PhysicalSize"]);
                    values["FileSizeBytes"] = VmStorageFields.FileSizeBytes.Make(VmQueryValueState.NotApplicable, null, storage.Time, "物理磁盘没有磁盘文件占用。");
                    values["VirtualDiskType"] = VmStorageFields.VirtualDiskType.Make(VmQueryValueState.NotApplicable, null, storage.Time, "此项不是虚拟磁盘文件。");
                }
                else
                {
                    if (!cached.TryGetValue(path, out var entry))
                    {
                        if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || !readCapacity)
                        {
                            try { long length = new FileInfo(path).Length; entry = (length, length, "ISO", new(PerformanceSampleState.Valid, DateTimeOffset.UtcNow), new(PerformanceSampleState.Valid, DateTimeOffset.UtcNow)); }
                            catch (Exception ex) { entry = (0, 0, "Unknown", new(PerformanceSampleState.Failed, DateTimeOffset.UtcNow, ex.Message), new(PerformanceSampleState.Failed, DateTimeOffset.UtcNow, ex.Message)); }
                        }
                        else
                        {
                            PerformanceSampleInfo? size = null, capacity = null;
                            var info = VmQueryService.GetDiskSizes(path, reader.Read, (s, c) => { size = s; capacity = c; });
                            entry = (info.Current, info.Max, info.DiskType, size, capacity);
                        }
                        cached.Add(path, entry);
                    }
                    values["FileSizeBytes"] = VmStorageFields.FileSizeBytes.Make(SampleState(entry.SizeSample), entry.Size, entry.SizeSample?.ObservedAtUtc ?? storage.Time, entry.SizeSample?.Error);
                    values["CapacityBytes"] = VmStorageFields.CapacityBytes.Make(SampleState(entry.CapacitySample), entry.Max, entry.CapacitySample?.ObservedAtUtc ?? storage.Time, entry.CapacitySample?.Error);
                    values["VirtualDiskType"] = VmStorageFields.VirtualDiskType.Make(SampleState(entry.CapacitySample), entry.Type, entry.CapacitySample?.ObservedAtUtc ?? storage.Time, entry.CapacitySample?.Error);
                }
                rows.Add(new(item.Key, values));
            }
            return Done(rows);
        }, token).ConfigureAwait(false);
    }
}
