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

    private sealed record CheckpointSelection(
        string VirtualSystemType,
        string VirtualSystemIdentifier,
        string Path);

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
                    return ApiResponse<string>.Fail(Properties.Resources.VmExport_PathRequired);

                string vmDirectoryName = vmId.ToString("D").ToUpperInvariant();
                string exportDirectory = Path.Combine(destinationRoot, vmDirectoryName);
                string stagingDirectory = Path.Combine(destinationRoot, vmDirectoryName + ".partial");
                if (Directory.Exists(exportDirectory) || File.Exists(exportDirectory))
                    return ApiResponse<string>.Fail(
                        string.Format(Properties.Resources.VmExport_TargetExists, vmDirectoryName));
                if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
                    return ApiResponse<string>.Fail(
                        string.Format(Properties.Resources.VmExport_TargetExists, vmDirectoryName + ".partial"));

                if (checkpointMode != VmExportCheckpointMode.None
                    && excludedVirtualHardDiskIds.Count > 0)
                    return ApiResponse<string>.Fail(
                        Properties.Resources.VmExport_DiskSelectionCheckpointsConflict);

                using var service = WmiApi.GetVirtualSystemManagementService();
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null)
                    return ApiResponse<string>.Fail(Properties.Resources.Error_Net_VmNotFound);

                string? validatedCheckpointPath = null;
                if (checkpointMode == VmExportCheckpointMode.Single)
                {
                    if (string.IsNullOrWhiteSpace(selectedCheckpointPath))
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_CheckpointSelectionRequired);

                    var checkpointResult = await WmiApi.GetByPathAsync(
                        selectedCheckpointPath,
                        checkpoint => new CheckpointSelection(
                            checkpoint["VirtualSystemType"]?.ToString() ?? string.Empty,
                            checkpoint["VirtualSystemIdentifier"]?.ToString() ?? string.Empty,
                            checkpoint.Path.Path),
                        WmiScope.HyperV);

                    if (!checkpointResult.Success)
                        return ApiResponse<string>.Fail(
                            checkpointResult.Error,
                            checkpointResult.Code,
                            checkpointResult.ErrorSource);

                    if (!checkpointResult.HasData)
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_CheckpointUnavailable);

                    var checkpoint = checkpointResult.Data!;
                    string realizedVmId = vm["Name"]?.ToString() ?? string.Empty;
                    if (!string.Equals(
                            checkpoint.VirtualSystemType,
                            "Microsoft:Hyper-V:Snapshot:Realized",
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(
                            checkpoint.VirtualSystemIdentifier,
                            realizedVmId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return ApiResponse<string>.Fail(
                            Properties.Resources.VmExport_CheckpointUnavailable);
                    }

                    validatedCheckpointPath = checkpoint.Path;
                }

                bool effectiveIncludeStorage = checkpointMode == VmExportCheckpointMode.Single
                    || includeVirtualHardDisks;
                bool effectiveIncludeRuntime = checkpointMode == VmExportCheckpointMode.Single
                    || includeRuntimeState;

                var settingsResult = await WmiApi.CreateInstanceTextAsync(
                    "Msvm_VirtualSystemExportSettingData",
                    settings =>
                    {
                        if (validatedCheckpointPath != null
                            && !settings.HasProperty("SnapshotVirtualSystem"))
                        {
                            return ApiResponse.Fail(
                                Properties.Resources.VmExport_CheckpointSelectionUnsupported);
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

                        settings["CopyVmStorage"] = effectiveIncludeStorage;
                        settings["CopyVmRuntimeInformation"] = effectiveIncludeRuntime;
                        settings["CreateVmExportSubdirectory"] = true;

                        if (checkpointMode == VmExportCheckpointMode.None
                            && includeVirtualHardDisks
                            && excludedVirtualHardDiskIds.Count > 0)
                        {
                            if (!settings.HasProperty("ExcludedVirtualHardDisks"))
                            {
                                return ApiResponse.Fail(
                                    Properties.Resources.VmExport_SelectDisksUnsupported);
                            }

                            settings["ExcludedVirtualHardDisks"] =
                                excludedVirtualHardDiskIds.ToArray();

                            if (settings.HasProperty("DisableDifferentialOfIgnoredStorage"))
                                settings["DisableDifferentialOfIgnoredStorage"] = true;
                        }

                        // Running VMs can be exported either crash-consistently or with saved state.
                        if (settings.HasProperty("CaptureLiveState"))
                            settings["CaptureLiveState"] = effectiveIncludeRuntime ? (byte)1 : (byte)0;

                        return ApiResponse.Ok();
                    },
                    WmiScope.HyperV);

                if (!settingsResult.Success)
                    return ApiResponse<string>.Fail(
                        settingsResult.Error,
                        settingsResult.Code,
                        settingsResult.ErrorSource);

                string settingsXml = settingsResult.Data!;
                Directory.CreateDirectory(stagingDirectory);
                var result = await WmiApi.InvokeOnObjectAsync(
                    service,
                    "ExportSystemDefinition",
                    p =>
                    {
                        p["ComputerSystem"] = vm.Path.Path;
                        p["ExportDirectory"] = stagingDirectory;
                        p["ExportSettingData"] = settingsXml;
                    },
                    progress: progress,
                    timeout: TimeSpan.FromHours(24));

                if (!result.Success)
                    return ApiResponse<string>.Fail(
                        result.Error, result.Code, result.ErrorSource);

                progress?.Report(100);

                string? stagedExportDirectory = FindExportDirectory(
                    stagingDirectory,
                    vmId);
                if (stagedExportDirectory == null)
                    return ApiResponse<string>.Fail(Properties.Resources.VmExport_ConfigurationMissing);

                if (string.Equals(
                        stagedExportDirectory,
                        stagingDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(stagingDirectory, exportDirectory);
                }
                else
                {
                    Directory.Move(stagedExportDirectory, exportDirectory);
                    if (!Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
                        Directory.Delete(stagingDirectory);
                }

                bool hasConfiguration = Directory.EnumerateFiles(
                    exportDirectory, "*.vmcx", SearchOption.AllDirectories).Any();
                return hasConfiguration
                    ? ApiResponse<string>.Ok(exportDirectory)
                    : ApiResponse<string>.Fail(Properties.Resources.VmExport_ConfigurationMissing);
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

    private static string? FindExportDirectory(string stagingDirectory, Guid vmId)
    {
        string expectedConfigurationName = vmId.ToString("D") + ".vmcx";
        string? configurationPath = Directory.EnumerateFiles(
                stagingDirectory,
                "*.vmcx",
                SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                expectedConfigurationName,
                StringComparison.OrdinalIgnoreCase))
            ?? Directory.EnumerateFiles(
                    stagingDirectory,
                    "*.vmcx",
                    SearchOption.AllDirectories)
                .FirstOrDefault();

        if (configurationPath == null)
            return null;

        string relativePath = Path.GetRelativePath(stagingDirectory, configurationPath);
        int separatorIndex = relativePath.IndexOf(Path.DirectorySeparatorChar);
        if (separatorIndex < 0)
            return stagingDirectory;

        string firstSegment = relativePath[..separatorIndex];
        return string.Equals(
            firstSegment,
            "Virtual Machines",
            StringComparison.OrdinalIgnoreCase)
            ? stagingDirectory
            : Path.Combine(stagingDirectory, firstSegment);
    }

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
