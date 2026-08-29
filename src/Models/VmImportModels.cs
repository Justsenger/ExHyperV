using System.Collections.ObjectModel;
using System.Globalization;
using ExHyperV.Properties;

namespace ExHyperV.Models;

public enum VmImportSourceKind
{
    Folder,
    Zip
}

public enum VmImportPlacementMode
{
    HostDirectories,
    ExistingDirectory
}

public sealed class VmImportDiskPreview
{
    public string Name { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string Controller { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public ulong VirtualSize { get; init; }
    public long ActualSize { get; init; }
    public string? ParentPath { get; init; }
    public bool Exists { get; init; }

    public string SlotText => string.IsNullOrWhiteSpace(Controller) ? "—" : Controller;
    public string KindText => string.Join(" · ", new[] { Format, Type }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string VirtualSizeText => FormatBytes(VirtualSize);
    public string ActualSizeText => FormatBytes((ulong)Math.Max(0, ActualSize));

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

public sealed class VmImportNetworkPreview
{
    public string Name { get; init; } = string.Empty;
    public string OriginalSwitchName { get; init; } = string.Empty;
    public bool OriginalSwitchAvailable { get; init; }
    public string AllocationPath { get; init; } = string.Empty;
    public ObservableCollection<string> AvailableSwitches { get; init; } = new();
    public string SelectedSwitch { get; set; } = string.Empty;
}

public sealed class VmImportCheckpointPreview
{
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime Created { get; init; }
    public int Depth { get; set; }
    public string BranchText => Depth <= 0 ? string.Empty : new string(' ', (Depth - 1) * 3) + "└─";
    public string CreatedText => Created == DateTime.MinValue
        ? string.Empty
        : Created.ToString("g", Resources.Culture ?? CultureInfo.CurrentUICulture);
}

public sealed class VmImportPreview
{
    public string Name { get; init; } = string.Empty;
    public Guid OriginalGuid { get; init; }
    public Guid PlannedGuid { get; init; }
    public bool GeneratedNewGuid => OriginalGuid != Guid.Empty && PlannedGuid != Guid.Empty && OriginalGuid != PlannedGuid;
    public string GuidText => PlannedGuid.ToString("D");
    public int Generation { get; init; }
    public string GenerationText => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_GenerationFormat,
        Generation);
    public string ConfigurationVersion { get; init; } = string.Empty;
    public DateTime Created { get; init; }
    public string CreatedText => Created == DateTime.MinValue
        ? "—"
        : Created.ToString("g", Resources.Culture ?? CultureInfo.CurrentUICulture);
    public string Notes { get; init; } = string.Empty;
    public string OsType { get; init; } = "Windows";
    public int ProcessorCount { get; init; }
    public bool DynamicMemory { get; init; }
    public ulong StartupMemoryMb { get; init; }
    public ulong MinimumMemoryMb { get; init; }
    public ulong MaximumMemoryMb { get; init; }
    public bool HasSavedState { get; init; }
    public string SavedStateText => HasSavedState ? Resources.VmImport_Yes : Resources.VmImport_No;
    public ObservableCollection<VmImportDiskPreview> Disks { get; init; } = new();
    public ObservableCollection<VmImportNetworkPreview> Networks { get; init; } = new();
    public ObservableCollection<VmImportCheckpointPreview> Checkpoints { get; init; } = new();
    public ObservableCollection<string> CompatibilityIssues { get; init; } = new();

    public string MemoryText => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        DynamicMemory ? Resources.VmImport_DynamicMemoryFormat : Resources.VmImport_StaticMemoryFormat,
        StartupMemoryMb,
        MinimumMemoryMb,
        MaximumMemoryMb);
    public string StartupMemoryText => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_StaticMemoryFormat,
        StartupMemoryMb);
    public string ConfigSummary
    {
        get
        {
            string diskPart = Disks.Count == 0
                ? Resources.Common_NoDisk
                : string.Join(" + ", Disks
                    .Select(d => d.VirtualSize / 1073741824.0)
                    .OrderByDescending(size => size)
                    .Select(size => size >= 1 ? $"{size:0.#} GB" : $"{size * 1024:0} MB"));

            return string.Format(
                Resources.Culture ?? CultureInfo.CurrentUICulture,
                Resources.Format_VmSummary,
                ProcessorCount,
                StartupMemoryMb / 1024.0,
                diskPart);
        }
    }
    public string ProcessorMemorySummary => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_ProcessorMemoryFormat,
        ProcessorCount,
        StartupMemoryMb / 1024.0);
    public string DiskSummary => Disks.Count == 0 ? "0" : Disks.Count.ToString();
    public string NetworkSummary => Networks.Count == 0 ? "0" : Networks.Count.ToString();
    public string CheckpointSummary => Checkpoints.Count == 0 ? "0" : Checkpoints.Count.ToString();
}
