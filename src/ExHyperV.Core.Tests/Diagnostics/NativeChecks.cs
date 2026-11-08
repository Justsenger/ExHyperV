using System.Reflection;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

internal static class NativeChecks
{
    // Opt-in integration checks. Every created VM and file belongs to this invocation.
    public static async Task Run(string outputRoot, string baselinePath, Action<bool, string> check)
    {
        outputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(outputRoot);
        string runId = Guid.NewGuid().ToString("N");
        string ownedRoot = Path.GetFullPath(Path.Combine(outputRoot, "owned-" + runId));
        if (!ownedRoot.StartsWith(outputRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture root");
        Directory.CreateDirectory(ownedRoot);
        File.WriteAllText(Path.Combine(ownedRoot, ".owner"), runId);
        var baseline = Assembly.LoadFrom(Path.GetFullPath(baselinePath));
        var owned = new List<(Guid Id, string Name)>();
        var requestedNames = new List<string>();
        var records = new List<object>();
        bool clean = true;
        try
        {
            var versions = await VmCreateService.GetSupportedVersionsAsync();
            if (!versions.HasData) throw new InvalidOperationException(versions.Error);
            string version = versions.Data.Where(v => Version.TryParse(v, out _)).OrderByDescending(Version.Parse).First();
            string? originalMemory = null;
            foreach (bool useBaseline in new[] { true, false })
            {
                string name = "ExHyperV-test-" + (useBaseline ? "old-" : "new-") + runId[..10];
                var existing = await WmiApi.QueryAsync($"SELECT Name FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(name)}", o => o["Name"]?.ToString());
                if (!existing.Success || existing.Data?.Count > 0) throw new InvalidOperationException("Cannot establish unique fixture identity: " + existing.Error);
                requestedNames.Add(name);
                var source = useBaseline ? baseline : typeof(VmCreateService).Assembly;
                var parameterType = useBaseline
                    ? source.GetType("ExHyperV.Models.VmCreationParams", true)!
                    : typeof(VmCreationParameters);
                object parameters = Activator.CreateInstance(parameterType)!;
                void Set(string key, object value) => parameterType.GetProperty(key)!.SetValue(parameters, value);
                Set("Name", name); Set("Path", ownedRoot); Set("Version", version);
                Set("ProcessorCount", 1); Set(useBaseline ? "MemoryMb" : "MemoryMiB", 256L); Set("EnableDynamicMemory", false);
                Set("EnableSecureBoot", false); Set("EnableTpm", false); Set("DiskMode", 2); Set("StartAfterCreation", false);
                object created = useBaseline ? await Invoke(source, "VmCreateService", "CreateVirtualMachineAsync", parameters)
                    : await VmCreateService.CreateVirtualMachineAsync((VmCreationParameters)parameters);
                var found = await WmiApi.QueryAsync($"SELECT Name FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(name)}", o => o["Name"]?.ToString());
                if (found.HasData)
                    foreach (string? id in found.Data!) if (Guid.TryParse(id, out var guid)) owned.Add((guid, name));
                check(TupleSuccess(created) && owned.Count(v => v.Name == name) == 1, (useBaseline ? "1.5.2" : "Core") + " creates an isolated powered-off VM: " + TupleMessage(created));
                var vmId = owned.Single(v => v.Name == name).Id;
                using (var vm = WmiApi.GetVmComputerSystem(vmId)) check(vm != null && Convert.ToUInt16(vm["EnabledState"]) == 3, "Fixture remains powered off");
                var currentSettings = useBaseline ? null : await VmMemoryService.GetVmMemorySettingsAsync(vmId);
                if (currentSettings is { HasData: false }) throw new InvalidOperationException(currentSettings.Error);
                object settings = useBaseline ? await Invoke(source, "VmMemoryService", "GetVmMemorySettingsAsync", name) : currentSettings!.Data!;
                settings.GetType().GetProperty("Startup")!.SetValue(settings, 512L);
                object changed = useBaseline ? await Invoke(source, "VmMemoryService", "SetVmMemorySettingsAsync", name, settings, false, null)
                    : await VmMemoryService.SetVmMemorySettingsAsync(vmId, (VmMemorySettings)settings, false,
                        [nameof(VmMemorySettings.Startup)]);
                check(TupleSuccess(changed), (useBaseline ? "1.5.2" : "Core") + " applies memory settings to its own VM: " + TupleMessage(changed));
                var memoryResponse = await VmMemoryService.GetVmMemorySettingsAsync(vmId);
                if (!memoryResponse.HasData) throw new InvalidOperationException(memoryResponse.Error);
                var readback = memoryResponse.Data;
                check(readback?.Startup == 512 && readback.Minimum == 512 && readback.Maximum == 512 && !readback.DynamicMemoryEnabled, "Native memory readback preserves static-memory semantics");
                string memoryJson = JsonSerializer.Serialize(readback);
                if (useBaseline) originalMemory = memoryJson;
                else check(originalMemory == memoryJson, "1.5.2 and Core produce identical native memory settings");
                if (!useBaseline)
                {
                    string diskPath = Path.Combine(ownedRoot, name, "owned.vhdx");
                    var disk = await VmStorageService.AddDriveAsync(vmId, new() { PathOrNumber = diskPath, IsNew = true, SizeGiB = 1 });
                    check(disk.Success, "Core creates and attaches a fixture-only dynamic VHDX: " + disk.Error);
                    var storage = await VmStorageService.GetVmStorageItemsAsync(vmId);
                    check(storage.HasData && storage.Data!.Any(d => string.Equals(d.PathOrDiskNumber, diskPath, StringComparison.OrdinalIgnoreCase)), "Detached storage snapshot reports the actual attached disk");
                    var checkpoint = await VmSpacetimeService.CaptureMomentAsync(vmId, SpacetimeMode.Still);
                    check(checkpoint.Success, "Core creates a checkpoint of the fixture: " + checkpoint.Error);
                    var nodes = await VmSpacetimeService.GetSpacetimeNodesAsync(vmId);
                    check(nodes.HasData && nodes.Data.Count(n => n.NodeType == SpacetimeNodeType.Snapshot) == 1, "Checkpoint tree retains genesis, checkpoint and current nodes");
                    string exportRoot = Path.Combine(ownedRoot, "export");
                    Directory.CreateDirectory(exportRoot);
                    var exported = await VmExportService.ExportAsync(vmId, exportRoot, true, [], VmExportCheckpointMode.All, null, false);
                    check(exported.Success && Directory.Exists(exported.OutputPath), "Core exports the fixture including its disk and checkpoint: " + exported.Error);
                    var removed = await VmDeleteService.DeleteVmAsync(name, vmId);
                    check(removed.Success && File.Exists(diskPath), "Unregister preserves the fixture disk and recovery configuration: " + removed.Error);
                }
                records.Add(new { baseline = useBaseline, id = vmId, version, memory = readback });
            }
        }
        finally
        {
            // Creation can register a VM before returning an error. Discover every
            // requested unique fixture name before deciding that its files are safe to remove.
            foreach (string name in requestedNames)
            {
                var discovered = await WmiApi.QueryAsync($"SELECT Name FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(name)}", o => o["Name"]?.ToString());
                if (!discovered.Success) { clean = false; continue; }
                foreach (string? id in discovered.Data ?? [])
                    if (Guid.TryParse(id, out var guid) && !owned.Any(v => v.Id == guid)) owned.Add((guid, name));
            }
            foreach (var vm in owned)
            {
                try
                {
                    using var actual = WmiApi.GetVmComputerSystem(vm.Id);
                    if (actual == null) continue;
                    if (!string.Equals(actual["ElementName"]?.ToString(), vm.Name, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture identity changed; preserving it for inspection.");
                    var result = await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "DestroySystem", p => p["AffectedSystem"] = actual.Path.Path);
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                    using var remaining = WmiApi.GetVmComputerSystem(vm.Id);
                    if (remaining != null) throw new InvalidOperationException("Fixture still registered");
                }
                catch (Exception error) { clean = false; Console.Error.WriteLine("Fixture cleanup pending: " + vm.Id + ": " + error.Message); }
            }
            File.WriteAllText(Path.Combine(outputRoot, "native-report.json"), JsonSerializer.Serialize(new { records, clean, ownedRoot }, new JsonSerializerOptions { WriteIndented = true }));
            if (clean && Path.GetFullPath(ownedRoot).StartsWith(outputRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(Path.Combine(ownedRoot, ".owner")) == runId)
                Directory.Delete(ownedRoot, recursive: true);
            check(clean, "All fixture VMs were removed and only owned test files were cleaned");
        }
    }

    private static async Task<object> Invoke(Assembly source, string type, string method, params object?[] parameters)
    {
        var task = (Task)source.GetType("ExHyperV.Services." + type, true)!.GetMethod(method)!.Invoke(null, parameters)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    private static bool TupleSuccess(object result) => (bool)(result.GetType().GetField("Item1")?.GetValue(result)
        ?? result.GetType().GetProperty("Success")!.GetValue(result))!;
    private static string TupleMessage(object result) => (string?)(result.GetType().GetField("Item2")?.GetValue(result)
        ?? result.GetType().GetProperty("Error")?.GetValue(result)) ?? "";
}
