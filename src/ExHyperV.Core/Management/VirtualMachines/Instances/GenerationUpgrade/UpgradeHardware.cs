using ExHyperV.Models;
using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal static class UpgradeHardware
{
    private const string VirtualizationNamespace = @"\\.\root\virtualization\v2";
    private const string ClusterNamespace = @"\\.\root\MSCluster";
    private static readonly Guid MicrosoftWindowsSecureBootTemplateGuid = new("1734c6e8-3154-4dda-ba5f-a874cc483422");

    private static readonly string[] SystemFields =
    [
        "Notes", "BIOSGUID", "BIOSSerialNumber", "BaseBoardSerialNumber", "ChassisSerialNumber", "ChassisAssetTag",
        "LowMmioGapSize", "HighMmioGapSize", "HighMmioGapBase", "GuestControlledCacheTypes", "LockOnDisconnect",
        "TurnOffOnGuestRestart", "EnableHibernation", "EnhancedSessionTransportType", "UserSnapshotType",
        "VirtualNumaEnabled", "VirtualNumaNodesPerSocket", "AllowReducedFcRedundancy", "SnapshotDataRoot", "SwapFileDataRoot"
    ];

    private static readonly string[] PolicyFields =
    [
        "AutomaticStartupAction", "AutomaticStartupActionDelay", "AutomaticShutdownAction",
        "AutomaticRecoveryAction", "AutomaticSnapshotsEnabled"
    ];

    private static readonly HashSet<string> SupportedSwitchFeatureClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Msvm_EthernetSwitchPortVlanSettingData",
        "Msvm_EthernetSwitchPortBandwidthSettingData",
        "Msvm_EthernetSwitchPortSecuritySettingData",
        "Msvm_EthernetSwitchPortOffloadSettingData"
    };

    internal static Guid SecureBootTemplate(VmGenerationUpgradeOptions options) =>
        options.SecureBootTemplateId ?? MicrosoftWindowsSecureBootTemplateGuid;

    internal static async Task ReconcileOperationsAsync(UpgradeJournal journal)
    {
        if (journal.UnconfirmedWithoutJob || !journal.TracksOperations && (journal.InFlight || journal.Step == UpgradeStep.Cleanup))
            throw new UpgradeUncertainException("The interrupted operation has no verifiable native receipt.");
        foreach (var (id, operation) in journal.PendingOperations.ToArray())
        {
            if (string.IsNullOrWhiteSpace(operation.Job))
                throw new UpgradeUncertainException("No job receipt for " + operation.Method);
            var receipt = new WmiOperationReceipt(operation.Method, operation.Target, WmiScope.HyperV, WmiContext.Local)
                { Job = operation.Job };
            await receipt.ObserveAsync();
            if (!receipt.IsSettled) throw new UpgradeUncertainException("Pending native job: " + operation.Job);
            journal.PendingOperations.Remove(id);
            journal.Save();
        }
    }

    internal static Task ConvertVhdAsync(string sourcePath, string workPath, string vhdType, CancellationToken token) => Task.Run(async () =>
    {
        var scope = ConnectScope();
        using var vhdClass = new ManagementClass(scope, new ManagementPath("Msvm_VirtualHardDiskSettingData"), null);
        using var vhdObj = vhdClass.CreateInstance();
        ushort typeCode = vhdType.Equals("Fixed", StringComparison.OrdinalIgnoreCase) ? (ushort)2 : (ushort)3;
        vhdObj["Type"] = typeCode;
        vhdObj["Format"] = (ushort)3; // VHDX
        vhdObj["Path"] = workPath;
        vhdObj["BlockSize"] = 0u;
        vhdObj["LogicalSectorSize"] = 0u;
        vhdObj["ParentPath"] = null;
        string vhdXml = vhdObj.GetText(TextFormat.CimDtd20);

        using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_ImageManagementService"));
        using var services = search.Get();
        using var service = SingleObject(services.Cast<ManagementObject>().ToArray(), "image service");
        var result = await WmiApi.InvokeOnObjectAsync(service, "ConvertVirtualHardDisk", input =>
        {
            input["SourcePath"] = sourcePath;
            input["VirtualDiskSettingData"] = vhdXml;
        }, cancellationToken: token, timeout: Timeout.InfiniteTimeSpan);
        WindowsUpgradeHost.Require(result);
    });

    internal static Task CheckCreationMarkerAsync(Guid id, string root, string marker, string temporaryName) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        using var current = GetRealizedSettings(scope, id);
        string? subType = current["VirtualSystemSubType"] as string;
        string? vmName = vm["ElementName"] as string;
        string? dataRoot = current["ConfigurationDataRoot"] as string;
        string[] notes = current["Notes"] as string[] ?? [];
        if (subType != "Microsoft:Hyper-V:SubType:2" ||
            !string.Equals(vmName, temporaryName, StringComparison.Ordinal) ||
            !WindowsUpgradeHost.Paths.Equals(dataRoot, root) ||
            notes.Length != 1 ||
            !string.Equals(notes[0], "ExHyperV.GenerationUpgrade/" + marker, StringComparison.Ordinal))
        {
            throw new IOException("Unconfirmed candidate identity");
        }
    });

    internal static Task ReceiptAsync(Guid id, string name, string root, string[] diskPaths) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        using var current = GetRealizedSettings(scope, id);
        string? subType = current["VirtualSystemSubType"] as string;
        string? vmName = vm["ElementName"] as string;
        string? dataRoot = current["ConfigurationDataRoot"] as string;
        var drives = GetAttachedDrives(current);
        var actual = drives.Where(d => !d.Dvd).Select(d => d.Path).Order(WindowsUpgradeHost.Paths);
        if (subType != "Microsoft:Hyper-V:SubType:2" ||
            !string.Equals(vmName, name, StringComparison.Ordinal) ||
            !WindowsUpgradeHost.Paths.Equals(dataRoot, root) ||
            !actual.SequenceEqual(diskPaths.Order(WindowsUpgradeHost.Paths), WindowsUpgradeHost.Paths))
        {
            throw new IOException("Committed VM or disk inventory changed");
        }
    });

    internal static Task<UpgradeSource> CaptureAsync(Guid id) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        RequireOff(vm);
        using var settings = GetRealizedSettings(scope, id);

        string? subType = settings["VirtualSystemSubType"] as string;
        ushort replicationMode = Convert.ToUInt16(vm["ReplicationMode"] ?? (ushort)0, CultureInfo.InvariantCulture);
        ushort replicationState = Convert.ToUInt16(vm["ReplicationState"] ?? (ushort)0, CultureInfo.InvariantCulture);
        if (subType != "Microsoft:Hyper-V:SubType:1" ||
            replicationMode != 0 ||
            replicationState != 0 ||
            IsClusteredVm(id) ||
            HasSnapshots(scope, id) ||
            GetRelatedAndCount(settings, "Msvm_PciExpressSettingData") != 0 ||
            GetRelatedAndCount(settings, "Msvm_GpuPartitionSettingData") != 0)
        {
            throw new IOException("Unsupported source configuration");
        }

        string vmName = vm["ElementName"] as string ?? throw new IOException("Missing VM name");
        if (CountVmsByName(scope, vmName) != 1)
            throw new IOException("Ambiguous original name");

        if (HasFloppyMedia(settings) || GetRelatedAndCount(settings, "Msvm_SyntheticFcPortSettingData") != 0)
            throw new IOException("Floppy media or virtual Fibre Channel cannot be mapped to generation 2");

        var drives = GetAttachedDrives(settings);

        using var cpu = SingleObject(GetRelated(settings, "Msvm_ProcessorSettingData"), "processor");
        using var ram = SingleObject(GetRelated(settings, "Msvm_MemorySettingData"), "memory");

        var legacy = GetRelated(settings, "Msvm_EmulatedEthernetPortSettingData");
        string[] legacyMacs;
        try { legacyMacs = legacy.Select(nic => nic["Address"] as string).OfType<string>().Order(StringComparer.Ordinal).ToArray(); }
        finally { foreach (var nic in legacy) nic.Dispose(); }

        var hardwareDict = new Dictionary<string, object?>
        {
            ["LegacyMacs"] = legacyMacs,
            ["BootOrder"] = settings["BootOrder"] as ushort[] ?? [],
            ["System"] = ReadFieldMap(settings, SystemFields),
            ["Policy"] = ReadFieldMap(settings, PolicyFields),
            ["Cpu"] = ReadResourceFieldMap(cpu),
            ["Memory"] = ReadResourceFieldMap(ram),
            ["Network"] = ReadAdapterManifest(scope, settings)
        };
        JsonElement hardwareElement = JsonSerializer.SerializeToElement(hardwareDict, CoreJson.Default.FieldMap);

        string dataRoot = settings["ConfigurationDataRoot"] as string ?? throw new IOException("Missing ConfigurationDataRoot");
        string version = settings["Version"] as string ?? "9.0";

        return new UpgradeSource(vmName, dataRoot, version, drives, hardwareElement, [], new SortedDictionary<string, string>(StringComparer.Ordinal));
    });

    internal static Task ConfigureAsync(Guid id, JsonElement hardware) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        RequireOff(vm);
        using var settings = GetRealizedSettings(scope, id);
        using var cpu = SingleObject(GetRelated(settings, "Msvm_ProcessorSettingData"), "processor");
        using var ram = SingleObject(GetRelated(settings, "Msvm_MemorySettingData"), "memory");

        settings["Notes"] = Array.Empty<string>();
        ModifySystemSettings(scope, settings);
        settings.Get();

        ApplyFields(scope, settings, hardware.GetProperty("System"), isSystem: true);
        ApplyFields(scope, cpu, hardware.GetProperty("Cpu"), isSystem: false);
        ApplyFields(scope, ram, hardware.GetProperty("Memory"), isSystem: false);

        EnsureTpmDisabled(scope, settings);
    });

    internal static Task DetachMediaAsync(Guid id, string workPath) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        RequireOff(vm);
        using var settings = GetRealizedSettings(scope, id);
        var drive = GetStorageAttachmentObjects(settings).Single(d => !d.IsDvd && WindowsUpgradeHost.Paths.Equals(d.Path, workPath));
        InvokeServiceMethod("Msvm_VirtualSystemManagementService", "RemoveResourceSettings", args =>
            args["ResourceSettings"] = new[] { drive.StoragePath ?? throw new IOException("Missing staged disk attachment.") });
    });

    internal static Task AttachMediaAsync(Guid id, UpgradeDrive slot) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        RequireOff(vm);
        using var settings = GetRealizedSettings(scope, id);
        var drive = GetStorageAttachmentObjects(settings).Single(d => d.ControllerType == "SCSI" &&
            d.ControllerNumber == slot.Number && d.ControllerLocation == slot.Location && !d.IsDvd && d.StoragePath == null);
        AttachMedia(scope, settings, drive.DrivePath, slot);
    });

    internal static Task FinalizeAsync(Guid id, string name, JsonElement hardware, UpgradeDrive[] slots) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        RequireOff(vm);

        int maxController = slots.Length == 0 ? 0 : slots.Max(s => s.Number) + 1;
        EnsureScsiControllerCount(scope, id, maxController);

        foreach (var slot in slots) AttachStorageDrive(scope, id, slot);
        ApplyNetworkManifest(scope, id, hardware.GetProperty("Network"));

        using var settings = GetRealizedSettings(scope, id);
        ApplyBootOrder(scope, settings, hardware, slots, verifyOnly: false);
        settings.Get();
        settings["ElementName"] = name;
        ModifySystemSettings(scope, settings);
        settings.Get();
        ApplyFields(scope, settings, hardware.GetProperty("Policy"), isSystem: true);
    });

    internal static Task VerifyAsync(Guid id, UpgradeSource source, UpgradeDrive[] slots, VmGenerationUpgradeOptions options) => Task.Run(() =>
    {
        var hardware = source.Hardware;
        var scope = ConnectScope();
        using var vm = GetVm(scope, id);
        RequireOff(vm);
        using var settings = GetRealizedSettings(scope, id);
        using var cpu = SingleObject(GetRelated(settings, "Msvm_ProcessorSettingData"), "processor");
        using var ram = SingleObject(GetRelated(settings, "Msvm_MemorySettingData"), "memory");

        string? subType = settings["VirtualSystemSubType"] as string;
        string? vmName = vm["ElementName"] as string;
        string? dataRoot = settings["ConfigurationDataRoot"] as string;
        bool? secureBoot = settings["SecureBootEnabled"] as bool?;
        string? templateId = settings["SecureBootTemplateId"] as string;

        if (subType != "Microsoft:Hyper-V:SubType:2" ||
            !string.Equals(vmName, source.Name, StringComparison.Ordinal) ||
            !WindowsUpgradeHost.Paths.Equals(dataRoot, source.Root) ||
            !string.Equals(settings["Version"] as string, source.Version, StringComparison.Ordinal) ||
            secureBoot != options.EnableSecureBoot ||
            (options.EnableSecureBoot && (!Guid.TryParse(templateId, out var actualTemplate) || actualTemplate != SecureBootTemplate(options))) ||
            IsTpmEnabled(settings) ||
            HasSnapshots(scope, id) ||
            CountVmsByName(scope, source.Name) != 1)
        {
            throw new IOException("Final configuration mismatch");
        }

        if (!CanonicalJsonEquals(ReadResourceFieldMap(cpu), hardware.GetProperty("Cpu")) ||
            !CanonicalJsonEquals(ReadResourceFieldMap(ram), hardware.GetProperty("Memory")) ||
            !CanonicalJsonEquals(ReadFieldMap(settings, PolicyFields), hardware.GetProperty("Policy")))
        {
            throw new IOException("CPU, memory or power policy changed");
        }

        foreach (JsonProperty prop in hardware.GetProperty("System").EnumerateObject())
        {
            if (!CanonicalJsonEquals(NormalizeWmiValue(settings[prop.Name]), prop.Value))
                throw new IOException("System setting changed: " + prop.Name);
        }

        VerifyNetworkManifest(scope, settings, hardware.GetProperty("Network"));

        ApplyBootOrder(scope, settings, hardware, slots, verifyOnly: true);

        var attached = GetAttachedDrives(settings);
        if (attached.Length != slots.Length)
            throw new IOException("Final storage count mismatch");

        foreach (var slot in slots)
        {
            var matches = attached.Where(a => a.Number == slot.Number && a.Location == slot.Location).ToArray();
            if (matches.Length != 1)
                throw new IOException("Expected one final slot");
            var actual = matches[0];
            if (!slot.Dvd &&
                (actual.MinimumIOPS != slot.MinimumIOPS ||
                 actual.MaximumIOPS != slot.MaximumIOPS ||
                 actual.QoSPolicyID != slot.QoSPolicyID ||
                 !string.Equals(actual.Pool ?? string.Empty, slot.Pool ?? string.Empty, StringComparison.Ordinal)))
            {
                throw new IOException("Storage policy mismatch");
            }
            if (actual.Controller != "SCSI" || actual.Dvd != slot.Dvd || actual.Shared ||
                !string.Equals(actual.Path ?? string.Empty, slot.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Final storage path mismatch");
            }
        }
    });

    private static ManagementScope ConnectScope()
    {
        var scope = new ManagementScope(VirtualizationNamespace);
        scope.Connect();
        return scope;
    }

    private static ManagementObject GetVm(ManagementScope scope, Guid id)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{id:D}'"));
        using var results = searcher.Get();
        return SingleObject(results.Cast<ManagementObject>().ToArray(), "VM");
    }

    private static ManagementObject GetRealizedSettings(ManagementScope scope, Guid id)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier='{id:D}' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'"));
        using var results = searcher.Get();
        return SingleObject(results.Cast<ManagementObject>().ToArray(), "realized configuration");
    }

    private static ManagementObject[] GetRelated(ManagementObject source, string relatedClass)
    {
        using var collection = source.GetRelated(relatedClass, "Msvm_VirtualSystemSettingDataComponent", null, null, null, null, false, null);
        return collection?.Cast<ManagementObject>().ToArray() ?? [];
    }

    private static int GetRelatedAndCount(ManagementObject source, string relatedClass)
    {
        var items = GetRelated(source, relatedClass);
        int count = items.Length;
        foreach (var item in items) item.Dispose();
        return count;
    }

    private static ManagementObject SingleObject(ManagementObject[] items, string description)
    {
        if (items.Length != 1)
        {
            foreach (var item in items) item.Dispose();
            throw new IOException($"Expected one {description}, found {items.Length}");
        }
        return items[0];
    }

    private static void RequireOff(ManagementObject vm)
    {
        int state = Convert.ToInt32(vm["EnabledState"] ?? 0, CultureInfo.InvariantCulture);
        if (state != 3) throw new IOException("VM must be Off");
    }

    private static void InvokeServiceMethod(string serviceClass, string method, Action<ManagementBaseObject> configure) =>
        WindowsUpgradeHost.Require(WmiApi.InvokeAsync($"SELECT * FROM {serviceClass}", method, configure).GetAwaiter().GetResult());

    private static void ModifySystemSettings(ManagementScope scope, ManagementObject settings)
    {
        string xml = settings.GetText(TextFormat.CimDtd20);
        InvokeServiceMethod("Msvm_VirtualSystemManagementService", "ModifySystemSettings", args =>
        {
            args["SystemSettings"] = xml;
        });
    }

    private static void ModifyResourceSettings(ManagementScope scope, ManagementObject resource)
    {
        string xml = resource.GetText(TextFormat.CimDtd20);
        InvokeServiceMethod("Msvm_VirtualSystemManagementService", "ModifyResourceSettings", args =>
        {
            args["ResourceSettings"] = new[] { xml };
        });
    }

    private static bool IsClusteredVm(Guid id)
    {
        try
        {
            var clusterScope = new ManagementScope(ClusterNamespace);
            clusterScope.Connect();
            using var searcher = new ManagementObjectSearcher(
                clusterScope,
                new ObjectQuery("SELECT PrivateProperties FROM MSCluster_Resource WHERE Type='Virtual Machine'"));
            using var results = searcher.Get();
            foreach (ManagementObject res in results)
            {
                using (res)
                {
                    if (res["PrivateProperties"] is ManagementBaseObject props &&
                        Guid.TryParse(props["VmID"] as string, out Guid vmGuid) &&
                        vmGuid == id)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        {
            return false;
        }
    }

    private static bool HasSnapshots(ManagementScope scope, Guid id)
    {
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier='{id:D}' AND VirtualSystemType LIKE 'Microsoft:Hyper-V:Snapshot:%'"));
        using var results = searcher.Get();
        foreach (ManagementObject snap in results)
        {
            using (snap) return true;
        }
        return false;
    }

    private static int CountVmsByName(ManagementScope scope, string name)
    {
        string escaped = name.Replace("\\", "\\\\").Replace("'", "\\'");
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName='{escaped}'"));
        using var results = searcher.Get();
        int count = 0;
        foreach (ManagementObject row in results)
        {
            using (row) count++;
        }
        return count;
    }

    private static bool HasFloppyMedia(ManagementObject settings)
    {
        var storageItems = GetRelated(settings, "Msvm_StorageAllocationSettingData");
        try
        {
            foreach (var item in storageItems)
            {
                string? subType = item["ResourceSubType"] as string;
                if ((subType?.Contains("Floppy", StringComparison.OrdinalIgnoreCase) == true ||
                     subType?.Contains("Diskette", StringComparison.OrdinalIgnoreCase) == true) &&
                    item["HostResource"] is string[] { Length: > 0 } paths &&
                    !string.IsNullOrWhiteSpace(paths[0]))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var item in storageItems) item.Dispose();
        }
    }

    private sealed record RawStorageAttachment(
        string ControllerType,
        int ControllerNumber,
        int ControllerLocation,
        bool IsDvd,
        string? Path,
        bool Shared,
        ulong MinimumIops,
        ulong MaximumIops,
        Guid? QosPolicyId,
        string? PoolId,
        string DrivePath,
        string? StoragePath);

    private static UpgradeDrive[] GetAttachedDrives(ManagementObject settings)
    {
        var attachments = GetStorageAttachmentObjects(settings);
        return attachments
            .OrderBy(a => a.IsDvd ? 1 : 0)
            .ThenBy(a => a.ControllerType, StringComparer.Ordinal)
            .ThenBy(a => a.ControllerNumber)
            .ThenBy(a => a.ControllerLocation)
            .Select(a => new UpgradeDrive(
                a.ControllerType,
                a.ControllerNumber,
                a.ControllerLocation,
                a.IsDvd,
                a.Path,
                a.Shared,
                MinimumIOPS: a.MinimumIops,
                MaximumIOPS: a.MaximumIops,
                QoSPolicyID: a.QosPolicyId,
                Pool: a.PoolId))
            .ToArray();
    }

    private static int ParseControllerOrderKey(ManagementObject controller)
    {
        if (int.TryParse(controller["Address"] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int addr))
            return addr;
        string inst = controller["InstanceID"] as string ?? string.Empty;
        string[] segs = inst.Split('\\');
        if (segs.Length > 0 && int.TryParse(segs[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int suffix))
            return suffix;
        return int.MaxValue;
    }

    private static RawStorageAttachment[] GetStorageAttachmentObjects(ManagementObject settings)
    {
        var rasds = GetRelated(settings, "Msvm_ResourceAllocationSettingData");
        var sasds = GetRelated(settings, "Msvm_StorageAllocationSettingData");
        try
        {
            var controllersByPath = new Dictionary<string, (string Type, int Number)>(StringComparer.OrdinalIgnoreCase);
            var controllersByType = new Dictionary<string, List<ManagementObject>>(StringComparer.OrdinalIgnoreCase)
            {
                ["IDE"] = [],
                ["SCSI"] = []
            };

            foreach (var r in rasds)
            {
                int resType = Convert.ToInt32(r["ResourceType"] ?? 0, CultureInfo.InvariantCulture);
                if (resType == 5)
                    controllersByType["IDE"].Add(r);
                else if (resType == 6)
                    controllersByType["SCSI"].Add(r);
            }

            foreach (var (type, list) in controllersByType)
            {
                var ordered = list
                    .OrderBy(ParseControllerOrderKey)
                    .ThenBy(c => c["InstanceID"] as string ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (int i = 0; i < ordered.Length; i++)
                {
                    string? path = ordered[i].Path?.Path;
                    if (!string.IsNullOrEmpty(path))
                        controllersByPath[path] = (type, i);
                }
            }

            var result = new List<RawStorageAttachment>();
            foreach (var drive in rasds)
            {
                int resType = Convert.ToInt32(drive["ResourceType"] ?? 0, CultureInfo.InvariantCulture);
                string? subType = drive["ResourceSubType"] as string;
                if (drive.ClassPath.ClassName == "Msvm_StorageAllocationSettingData") continue;
                bool isDisk = resType is 17 or 22;
                bool isDvd = resType == 16;
                if (!isDisk && !isDvd) continue;

                string parentPath = drive["Parent"] as string ?? string.Empty;
                if (!controllersByPath.TryGetValue(parentPath, out var controller))
                    throw new IOException("Unmapped storage controller for drive: " + subType);

                if (!int.TryParse(drive["AddressOnParent"] as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int location))
                    throw new InvalidDataException("Storage attachment has no slot: " + subType);
                string drivePath = drive.Path?.Path ?? string.Empty;
                if (drive["HostResource"] is string[] direct && direct.Any(value => !string.IsNullOrWhiteSpace(value)))
                    throw new InvalidDataException("Direct device assignment cannot be migrated: " + drivePath);
                bool shared = false;
                bool isPhysicalHardDisk = string.Equals(subType, "Microsoft:Hyper-V:Physical Disk Drive", StringComparison.OrdinalIgnoreCase);

                ManagementObject? media = sasds.SingleOrDefault(s =>
                    string.Equals(s["Parent"] as string, drivePath, StringComparison.OrdinalIgnoreCase));

                string? filePath = null;
                ulong minIops = 0;
                ulong maxIops = 0;
                Guid? qosId = null;
                string? poolId = null;
                string? storagePath = null;

                // Keep Path null for physical passthrough disks for storage preflight to reject
                if (media != null && !isPhysicalHardDisk)
                {
                    storagePath = media.Path?.Path;
                    if (media["HostResource"] is string[] { Length: > 0 } hostRes && !string.IsNullOrEmpty(hostRes[0]))
                        filePath = hostRes[0];
                    if (!isDvd)
                    {
                        shared = media["PersistentReservationsSupported"] is true;
                        minIops = Convert.ToUInt64(media["IOPSReservation"] ?? 0UL, CultureInfo.InvariantCulture);
                        maxIops = Convert.ToUInt64(media["IOPSLimit"] ?? 0UL, CultureInfo.InvariantCulture);
                        if (Guid.TryParse(media["StorageQoSPolicyID"] as string, out Guid parsedQos) && parsedQos != Guid.Empty)
                            qosId = parsedQos;
                        string? rawPool = media["PoolID"] as string;
                        if (!string.IsNullOrEmpty(rawPool))
                            poolId = rawPool;
                    }
                }

                result.Add(new RawStorageAttachment(
                    controller.Type,
                    controller.Number,
                    location,
                    isDvd,
                    filePath,
                    shared,
                    minIops,
                    maxIops,
                    qosId,
                    poolId,
                    drivePath,
                    storagePath));
            }

            return result.ToArray();
        }
        finally
        {
            foreach (var r in rasds) r.Dispose();
            foreach (var s in sasds) s.Dispose();
        }
    }

    private static ManagementObject[] GetNetworkPorts(ManagementObject settings)
    {
        var emulated = GetRelated(settings, "Msvm_EmulatedEthernetPortSettingData");
        var synthetic = GetRelated(settings, "Msvm_SyntheticEthernetPortSettingData");
        return emulated.Concat(synthetic)
            .OrderBy(p => p["InstanceID"] as string ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, object?> ReadFieldMap(ManagementObject row, IEnumerable<string> names)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var existingNames = row.Properties.Cast<PropertyData>().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (!existingNames.Contains(name)) continue;
            object? value = row[name];
            if (value != null)
                result[name] = NormalizeWmiValue(value);
        }
        return result;
    }

    private static Dictionary<string, object?> ReadResourceFieldMap(ManagementObject row)
    {
        string className = row["__CLASS"] as string ?? string.Empty;
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "InstanceID", "ElementName", "Caption", "Description"
        };
        var names = new List<string> { "VirtualQuantity", "Reservation", "Limit", "Weight" };
        names.AddRange(row.Properties.Cast<PropertyData>()
            .Where(p => string.Equals(p.Origin, className, StringComparison.OrdinalIgnoreCase) && !excluded.Contains(p.Name))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal));
        return ReadFieldMap(row, names.Distinct(StringComparer.Ordinal));
    }

    private static List<Dictionary<string, object?>> ReadAdapterManifest(ManagementScope scope, ManagementObject settings)
    {
        var allocations = GetRelated(settings, "Msvm_EthernetPortAllocationSettingData");
        var ports = GetNetworkPorts(settings);
        try
        {
            var manifest = new List<Dictionary<string, object?>>();
            foreach (var port in ports)
            {
                string portPath = port.Path?.Path ?? string.Empty;
                string portId = port["InstanceID"] as string ?? string.Empty;
                var matchingAllocs = allocations.Where(a =>
                    string.Equals(a["Parent"] as string, portPath, StringComparison.OrdinalIgnoreCase) ||
                    (a["InstanceID"] as string ?? string.Empty).StartsWith(portId + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();

                if (matchingAllocs.Length > 1)
                    throw new IOException("Ambiguous switch allocation");

                string? switchName = null;
                var features = new List<Dictionary<string, object?>>();
                bool enabled = false;

                if (matchingAllocs.Length == 1)
                {
                    var alloc = matchingAllocs[0];
                    enabled = Convert.ToUInt16(alloc["EnabledState"] ?? (ushort)0, CultureInfo.InvariantCulture) == 2;
                    if (alloc["HostResource"] is string[] { Length: > 0 } hostRes && !string.IsNullOrWhiteSpace(hostRes[0]))
                    {
                        if (hostRes.Length != 1) throw new IOException("Expected one switch");
                        using var sw = new ManagementObject(scope, new ManagementPath(hostRes[0]), null);
                        sw.Get();
                        switchName = sw["Name"] as string;
                    }

                    using var relatedFeatures = alloc.GetRelated("Msvm_EthernetSwitchPortFeatureSettingData");
                    var orderedFeatures = relatedFeatures.Cast<ManagementObject>()
                        .OrderBy(f => f["__CLASS"] as string ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(f => f["InstanceID"] as string ?? string.Empty, StringComparer.Ordinal)
                        .ToArray();
                    try
                    {
                        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "InstanceID", "ElementName", "Caption", "Description", "FeatureId", "Parent", "SystemName", "HostResource"
                        };
                        foreach (var f in orderedFeatures)
                        {
                            string fClass = f["__CLASS"] as string ?? string.Empty;
                            if (!SupportedSwitchFeatureClasses.Contains(fClass))
                                throw new IOException("Unsupported switch feature: " + fClass);
                            var fNames = f.Properties.Cast<PropertyData>()
                                .Where(p => string.Equals(p.Origin, fClass, StringComparison.OrdinalIgnoreCase) && !excluded.Contains(p.Name))
                                .Select(p => p.Name)
                                .OrderBy(n => n, StringComparer.Ordinal);
                            features.Add(new Dictionary<string, object?>
                            {
                                ["Class"] = fClass,
                                ["Fields"] = ReadFieldMap(f, fNames)
                            });
                        }
                    }
                    finally
                    {
                        foreach (var f in orderedFeatures) f.Dispose();
                    }
                }

                manifest.Add(new Dictionary<string, object?>
                {
                    ["Name"] = port["ElementName"] as string ?? "Network Adapter",
                    ["Address"] = port["Address"] as string,
                    ["Static"] = port["StaticMacAddress"] is true,
                    ["HasAllocation"] = matchingAllocs.Length == 1,
                    ["Switch"] = switchName,
                    ["Enabled"] = enabled,
                    ["Features"] = features
                });
            }

            return manifest;
        }
        finally
        {
            foreach (var a in allocations) a.Dispose();
            foreach (var p in ports) p.Dispose();
        }
    }

    private static void ApplyNetworkManifest(ManagementScope scope, Guid id, JsonElement manifest)
    {
        using (var initialSettings = GetRealizedSettings(scope, id))
        {
            var existingPorts = GetNetworkPorts(initialSettings);
            try
            {
                if (existingPorts.Length != 0)
                    throw new IOException("Candidate already has network devices");
            }
            finally
            {
                foreach (var p in existingPorts) p.Dispose();
            }
        }

        foreach (JsonElement nic in manifest.EnumerateArray())
        {
            string nicName = nic.GetProperty("Name").GetString() ?? "Network Adapter";
            string? address = nic.TryGetProperty("Address", out var addrEl) && addrEl.ValueKind == JsonValueKind.String ? addrEl.GetString() : null;
            bool isStatic = nic.TryGetProperty("Static", out var statEl) && statEl.ValueKind == JsonValueKind.True;
            string? switchId = nic.TryGetProperty("Switch", out var swEl) && swEl.ValueKind == JsonValueKind.String ? swEl.GetString() : null;
            bool enabled = nic.TryGetProperty("Enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;

            string portPath = AddSyntheticNetworkAdapter(scope, id, nicName, address, isStatic);
            if (!nic.GetProperty("HasAllocation").GetBoolean()) continue;
            using var port = new ManagementObject(scope, new ManagementPath(portPath), null);
            port.Get();
            string portInstanceId = port["InstanceID"] as string ?? throw new IOException("Missing NIC InstanceID");

            string allocPath = EnsureEthernetAllocation(scope, id, portPath, portInstanceId, switchId, enabled);
            using var alloc = new ManagementObject(scope, new ManagementPath(allocPath), null);
            alloc.Get();

            if (nic.TryGetProperty("Features", out var featuresEl) && featuresEl.ValueKind == JsonValueKind.Array)
            {
                var desiredClasses = featuresEl.EnumerateArray()
                    .Select(f => f.GetProperty("Class").GetString() ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                using (var currentFeatures = alloc.GetRelated("Msvm_EthernetSwitchPortFeatureSettingData"))
                {
                    foreach (ManagementObject def in currentFeatures.Cast<ManagementObject>())
                    {
                        using (def)
                        {
                            string defClass = def["__CLASS"] as string ?? string.Empty;
                            if (!desiredClasses.Contains(defClass) && def.Path?.Path is { } defPath)
                            {
                                InvokeServiceMethod("Msvm_VirtualSystemManagementService", "RemoveFeatureSettings", args =>
                                {
                                    args["FeatureSettings"] = new[] { defPath };
                                });
                            }
                        }
                    }
                }

                foreach (JsonElement feature in featuresEl.EnumerateArray())
                {
                    string featureClass = feature.GetProperty("Class").GetString() ?? throw new IOException("Missing feature class");
                    JsonElement fields = feature.GetProperty("Fields");
                    using var existingCollection = alloc.GetRelated(featureClass);
                    var existingList = existingCollection.Cast<ManagementObject>().ToArray();
                    try
                    {
                        if (existingList.Length == 0)
                        {
                            using var template = GetDefaultSettingTemplate(scope, featureClass);
                            foreach (JsonProperty prop in fields.EnumerateObject())
                                SetWmiField(template, prop.Name, prop.Value);
                            string xml = template.GetText(TextFormat.CimDtd20);
                            InvokeServiceMethod("Msvm_VirtualSystemManagementService", "AddFeatureSettings", args =>
                            {
                                args["AffectedConfiguration"] = allocPath;
                                args["FeatureSettings"] = new[] { xml };
                            });
                        }
                        else
                        {
                            var target = SingleObject(existingList, "switch feature");
                            foreach (JsonProperty prop in fields.EnumerateObject())
                                SetWmiField(target, prop.Name, prop.Value);
                            string xml = target.GetText(TextFormat.CimDtd20);
                            InvokeServiceMethod("Msvm_VirtualSystemManagementService", "ModifyFeatureSettings", args =>
                            {
                                args["FeatureSettings"] = new[] { xml };
                            });
                        }
                    }
                    finally
                    {
                        foreach (var e in existingList) e.Dispose();
                    }
                }
            }
        }

        using var verifySettings = GetRealizedSettings(scope, id);
        VerifyNetworkManifest(scope, verifySettings, manifest);
    }

    private static string AddSyntheticNetworkAdapter(ManagementScope scope, Guid id, string name, string? address, bool isStatic)
    {
        using var settings = GetRealizedSettings(scope, id);
        using var template = GetDefaultSettingTemplate(scope, "Msvm_SyntheticEthernetPortSettingData");
        template["ElementName"] = name;
        template["StaticMacAddress"] = isStatic;
        if (!string.IsNullOrWhiteSpace(address)) template["Address"] = address;
        template["VirtualSystemIdentifiers"] = new[] { Guid.NewGuid().ToString("B") };

        return AddSingleResourceSetting(settings.Path.Path, template);
    }

    private static string EnsureEthernetAllocation(
        ManagementScope scope,
        Guid id,
        string portPath,
        string portInstanceId,
        string? switchName,
        bool enabled)
    {
        using var settings = GetRealizedSettings(scope, id);
        var allocations = GetRelated(settings, "Msvm_EthernetPortAllocationSettingData");
        try
        {
            var existing = allocations.FirstOrDefault(a =>
                string.Equals(a["Parent"] as string, portPath, StringComparison.OrdinalIgnoreCase) ||
                (a["InstanceID"] as string ?? string.Empty).StartsWith(portInstanceId + "\\", StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                using var template = GetDefaultSettingTemplate(scope, "Msvm_EthernetPortAllocationSettingData");
                template["Parent"] = portPath;
                template["EnabledState"] = enabled ? (ushort)2 : (ushort)3;
                if (!string.IsNullOrWhiteSpace(switchName))
                    template["HostResource"] = new[] { FindSwitchPath(scope, switchName) };
                return AddSingleResourceSetting(settings.Path.Path, template);
            }
            else
            {
                existing["EnabledState"] = enabled ? (ushort)2 : (ushort)3;
                if (!string.IsNullOrWhiteSpace(switchName))
                    existing["HostResource"] = new[] { FindSwitchPath(scope, switchName) };
                ModifyResourceSettings(scope, existing);
                return existing.Path.Path;
            }
        }
        finally
        {
            foreach (var a in allocations) a.Dispose();
        }
    }

    private static string FindSwitchPath(ManagementScope scope, string switchName)
    {
        string escaped = switchName.Replace("\\", "\\\\").Replace("'", "\\'");
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery($"SELECT * FROM Msvm_VirtualEthernetSwitch WHERE Name='{escaped}'"));
        using var results = searcher.Get();
        using var sw = SingleObject(results.Cast<ManagementObject>().ToArray(), "virtual switch");
        return sw.Path.Path;
    }

    private static void VerifyNetworkManifest(ManagementScope scope, ManagementObject settings, JsonElement expectedManifest)
    {
        var actual = ReadAdapterManifest(scope, settings);
        string[] expectedRows = expectedManifest.EnumerateArray()
            .Select(e => CanonicalizeJson(JsonNode.Parse(e.GetRawText())))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        string[] actualRows = actual
            .Select(a => CanonicalizeJson(JsonSerializer.SerializeToNode(a, CoreJson.Default.FieldMap)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        if (!expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal))
            throw new IOException("Network settings failed readback");
    }

    private static string CanonicalizeJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => JsonSerializer.Serialize(kv.Key, CoreJson.Default.String) + ":" + CanonicalizeJson(kv.Value))) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(CanonicalizeJson)) + "]",
        _ => node.ToJsonString()
    };

    private static void EnsureScsiControllerCount(ManagementScope scope, Guid id, int requiredCount)
    {
        while (true)
        {
            using var settings = GetRealizedSettings(scope, id);
            var rasds = GetRelated(settings, "Msvm_ResourceAllocationSettingData");
            int currentCount;
            try
            {
                currentCount = rasds.Count(r => Convert.ToInt32(r["ResourceType"] ?? 0, CultureInfo.InvariantCulture) == 6);
            }
            finally
            {
                foreach (var r in rasds) r.Dispose();
            }

            if (currentCount >= requiredCount) return;
            using var template = GetDefaultSettingTemplate(scope, "Msvm_ResourceAllocationSettingData", "Microsoft:Hyper-V:Synthetic SCSI Controller");
            _ = AddSingleResourceSetting(settings.Path.Path, template);
        }
    }

    private static void AttachStorageDrive(ManagementScope scope, Guid id, UpgradeDrive drive)
    {
        using var settings = GetRealizedSettings(scope, id);
        var rasds = GetRelated(settings, "Msvm_ResourceAllocationSettingData");
        string controllerPath;
        try
        {
            var scsiControllers = rasds
                .Where(r => Convert.ToInt32(r["ResourceType"] ?? 0, CultureInfo.InvariantCulture) == 6)
                .OrderBy(ParseControllerOrderKey)
                .ThenBy(c => c["InstanceID"] as string ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (drive.Number < 0 || drive.Number >= scsiControllers.Length)
                throw new IOException($"SCSI controller {drive.Number} is not present");
            controllerPath = scsiControllers[drive.Number].Path.Path;
        }
        finally
        {
            foreach (var r in rasds) r.Dispose();
        }

        string driveSubType = drive.Dvd
            ? "Microsoft:Hyper-V:Synthetic DVD Drive"
            : "Microsoft:Hyper-V:Synthetic Disk Drive";
        using var driveTemplate = GetDefaultSettingTemplate(scope, "Msvm_ResourceAllocationSettingData", driveSubType);
        driveTemplate["Parent"] = controllerPath;
        driveTemplate["AddressOnParent"] = drive.Location.ToString(CultureInfo.InvariantCulture);
        string drivePath = AddSingleResourceSetting(settings.Path.Path, driveTemplate);

        if (!string.IsNullOrWhiteSpace(drive.Path)) AttachMedia(scope, settings, drivePath, drive);
    }

    private static void AttachMedia(ManagementScope scope, ManagementObject settings, string drivePath, UpgradeDrive drive)
    {
        string mediaSubType = drive.Dvd
            ? "Microsoft:Hyper-V:Virtual CD/DVD Disk"
            : "Microsoft:Hyper-V:Virtual Hard Disk";
        using var mediaTemplate = GetDefaultSettingTemplate(scope, "Msvm_StorageAllocationSettingData", mediaSubType);
        mediaTemplate["Parent"] = drivePath;
        mediaTemplate["HostResource"] = new[] { drive.Path };
        if (!drive.Dvd)
        {
            if (!string.IsNullOrWhiteSpace(drive.Pool))
                mediaTemplate["PoolID"] = drive.Pool;
            if (drive.QoSPolicyID.HasValue && drive.QoSPolicyID.Value != Guid.Empty)
                mediaTemplate["StorageQoSPolicyID"] = drive.QoSPolicyID.Value.ToString("D");
            if (drive.MinimumIOPS > 0)
                mediaTemplate["IOPSReservation"] = drive.MinimumIOPS;
            if (drive.MaximumIOPS > 0)
                mediaTemplate["IOPSLimit"] = drive.MaximumIOPS;
        }
        _ = AddSingleResourceSetting(settings.Path.Path, mediaTemplate);
    }

    internal static int BootRank(ushort[] order, int device)
    {
        int index = Array.IndexOf(order, (ushort)device);
        if (index < 0 && device == 4) index = Array.IndexOf(order, (ushort)2);
        return index < 0 ? int.MaxValue : index;
    }

    internal static string[] MapBootOrder(ushort[] bios, UpgradeDrive[] slots, (string Path, string Firmware)[] sources, string[]? legacyMacs = null)
    {
        (int Class, int Slot) Priority(string firmware)
        {
            var match = Regex.Match(firmware, @"Scsi\((\d+),\s*(\d+)\)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int controller) &&
                int.TryParse(match.Groups[2].Value, out int location) &&
                slots.FirstOrDefault(d => d.Number == controller && d.Location == location) is { } drive)
                return (BootRank(bios, drive.BiosDevice), controller * 64 + location);
            if (firmware.Contains("MAC(", StringComparison.OrdinalIgnoreCase))
            {
                bool legacy = legacyMacs?.Any(mac => mac.Length > 0 && firmware.Contains("MAC(" + mac, StringComparison.OrdinalIgnoreCase)) == true;
                return (BootRank(bios, 3), legacy ? 0 : 1);
            }
            return (int.MaxValue, int.MaxValue);
        }
        return sources.OrderBy(s => Priority(s.Firmware)).Select(s => s.Path).ToArray();
    }

    internal static Task RestoreBootOrderAsync(Guid id, JsonElement hardware, UpgradeDrive[] slots) => Task.Run(() =>
    {
        var scope = ConnectScope();
        using var settings = GetRealizedSettings(scope, id);
        ApplyBootOrder(scope, settings, hardware, slots, verifyOnly: false);
    });

    private static void ApplyBootOrder(ManagementScope scope, ManagementObject settings, JsonElement hardware, UpgradeDrive[] slots, bool verifyOnly)
    {
        var current = settings["BootSourceOrder"] as string[] ?? [];
        var sources = current.Select(path =>
        {
            using var source = new ManagementObject(scope, new ManagementPath(path), null);
            source.Get();
            return (path, source["FirmwareDevicePath"] as string ?? "");
        }).ToArray();
        var order = MapBootOrder(hardware.GetProperty("BootOrder").Deserialize(CoreJson.Default.UInt16Array)!, slots, sources,
            hardware.GetProperty("LegacyMacs").Deserialize(CoreJson.Default.StringArray));
        if (current.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) return;
        if (verifyOnly) throw new IOException("Firmware boot order changed.");
        settings["BootSourceOrder"] = order;
        ModifySystemSettings(scope, settings);
    }

    private static bool IsTpmEnabled(ManagementObject settings)
    {
        using var collection = settings.GetRelated("Msvm_SecuritySettingData");
        foreach (ManagementObject sec in collection.Cast<ManagementObject>())
        {
            using (sec)
            {
                if (sec["TpmEnabled"] is true) return true;
            }
        }
        return false;
    }

    private static void EnsureTpmDisabled(ManagementScope scope, ManagementObject settings)
    {
        using var collection = settings.GetRelated("Msvm_SecuritySettingData");
        foreach (ManagementObject sec in collection.Cast<ManagementObject>())
        {
            using (sec)
            {
                if (sec["TpmEnabled"] is true)
                {
                    sec["TpmEnabled"] = false;
                    string xml = sec.GetText(TextFormat.CimDtd20);
                    InvokeServiceMethod("Msvm_SecurityService", "ModifySecuritySettings", args =>
                    {
                        args["SecuritySettingData"] = xml;
                    });
                }
            }
        }
    }

    private static ManagementObject GetDefaultSettingTemplate(ManagementScope scope, string className, string? resourceSubType = null)
    {
        string filter = resourceSubType == null
            ? "InstanceID LIKE '%Default%'"
            : $"InstanceID LIKE '%Default%' AND ResourceSubType='{resourceSubType}'";
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className} WHERE {filter}"));
        using var results = searcher.Get();
        return SingleObject(results.Cast<ManagementObject>().ToArray(), className + " template");
    }

    private static string AddSingleResourceSetting(string systemSettingsPath, ManagementObject template)
    {
        string xml = template.GetText(TextFormat.CimDtd20);
        var receipt = WmiApi.InvokeCreationAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "AddResourceSettings",
            template.ClassPath.ClassName, args =>
            {
                args["AffectedConfiguration"] = systemSettingsPath;
                args["ResourceSettings"] = new[] { xml };
            }).GetAwaiter().GetResult();
        if (receipt.OutcomeUnknown) throw new UpgradeUncertainException(receipt.Response.Error);
        WindowsUpgradeHost.Require(receipt.Response);
        return receipt.CreatedPaths.Single();
    }

    private static void ApplyFields(ManagementScope scope, ManagementObject row, JsonElement values, bool isSystem)
    {
        bool dirty = false;
        foreach (JsonProperty field in values.EnumerateObject())
        {
            if (!CanonicalJsonEquals(NormalizeWmiValue(row[field.Name]), field.Value))
            {
                SetWmiField(row, field.Name, field.Value);
                dirty = true;
            }
        }

        if (!dirty) return;
        if (isSystem) ModifySystemSettings(scope, row);
        else ModifyResourceSettings(scope, row);

        row.Get();
        foreach (JsonProperty field in values.EnumerateObject())
        {
            if (!CanonicalJsonEquals(NormalizeWmiValue(row[field.Name]), field.Value))
                throw new IOException("Setting readback: " + field.Name);
        }
    }

    private static void SetWmiField(ManagementObject row, string name, JsonElement value)
    {
        PropertyData prop = row.Properties[name];
        row[name] = ConvertJsonToCimValue(value, prop.Type, prop.IsArray);
    }

    private static object? ConvertJsonToCimValue(JsonElement value, CimType cimType, bool isArray)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (isArray)
        {
            var items = value.EnumerateArray().ToArray();
            return cimType switch
            {
                CimType.String or CimType.DateTime => items.Select(e => e.GetString() ?? string.Empty).ToArray(),
                CimType.UInt8 => items.Select(e => e.GetByte()).ToArray(),
                CimType.UInt16 => items.Select(e => e.GetUInt16()).ToArray(),
                CimType.UInt32 => items.Select(e => e.GetUInt32()).ToArray(),
                CimType.UInt64 => items.Select(e => e.GetUInt64()).ToArray(),
                CimType.SInt8 => items.Select(e => e.GetSByte()).ToArray(),
                CimType.SInt16 => items.Select(e => e.GetInt16()).ToArray(),
                CimType.SInt32 => items.Select(e => e.GetInt32()).ToArray(),
                CimType.SInt64 => items.Select(e => e.GetInt64()).ToArray(),
                CimType.Boolean => items.Select(e => e.GetBoolean()).ToArray(),
                _ => throw new IOException($"Unsupported CIM array setting: {cimType}")
            };
        }

        return cimType switch
        {
            CimType.String or CimType.DateTime => value.GetString() ?? string.Empty,
            CimType.Boolean => value.GetBoolean(),
            CimType.UInt8 => value.GetByte(),
            CimType.UInt16 => value.GetUInt16(),
            CimType.UInt32 => value.GetUInt32(),
            CimType.UInt64 => value.GetUInt64(),
            CimType.SInt8 => value.GetSByte(),
            CimType.SInt16 => value.GetInt16(),
            CimType.SInt32 => value.GetInt32(),
            CimType.SInt64 => value.GetInt64(),
            CimType.Real32 => value.GetSingle(),
            CimType.Real64 => value.GetDouble(),
            _ => throw new IOException($"Unsupported CIM setting: {cimType}")
        };
    }

    private static object? NormalizeWmiValue(object? value) => value switch
    {
        null => null,
        ManagementBaseObject or ManagementBaseObject[] => CimJsonValue.FromProperty(value),
        Array arr => arr.Cast<object?>().Select(NormalizeWmiValue).ToArray(),
        _ => value
    };

    private static bool CanonicalJsonEquals(object? left, JsonElement right)
    {
        string leftCanonical = CanonicalizeJson(JsonSerializer.SerializeToNode(left, CoreJson.Default.Object));
        string rightCanonical = CanonicalizeJson(JsonNode.Parse(right.GetRawText()));
        return string.Equals(leftCanonical, rightCanonical, StringComparison.Ordinal);
    }
}
