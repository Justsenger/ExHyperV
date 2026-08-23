using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public enum VmExportCheckpointMode
{
    None,
    All,
    Single
}

public static class VmExportService
{
    public sealed record VirtualHardDiskInfo(string InstanceId, string Path);
    public sealed record CheckpointInfo(
        string Id,
        string? ParentId,
        string Name,
        DateTime CreatedDate,
        string Path);

    private sealed record VirtualHardDiskAllocation(
        string InstanceId,
        string ResourceSubType,
        string[] HostResources);

    public static async Task<ApiResponse<List<VirtualHardDiskInfo>>> GetVirtualHardDisksAsync(
        Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(
                Properties.Resources.Error_Net_VmNotFound);

        string escapedVmId = WmiApi.Escape(vmId.ToString("D"));
        var outer = await WmiApi.WithFirstAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE VirtualSystemIdentifier = '{escapedVmId}' " +
            $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            settings => WmiApi.QueryRelatedCimAsync(
                settings,
                "Msvm_VirtualSystemSettingDataComponent",
                "Msvm_StorageAllocationSettingData",
                "GroupComponent",
                "PartComponent",
                obj => new VirtualHardDiskAllocation(
                    obj["InstanceID"]?.ToString() ?? string.Empty,
                    obj["ResourceSubType"]?.ToString() ?? string.Empty,
                    obj["HostResource"] as string[]
                        ?? (obj["HostResource"] is string path
                            ? new[] { path }
                            : Array.Empty<string>())),
                WmiScope.HyperV),
            WmiScope.HyperV);

        if (!outer.Success)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(
                outer.Error, outer.Code, outer.ErrorSource);

        if (!outer.HasData)
            return ApiResponse<List<VirtualHardDiskInfo>>.Empty();

        var inner = outer.Data!;
        if (!inner.Success)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(
                inner.Error, inner.Code, inner.ErrorSource);

        var disks = (inner.Data ?? new List<VirtualHardDiskAllocation>())
            .Where(item => string.Equals(
                item.ResourceSubType,
                "Microsoft:Hyper-V:Virtual Hard Disk",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => new VirtualHardDiskInfo(
                item.InstanceId,
                item.HostResources.FirstOrDefault() ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.InstanceId)
                        && !string.IsNullOrWhiteSpace(item.Path))
            .ToList();

        return ApiResponse<List<VirtualHardDiskInfo>>.Ok(disks);
    }

    public static async Task<ApiResponse<List<CheckpointInfo>>> GetCheckpointsAsync(
        Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<List<CheckpointInfo>>.Fail(
                Properties.Resources.Error_Net_VmNotFound);

        string escapedVmId = WmiApi.Escape(vmId.ToString("D"));
        var checkpointsResult = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE VirtualSystemIdentifier = '{escapedVmId}' " +
            "AND VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'",
            obj => new CheckpointInfo(
                obj["InstanceID"]?.ToString() ?? string.Empty,
                ExtractInstanceId(obj["Parent"]?.ToString()),
                obj["ElementName"]?.ToString() ?? string.Empty,
                obj["CreationTime"] is string creationTime
                    ? ManagementDateTimeConverter.ToDateTime(creationTime)
                    : DateTime.MinValue,
                obj.Path.Path),
            WmiScope.HyperV);

        if (!checkpointsResult.Success)
            return ApiResponse<List<CheckpointInfo>>.Fail(
                checkpointsResult.Error,
                checkpointsResult.Code,
                checkpointsResult.ErrorSource);

        return ApiResponse<List<CheckpointInfo>>.Ok(
            checkpointsResult.Data ?? new List<CheckpointInfo>());
    }

    public static Task<ApiResponse<string>> ExportAsync(
        Guid vmId,
        string vmName,
        string destinationRoot,
        bool includeVirtualHardDisks,
        IReadOnlyCollection<string> excludedVirtualHardDiskIds,
        VmExportCheckpointMode checkpointMode,
        string? selectedCheckpointPath,
        bool includeRuntimeState,
        IProgress<int>? progress = null)
        => Task.Run(async () =>
        {
            try
            {
                if (vmId == Guid.Empty)
                    return ApiResponse<string>.Fail(Properties.Resources.Error_Net_VmNotFound);

                if (!Directory.Exists(destinationRoot))
                    return ApiResponse<string>.Fail("The export destination does not exist.");

                string exportDirectory = Path.Combine(destinationRoot, vmName);
                if (Directory.Exists(exportDirectory) || File.Exists(exportDirectory))
                    return ApiResponse<string>.Fail("The export target already exists.");

                if (checkpointMode != VmExportCheckpointMode.None
                    && excludedVirtualHardDiskIds.Count > 0)
                    return ApiResponse<string>.Fail(
                        Properties.Resources.VmExport_DiskSelectionCheckpointsConflict);

                using var service = WmiApi.GetVirtualSystemManagementService();
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null)
                    return ApiResponse<string>.Fail(Properties.Resources.Error_Net_VmNotFound);

                using var settingClass = new ManagementClass(
                    service.Scope,
                    new ManagementPath("Msvm_VirtualSystemExportSettingData"),
                    null);
                using var settings = settingClass.CreateInstance();

                string? validatedCheckpointPath = null;
                if (checkpointMode == VmExportCheckpointMode.Single)
                {
                    if (string.IsNullOrWhiteSpace(selectedCheckpointPath))
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_CheckpointSelectionRequired);

                    using var checkpoint = new ManagementObject(
                        service.Scope,
                        new ManagementPath(selectedCheckpointPath),
                        null);
                    checkpoint.Get();

                    string checkpointType = checkpoint["VirtualSystemType"]?.ToString() ?? string.Empty;
                    string checkpointVmId = checkpoint["VirtualSystemIdentifier"]?.ToString() ?? string.Empty;
                    string vmId = vm["Name"]?.ToString() ?? string.Empty;
                    if (!string.Equals(
                            checkpointType,
                            "Microsoft:Hyper-V:Snapshot:Realized",
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(checkpointVmId, vmId, StringComparison.OrdinalIgnoreCase))
                    {
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_CheckpointUnavailable);
                    }

                    if (!settings.HasProperty("SnapshotVirtualSystem"))
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_CheckpointSelectionUnsupported);

                    validatedCheckpointPath = checkpoint.Path.Path;
                }

                settings["CopySnapshotConfiguration"] = checkpointMode switch
                {
                    VmExportCheckpointMode.All => (byte)0,
                    VmExportCheckpointMode.None => (byte)1,
                    VmExportCheckpointMode.Single => (byte)2,
                    _ => (byte)1
                };

                if (validatedCheckpointPath != null)
                    settings["SnapshotVirtualSystem"] = validatedCheckpointPath;

                bool effectiveIncludeStorage = checkpointMode == VmExportCheckpointMode.Single
                    || includeVirtualHardDisks;
                bool effectiveIncludeRuntime = checkpointMode == VmExportCheckpointMode.Single
                    || includeRuntimeState;

                settings["CopyVmStorage"] = effectiveIncludeStorage;
                settings["CopyVmRuntimeInformation"] = effectiveIncludeRuntime;
                settings["CreateVmExportSubdirectory"] = true;

                if (checkpointMode == VmExportCheckpointMode.None
                    && includeVirtualHardDisks
                    && excludedVirtualHardDiskIds.Count > 0)
                {
                    if (!settings.HasProperty("ExcludedVirtualHardDisks"))
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_SelectDisksUnsupported);

                    settings["ExcludedVirtualHardDisks"] =
                        excludedVirtualHardDiskIds.ToArray();

                    if (settings.HasProperty("DisableDifferentialOfIgnoredStorage"))
                        settings["DisableDifferentialOfIgnoredStorage"] = true;
                }

                // Running VMs can be exported either crash-consistently or with saved state.
                if (settings.HasProperty("CaptureLiveState"))
                    settings["CaptureLiveState"] = effectiveIncludeRuntime ? (byte)1 : (byte)0;

                string settingsXml = settings.GetText(TextFormat.CimDtd20);
                var result = await WmiApi.InvokeOnObjectAsync(
                    service,
                    "ExportSystemDefinition",
                    p =>
                    {
                        p["ComputerSystem"] = vm.Path.Path;
                        p["ExportDirectory"] = destinationRoot;
                        p["ExportSettingData"] = settingsXml;
                    },
                    progress: progress,
                    timeout: TimeSpan.FromHours(24));

                if (!result.Success)
                    return ApiResponse<string>.Fail(
                        result.Error, result.Code, result.ErrorSource);

                progress?.Report(100);

                bool hasConfiguration = Directory.Exists(exportDirectory)
                    && Directory.EnumerateFiles(
                        exportDirectory, "*.vmcx", SearchOption.AllDirectories).Any();
                return hasConfiguration
                    ? ApiResponse<string>.Ok(exportDirectory)
                    : ApiResponse<string>.Fail("Hyper-V completed the export but no virtual machine configuration was found.");
            }
            catch (ManagementException ex)
            {
                return ApiResponse<string>.Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiResponse<string>.Fail(
                    ex.Message, 5, ApiErrorSource.Win32, ex);
            }
            catch (Exception ex)
            {
                return ApiResponse<string>.Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });

    private static string? ExtractInstanceId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        Match match = Regex.Match(
            path,
            "InstanceID=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
